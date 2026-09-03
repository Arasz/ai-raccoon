using System.Globalization;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Setup.Cli;
using Dapper;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     WP7-T1 (docs/plans/2026-08-16-bank-open-cost-implementation.md §6): a CLI process must not
///     write the bank. Each command runs as a real <c>ai-raccoon</c> OS process against a scratch
///     data root, with a connection held open across it watching for a committed write transaction
///     — INSERT, UPDATE, DELETE and DDL alike, which is the criterion as the plan states it.
///     <para />
///     The precondition is an existing, current bank: creating one legitimately writes it. The
///     fixture opens it once so every command below meets a bank that is already stamped.
///     <para />
///     Scope here is the read verbs, and it stays that way even with the transport wired
///     (ADR-0075 §5.3): a write verb still has to end in a committed write <em>somewhere</em> on the
///     same bank file, and <see cref="BankCommitObserver" /> watches the file, not the process that
///     touched it — it cannot tell "the CLI wrote directly" from "the CLI's HTTP call made the
///     server write", so it is not the seam that proves write-exclusivity for write verbs.
///     <see cref="AWriteCommand_IsSeen_ProvingTheAssertionCanFail" /> below still shows a write
///     verb committing — now via the auto-started server rather than the CLI itself — which is
///     what keeps <see cref="ReadCommand_CommitsNothingToTheBank" /> a real assertion rather than one
///     that could only ever pass. The actual "CLI never writes directly" guarantee for write verbs is
///     proved elsewhere, by construction: <c>CliWriteOptOutsTests</c> pins the opt-out list,
///     <c>AppRunnerSettingsRoutingTests</c> pins that a non-opted-out command never resolves
///     <c>SqliteSettingsStore</c>, and <c>ConfigCommands</c> never takes <c>ISqliteConnectionFactory</c>
///     for any settings verb.
///     <para />
///     The command list is derived from the command tree, so a read verb added later is covered
///     without an edit here.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
public sealed class CliBankWriteTests : IAsyncLifetime
{
    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(60);

    /// <summary>
    ///     The jobs whose ledger rows the fixture stamps so no command's auto-started server finds
    ///     anything due (ADR-0070: <c>IsDue</c> is true while a job's row is missing). Kept in step
    ///     with the DI-registered job list by <c>CliBankWriteLedgerDriftTests</c> — a job added to
    ///     the list without a row here fails PR CI, not the next nightly.
    /// </summary>
    internal static readonly string[] MaintenanceLedgerNames =
    [
        ChunkBackfillJob.JobName,
        Vec0ReclaimJob.JobName,
        VacuumJob.JobName,
        MetricsRetentionJob.JobName,
        ModelMigrationJob.JobName,
        ChunkIndexRepairJob.JobName,
        ReingestRepairJob.JobName,
        ProjectIdsRepairJob.JobName, // Ledger — ledger-drifts : --filter CliBankWriteLedgerDriftTests/CliBankWriteTests : DI job list.
        PromotionQueuePruneJob.JobName,
        PendingEmbedJob.JobName,
        CodeReindexJob.JobName
    ];

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-cli-writes");
    private ISqliteConnectionFactory _factory = null!;
    private LoopbackPort _portLease = null!;
    private int _port;

    /// <summary>Commands that only report; none of them has any business writing the bank.</summary>
    public static TheoryData<string, string[]> ReadCommands()
    {
        var data = new TheoryData<string, string[]>();
        foreach (var (path, argv) in SettingsCommandTreeTests.SettingsLeaves)
        {
            if (path.EndsWith(" show", StringComparison.Ordinal) || path.EndsWith(" list", StringComparison.Ordinal))
            {
                data.Add(path, argv);
            }
        }

        data.Add("noise entries", ["noise", "entries"]);
        data.Add("watch registered", ["watch", "registered"]);
        data.Add("extract prune", ["extract", "prune"]);
        data.Add("repair chunk-index", ["repair", "chunk-index"]);
        data.Add("repair reingest", ["repair", "reingest"]);
        data.Add("repair project-ids", ["repair", "project-ids"]);
        return data;
    }

    /// <summary>
    ///     Every leaf CliCommandTree exposes with an --apply option, derived and cross-checked
    ///     against <c>SettingsCommandTreeTests.ApplyLeaves_MatchCliBankWriteTestsCoverage</c> — a new
    ///     --apply leaf added without a row below fails that test, not this one, so the gate cannot
    ///     silently stay a no-op the way it did for `repair` before this task.
    /// </summary>
    public static readonly string[] ApplyCommandPaths = ["extract prune", "repair chunk-index", "repair reingest", "repair project-ids"];

    /// <summary>
    ///     The --apply form of each leaf above, paired with the outbox table its request must (and
    ///     must only) land in. Asserted via <see cref="BankContent.Changed" /> table digests, not
    ///     <c>PRAGMA data_version</c> (<see cref="BankCommitObserver" />) — a data_version read
    ///     cannot distinguish "the CLI's request landed in the outbox table" (correct) from "the CLI
    ///     synchronously mutated the domain table itself" (the ADR-0075 violation this gate exists to
    ///     catch), because both commit a write to the same file.
    /// </summary>
    public static TheoryData<string, string[], string> ApplyCommands() => new()
    {
        { "extract prune --apply", ["extract", "prune", "--apply"], "promotion_queue_prune_requests" },
        { "repair chunk-index --apply", ["repair", "chunk-index", "--apply"], "repair_requests" },
        { "repair reingest --apply", ["repair", "reingest", "--apply"], "repair_requests" },
        // Ledger — project-ids-apply-unlisted : --filter ApplyCommand_OnlyCommitsAnOutboxRequest : jsaa-cluster seed below.
        { "repair project-ids --apply", ["repair", "project-ids", "--apply"], "repair_requests" }
    };

    public async ValueTask InitializeAsync()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options,
            new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
                [new EnvEncryptionKeyProvider()]));

        // Creating the bank is itself a write; do it here so the assertions below meet a bank that
        // is already stamped and current.
        await using var _ = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        // Stamp every maintenance job's ledger row as "already run, just now". A missing row makes
        // the job DUE (MaintenanceJobRunner.IsDue is true while last_run_at is NULL), and every
        // command's auto-started server runs a startup pass that re-stamps whatever is missing —
        // inside the command's observed window on a starved runner (2026-08-20 nightly: 5 of 6
        // failures were exactly that). With the rows present, nothing is due on this bank and the
        // pass writes nothing, deterministically — no dependence on the warm-up pass completing
        // before the warm-up server exits.
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var name in MaintenanceLedgerNames)
            {
                await connection.ExecuteAsync(
                    """
                    INSERT INTO maintenance_jobs (name, last_run_at, run_count) VALUES (@name, @now, 1)
                    ON CONFLICT(name) DO NOTHING
                    """,
                    new { name, now });
            }
        }

        _portLease = LoopbackPort.Reserve();
        _port = _portLease.Port;
        _portLease.ReleaseForBind();

        // Warm-up: every settings command now auto-starts a full server (ADR-0075 §5.1), and
        // BankMaintenanceHostedService runs an unrelated startup checkpoint pass the instant it
        // does (BankMaintenanceHostedService.cs:73), racily, in the background. Absorbing that here
        // and waiting for it to settle keeps it from being mistaken for a write the verb under test
        // made — the assertions below only care about what the verb itself commits.
        var warmup = await RaccoonProcess.RunAsync(
            ["--data-root", _dataRoot, "--port", _port.ToString(CultureInfo.InvariantCulture), "settings", "sweep", "show"],
            HardCap, TestContext.Current.CancellationToken);
        warmup.ExitCode.ShouldBe(0, $"warm-up failed; stderr: {warmup.Stderr}");
        await WaitForBankToSettleAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>Polls until two consecutive snapshots match, so the startup maintenance pass's own
    /// write (racy relative to the server answering its first request) is not still in flight.</summary>
    private async Task WaitForBankToSettleAsync(CancellationToken cancellationToken)
    {
        var previous = await BankContent.SnapshotAsync(_factory, cancellationToken);
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            var current = await BankContent.SnapshotAsync(_factory, cancellationToken);
            if (BankContent.Changed(previous, current).Count == 0)
            {
                return;
            }

            previous = current;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await RaccoonBackendCleanup.ShutdownIfRunningAsync(_dataRoot, _port, CancellationToken.None);
        TestData.DeleteTempRoot(_dataRoot);
    }

    [RetryTheory]
    [MemberData(nameof(ReadCommands))]
    public async Task ReadCommand_CommitsNothingToTheBank(string label, string[] argv)
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        await using var observer = await BankCommitObserver.OpenAsync(_factory, TestContext.Current.CancellationToken);
        var before = await BankContent.SnapshotAsync(_factory, TestContext.Current.CancellationToken);

        var run = await RaccoonProcess.RunAsync(
            ["--data-root", _dataRoot, "--port", _port.ToString(CultureInfo.InvariantCulture), .. argv],
            HardCap, TestContext.Current.CancellationToken);
        run.ExitCode.ShouldBe(0, $"'{label}' failed; stderr: {run.Stderr}");

        var committed = await observer.HasCommittedSinceMarkAsync(TestContext.Current.CancellationToken);
        var after = await BankContent.SnapshotAsync(_factory, TestContext.Current.CancellationToken);
        committed.ShouldBeFalse(
            $"'{label}' committed a write to the bank; tables changed: {string.Join(", ", BankContent.Changed(before, after))}");
    }

    /// <summary>
    ///     The seam's own proof: a write verb through the same harness is seen. Without this, the
    ///     assertion above would be indistinguishable from one that can only pass. Under
    ///     write-exclusivity the commit still happens — now performed by the server this write verb
    ///     auto-starts, not by the CLI process itself, but a committed write to the same file either way.
    /// </summary>
    [RetryFact]
    public async Task AWriteCommand_IsSeen_ProvingTheAssertionCanFail()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        await using var observer = await BankCommitObserver.OpenAsync(_factory, TestContext.Current.CancellationToken);

        var run = await RaccoonProcess.RunAsync(
            ["--data-root", _dataRoot, "--port", _port.ToString(CultureInfo.InvariantCulture),
                "settings", "sweep", "threshold", "set", "0.42"],
            HardCap, TestContext.Current.CancellationToken);
        run.ExitCode.ShouldBe(0, $"the writer must exit cleanly; stderr: {run.Stderr}");

        (await observer.HasCommittedSinceMarkAsync(TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    /// <summary>
    ///     The most important assertion in this file: an --apply verb's own synchronous call must
    ///     commit only its outbox request, never the domain table the eventual mutation targets —
    ///     that table changes later, inside the server's maintenance loop, not inside this CLI
    ///     invocation. Each row is seeded with data the mutation can actually act on (item 3 of the
    ///     CLI-off-the-bank task): an empty bank would let `extract prune --apply` short-circuit
    ///     before ever reaching the outbox write, making the assertion a no-op that could not fail.
    ///     <para />
    ///     The changed-set check is <see cref="ApplyCommitAssertions.IsHonestApplyCommit" />, not
    ///     exact equality: on a loaded machine the 15 s maintenance poll can consume the request
    ///     inside the command's window (2026-08-20 nightly F5), which legitimately changes the
    ///     target table plus maintenance_jobs — the composite requires the request row to exist,
    ///     nothing outside the allowed set to change, and any domain mutation to carry a
    ///     maintenance stamp.
    /// </summary>
    [RetryTheory]
    [MemberData(nameof(ApplyCommands))]
    public async Task ApplyCommand_OnlyCommitsAnOutboxRequest_NeverTheDomainTableDirectly(
        string label, string[] argv, string expectedOutboxTable)
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        await SeedForAsync(label, TestContext.Current.CancellationToken);
        var before = await BankContent.SnapshotAsync(_factory, TestContext.Current.CancellationToken);

        var run = await RaccoonProcess.RunAsync(
            ["--data-root", _dataRoot, "--port", _port.ToString(CultureInfo.InvariantCulture), .. argv],
            HardCap, TestContext.Current.CancellationToken);
        run.ExitCode.ShouldBe(0, $"'{label}' failed; stderr: {run.Stderr}");

        var requestRowExists = await RequestRowExistsAsync(label, TestContext.Current.CancellationToken);
        var after = await BankContent.SnapshotAsync(_factory, TestContext.Current.CancellationToken);
        var changed = BankContent.Changed(before, after);
        ApplyCommitAssertions.IsHonestApplyCommit(
                changed, expectedOutboxTable, TargetDomainTablesFor(label), requestRowExists)
            .ShouldBeTrue(
                $"'{label}' must commit only its outbox request ({expectedOutboxTable}) — any domain-table change must carry a maintenance_jobs stamp; changed: {string.Join(", ", changed)}");
    }

    /// <summary>
    ///     The composite's own proof: a rogue synchronous domain write (the ADR-0075 violation the
    ///     gate exists to catch) must fail it — no request row was ever written and no maintenance
    ///     stamp follows. Without this, the composite would be indistinguishable from one that can
    ///     only pass.
    /// </summary>
    [RetryFact]
    public async Task ADirectDomainWrite_WithoutRequest_FailsTheCompositeAssertion()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        await SeedOrphanedPromotionQueueRowAsync(TestContext.Current.CancellationToken);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("DELETE FROM promotion_queue"); // the rogue's synchronous write

        var requestRowExists = await RequestRowExistsAsync("extract prune --apply", TestContext.Current.CancellationToken);
        ApplyCommitAssertions.IsHonestApplyCommit(
                ["promotion_queue"], "promotion_queue_prune_requests",
                TargetDomainTablesFor("extract prune --apply"), requestRowExists)
            .ShouldBeFalse("a direct domain write with no outbox request must fail the composite");
    }

    private static IReadOnlySet<string> TargetDomainTablesFor(string label) => label switch
    {
        "extract prune --apply" => new HashSet<string> { "promotion_queue" },
        "repair chunk-index --apply" => new HashSet<string> { "entries" },
        "repair reingest --apply" => new HashSet<string> { "entries" },
        // Every id-keyed surface the fold rewrites (ProjectIdsRepair step order): an in-window
        // relay drain may legitimately touch any of them plus its maintenance_jobs stamp.
        // Ledger — dropped-surface-omitted : --filter ApplyCommand_OnlyCommitsAnOutboxRequest : recipe lists every surface the fold writes.
        "repair project-ids --apply" => new HashSet<string>
        {
            "entries", "code_entries", "promotion_queue", "promotion_discards", "search_quality",
            "watches", "watch_files", "watch_digest_claims", "settings", "projects", "sync_tombstones"
        },
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, "no target-table recipe for this --apply leaf")
    };

    /// <summary>
    ///     Whether the CLI's outbox request row exists. The request rows persist after the relay
    ///     consumes them (Finish* are UPDATEs), so existence — not "still pending" — is the claim:
    ///     a <c>finished_at</c> condition would fail the moment the 15 s poll consumes the request
    ///     in-window, recreating the F5-tail flake for the repair verbs.
    /// </summary>
    private async Task<bool> RequestRowExistsAsync(string label, CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenBankAsync(cancellationToken);
        return label switch
        {
            "extract prune --apply" => await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT count(*) FROM promotion_queue_prune_requests WHERE id = 1",
                    cancellationToken: cancellationToken)) > 0,
            "repair chunk-index --apply" => await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT count(*) FROM repair_requests WHERE kind = 'chunk-index'",
                    cancellationToken: cancellationToken)) > 0,
            "repair reingest --apply" => await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT count(*) FROM repair_requests WHERE kind = 'reingest'",
                    cancellationToken: cancellationToken)) > 0,
            "repair project-ids --apply" => await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "SELECT count(*) FROM repair_requests WHERE kind = 'project-ids'",
                    cancellationToken: cancellationToken)) > 0,
            _ => throw new ArgumentOutOfRangeException(nameof(label), label, "no request-row recipe for this --apply leaf")
        };
    }

    private Task SeedForAsync(string label, CancellationToken cancellationToken) => label switch
    {
        "extract prune --apply" => SeedOrphanedPromotionQueueRowAsync(cancellationToken),
        "repair chunk-index --apply" => SeedMisorderedChunkIndexSourceAsync(cancellationToken),
        "repair reingest --apply" => SeedStaleReingestSourceAsync(cancellationToken),
        "repair project-ids --apply" => SeedProjectIdsLoserRowAsync(cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(label), label, "no seeding recipe for this --apply leaf")
    };

    /// <summary>A promotion_queue row whose hash has no backing entries row — the same "orphan" shape PromotionQueueInvalidationTests uses.</summary>
    private async Task SeedOrphanedPromotionQueueRowAsync(CancellationToken cancellationToken)
    {
        var queueStore = new SqlitePromotionQueueStore(_factory, TimeProvider.System);
        var hash = "orphan-" + Guid.NewGuid().ToString("N");
        await queueStore.UpsertAsync("acme", [new QueueCandidate(hash, $"{hash}.md", "gone", null, 1.0, ["organic-write"])],
            cancellationToken);
    }

    /// <summary>A row whose hash a chunker rescan cannot reproduce — ReingestRepairJobTests.SeedStaleFileAsync's technique, direct-SQL since this seeds a separate OS process's bank.</summary>
    private async Task SeedStaleReingestSourceAsync(CancellationToken cancellationToken)
    {
        var file = Path.Combine(_dataRoot, "stale.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two", cancellationToken);
        await using var connection = await _factory.OpenBankAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO entries (hash, path, value, source_file, scope, project_id, created_at, updated_at, embed_state, chunk_index, total_chunks)
            VALUES (@hash, @path, @value, @sourceFile, 'project', 'acme', 0, 0, 'embedded', -1, 1)
            """,
            new { hash = "stale-" + Guid.NewGuid().ToString("N"), path = file, value = "stale content", sourceFile = file });
    }

    /// <summary>Three chunks seeded out of the order a fresh chunk of the file would produce — ChunkIndexRepairJobTests.OpenSeededAsync's technique.</summary>
    private async Task SeedMisorderedChunkIndexSourceAsync(CancellationToken cancellationToken)
    {
        var file = Path.Combine(_dataRoot, "doc.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", cancellationToken);
        await using var connection = await _factory.OpenBankAsync(cancellationToken);
        foreach (var (value, chunkIndex) in new[] { ("para one", 0), ("para three", 1), ("para two", 2) })
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO entries (hash, path, value, source_file, scope, project_id, created_at, updated_at, embed_state, chunk_index, total_chunks)
                VALUES (@hash, @path, @value, @sourceFile, 'project', 'acme', 0, 0, 'embedded', @chunkIndex, 3)
                """,
                new { hash = ContentHash.Of(file, value), path = file, value, sourceFile = file, chunkIndex });
        }
    }

    /// <summary>A labeled loser-id row plus its queue leg — the jsaa-cluster shape the project-ids diagnose reports on.</summary>
    // Ledger — empty-bank-short-circuits-outbox-write : --filter ApplyCommand_OnlyCommitsAnOutboxRequest : loser + winner rows (an empty bank would let --apply short-circuit before the outbox write).
    private async Task SeedProjectIdsLoserRowAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenBankAsync(cancellationToken);
        await connection.ExecuteAsync(
            """
            INSERT INTO entries (hash, path, value, source_file, scope, project_id, context_label, created_at, updated_at, embed_state)
            VALUES ('loser-1', 'loser-1', 'loser content', 'seed.md', 'project', 'job-search-ai-assistant', 'ctx-a', 0, 0, 'pending'),
                   ('winner-1', 'winner-1', 'winner content', 'seed.md', 'project', 'jsaa', 'ctx-a', 0, 0, 'pending')
            """);
        await connection.ExecuteAsync(
            """
            INSERT INTO promotion_queue (project_id, hash, value, score, created_at, updated_at)
            VALUES ('job-search-ai-assistant', 'loser-q1', 'loser-q1', 0.7, 0, 0),
                   ('jsaa', 'winner-q1', 'winner-q1', 0.9, 0, 0)
            """);
    }
}
