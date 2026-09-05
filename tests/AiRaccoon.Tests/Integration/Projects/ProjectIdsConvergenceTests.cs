using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Projects;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using AiRaccoon.Tests.Unit.Projects;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Projects;

/// <summary>
///     Package G integration (D6 end-to-end): the full stack — real CLI run-until-fixed loop
///     (F) → real server store (ADR-0075) → real repair job (B/C/D applier + persist) → real
///     embed drain — driven against one scratch bank per test. The only collapsed seam is the
///     ~15s maintenance poll: the live-server decorator applies each committed request inline
///     (same job, same request row, same census the poll would use) instead of on a timer —
///     the F loop tests already pin the zero-delay poll, and the poll scheduling itself belongs
///     to BankMaintenanceHostedService. G proves the stack; it fixes nothing.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ProjectIdAliasDefaultCollection.Name)]
public sealed class ProjectIdsConvergenceTests : IAsyncLifetime
{
    private const string Winner = "gw";
    private const string Loser = "g-loser";
    private const string SharedLoser = "g-shared";
    private const string MetricsOnly = "g-metrics";
    private const string Dropped = "g-dropped";
    private const string Retired = "g-retired";

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("project-ids-convergence");
    private readonly FakeTimeProvider _clock = new(FixedNow);
    private SqliteConnectionFactory _factory = null!;
    private SqliteMemoryStore _store = null!;
    private FakeEmbeddingEndpoint _embeddingEndpoint = null!;

    public async ValueTask InitializeAsync()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), new StubChunker(), _clock, TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);

        _embeddingEndpoint = await FakeEmbeddingEndpoint.StartAsync(TestContext.Current.CancellationToken);
        await _store.SetSettingAsync(EmbeddingSettingsKeys.ApiKey, "test-key-123", TestContext.Current.CancellationToken);
        await TestData.ConfigureAndDrainEmbeddingAsync(_store, _factory, TestData.CreateEmbeddingService(),
            "openai", "nomic-embed-text", _embeddingEndpoint.BaseUrl, TestContext.Current.CancellationToken, _clock);
    }

    public async ValueTask DisposeAsync()
    {
        // Package E1 (review #619): every repair apply reloads the process-static choke-point
        // cache (job-side reload after persisting) — reset it so the next collection test sees
        // the empty steady state, same hygiene as SingleProjectIdE2E.DisposeAsync.
        ProjectIdAliasMap.ResetDefault();
        await _embeddingEndpoint.DisposeAsync();
        TestData.DeleteTempRoot(_dataRoot);
    }

    /// <summary>
    ///     G1: NULL-only + custom + shared-keyed (H9 pin-with-reason) + metrics-only + a live
    ///     writer racing the first pass converge in ONE --apply invocation to the D6 verdict:
    ///     zero folds/drops/retires/unresolved, the remaining non-canonical rows pinned with
    ///     reasons, the durable map persisted, the drain empty, P3 armed on the closing line.
    /// </summary>
    [RetryFact]
    public async Task RunOnce_ConvergesToPinnedOnlyVerdict_WithP3Armed()
    {
        var ct = TestContext.Current.CancellationToken;
        var (stdout, live, _) = await RunConvergedAsync(ct);

        live.RequestCalls.ShouldBe(2, "pass 1 folds the seed; pass 2 folds the racing writer row; the settled re-derive commits none");
        stdout.ShouldContain("pass 1");
        stdout.ShouldContain("pass 2");
        stdout.ShouldContain("census totals grew");

        // SQL ground truth before the verdict line: a narrowed applier (pre-D1) leaves
        // committed loser rows behind while the plan still reads clean, so these fail first.
        await using var connection = await _factory.OpenBankAsync(ct);
        (await CommittedCountAsync(connection, Loser, ct))
            .ShouldBe(0, "every committed loser row folded — NULL-context and custom with labeled");
        (await CountAsync(connection,
                "SELECT count(*) FROM entries WHERE hash = 'gs-shared' AND scope = 'shared' AND project_id = @id AND value = 'shared content stays put'",
                ct, new { id = SharedLoser }))
            .ShouldBe(1, "the shared row is byte-identical under its writer's key — never folded (H9)");
        (await CountAsync(connection, "SELECT count(*) FROM metrics WHERE project_id = @id",
                ct, new { id = MetricsOnly }))
            .ShouldBe(1, "telemetry is never moved — the metrics-only id pins instead");
        (await CountAsync(connection,
                "SELECT count(*) FROM sync_tombstones WHERE project_id = @id AND hash = 'gd-residue'",
                ct, new { id = Dropped }))
            .ShouldBe(1, "the dropped residue deletes with a tombstone per removed hash");
        (await CountAsync(connection, "SELECT count(*) FROM projects WHERE id = @id",
                ct, new { id = Retired }))
            .ShouldBe(0, "the registered-empty id retires — its registry row is removed");
        (await CountAsync(connection, "SELECT count(*) FROM projects WHERE id = @id",
                ct, new { id = Winner }))
            .ShouldBe(1, "the winner's registration survives");

        var aliases = (await connection.QueryAsync<(string Alias, string? Winner, string Kind)>(
                new CommandDefinition(
                    "SELECT alias AS Alias, winner AS Winner, kind AS Kind FROM project_id_aliases ORDER BY alias",
                    cancellationToken: ct)))
            .ToList();
        aliases.ShouldBe(
            [(Dropped, (string?)null, "drop"), (Loser, Winner, "alias"), (SharedLoser, Winner, "alias")],
            "D6-iv: the applied one-shot map persists — canonicals are not stored, rows are append-only");

        (await CountAsync(connection,
                "SELECT count(*) FROM repair_requests WHERE kind = @kind AND finished_at IS NULL",
                ct, new { kind = RepairKinds.ProjectIds }))
            .ShouldBe(0, "no request is left open behind the loop");
        (await CountAsync(connection,
                "SELECT count(*) FROM repair_requests WHERE kind = @kind AND finished_at IS NOT NULL",
                ct, new { kind = RepairKinds.ProjectIds }))
            .ShouldBe(1, "one row per kind (upsert): the last pass finished it");
        (await CountAsync(connection, MemorySql.CountPendingEmbed, ct))
            .ShouldBe(0, "the drain cleared what the fold left pending — job→drain ordering holds");
        live.Drains.ShouldBe(2, "each committed request applies the job AND drains, like the maintenance poll would");

        // The closing D6 line, asserted last: every RED mutation below funnels here when its
        // own assert does not fire first, so this stays the funnel, never the only check. The P3
        // clause carries the durable map's own row counts — the fixture map's two aliases and one
        // dropped id, read back off project_id_aliases — so "armed" names what is armed.
        LastNonEmptyLine(stdout).ShouldBe(
            "project-ids repair: summary — pinned-only: 0 fold, 0 drop, 0 retire, 0 unresolved, " +
            "2 pinned (pinned-telemetry-only: 'g-metrics', pinned-shared-only: 'g-shared'), " +
            "P3 armed (2 alias, 1 dropped).");
    }

    /// <summary>
    ///     G1-iii: two consecutive derives off the converged bank are identical — same pinned
    ///     sets with the same reasons, zero actionable, zero moved rows — and a further job run
    ///     is a no-op (no open request).
    /// </summary>
    [RetryFact]
    public async Task ConsecutiveDerives_AreIdenticalWithZeroMoves()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, live, map) = await RunConvergedAsync(ct);

        var first = await live.ReportProjectIdsAsync(ct);
        var planA = ProjectIdsFoldPlan.FromCensus(first, map);
        var second = await live.ReportProjectIdsAsync(ct);
        var planB = ProjectIdsFoldPlan.FromCensus(second, map);

        PinsCanon(planB).ShouldBe(PinsCanon(planA), "stability: identical pinned sets with identical reasons");
        (planB.Folds.Count + planB.Dropped.Count + planB.RetiredProjects.Count)
            .ShouldBe(0, "stability: nothing actionable remains to move");
        planB.Unresolved.ShouldBeEmpty("stability: no id waits for a human");
        TotalsById(second).ShouldBe(TotalsById(first), "stability: zero rows moved between derives");
        await using var connection = await _factory.OpenBankAsync(ct);
        (await NewRepairJob().RunAsync(connection, ct))
            .ShouldBeFalse("no open request — a further run touches nothing");
    }

    /// <summary>
    ///     G2 post-fix probe (D4): after convergence, a write under the retired alias loser
    ///     folds through to the winner and lands canonical, a write under the dropped id is
    ///     refused naming the repair attribution, and a re-derive stays clean (zero actionable,
    ///     same pins) through the probe.
    ///     <para>
    ///         Known E double-check #1, closed by the E1 reload legs (review #619): the repair job
    ///         reloads the choke-point cache right after persisting the applied map, so the
    ///         same-process probe observes the PIPELINE's reload — there is no hand reload here by
    ///         design. A hand reload would mask a job-reload regression and prove nothing about the
    ///         pipeline; the RED ledger strips the job leg instead and watches this probe fail.
    ///         The startup-warm leg (ProjectIdAliasCacheHostedService) covers real restarts and is
    ///         not exercised in-process.
    ///     </para>
    /// </summary>
    [RetryFact]
    public async Task PostFixProbe_RefusesDroppedAndFoldsAliasLoser_ThenRederiveStaysClean()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, live, map) = await RunConvergedAsync(ct);
        var before = ProjectIdsFoldPlan.FromCensus(await live.ReportProjectIdsAsync(ct), map);
        try
        {
            // No hand reload: the job's own reload leg (Package E1) already refreshed Default
            // off the persisted map during the run above — asserting here proves the pipeline.
            ProjectIdAliasMap.Default.TryResolve(Loser, out var resolved).ShouldBeTrue();
            resolved.ShouldBe(Winner, "the reloaded durable map drives the chokes — no file map in this process");
            (await new SqliteProjectIdsMigrationGate(_factory).IsMigratedAsync(ct))
                .ShouldBeTrue("P3 arms on the job's finished row — earned, never hand-stamped");

            var gate = NewRealGate();
            (await gate.RequireAsync(Loser, AccessRequirement.Write, "memory_write", ct))
                .ShouldBe(Winner, "E-AC2: a stale-config write under the retired loser folds through to the winner");

            var tools = NewRealTools(gate);
            var written = await tools.Write(Loser, "probe quokka folds through", cancellationToken: ct);
            written.Data!.Stored.ShouldBeTrue("the folded-through write stores — refusal is only for dropped ids");
            await using var connection = await _factory.OpenBankAsync(ct);
            (await CountAsync(connection,
                    "SELECT count(*) FROM entries WHERE hash = @hash AND project_id = @winner",
                    ct, new { hash = written.Data.Hash, winner = Winner }))
                .ShouldBe(1, "the probe content lands canonical under the winner");
            (await CommittedCountAsync(connection, Loser, ct))
                .ShouldBe(0, "nothing resurrects the retired loser key");

            var refusal = await Should.ThrowAsync<RetiredProjectException>(() =>
                gate.RequireAsync(Dropped, AccessRequirement.Write, "memory_write", ct));
            refusal.Message.ShouldContain(Dropped);
            refusal.Message.ShouldContain("repair");
            (await CountAsync(connection, "SELECT count(*) FROM entries WHERE project_id = @id",
                    ct, new { id = Dropped }))
                .ShouldBe(0, "the refused write stores nothing");

            var after = ProjectIdsFoldPlan.FromCensus(await live.ReportProjectIdsAsync(ct), map);
            (after.Folds.Count + after.Dropped.Count + after.RetiredProjects.Count)
                .ShouldBe(0, "re-derive stability: the probe leaves nothing actionable");
            after.Unresolved.ShouldBeEmpty();
            PinsCanon(after).ShouldBe(PinsCanon(before), "re-derive stability: the same pins with the same reasons");
        }
        finally
        {
            ProjectIdAliasMap.ResetDefault();
        }
    }

    private async Task<(string Stdout, LiveServerRepairStore Live, ProjectIdAliasMap Map)> RunConvergedAsync(CancellationToken ct)
    {
        await using (var seedConnection = await _factory.OpenBankAsync(ct))
        {
            await SeedConvergenceBankAsync(seedConnection, ct);
            (await CountAsync(seedConnection, MemorySql.CountPendingEmbed, ct))
                .ShouldBe(7, "arrange: every seeded entry row starts pending — the drain faces a real backlog, never a vacuous zero");
        }

        var map = ConvergenceMap();
        var mapPath = Path.Combine(_dataRoot, $"convergence-map-{Guid.CreateVersion7():N}.json");
        File.WriteAllText(mapPath, map.ToJson(indented: true));
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]);
        var live = new LiveServerRepairStore(
            new SqliteRepairStore(_factory, matcher, TestData.CreateEmbeddingService(), _store, _clock),
            _factory, _store, matcher, InsertRacingWriterRowsAsync);
        CliArgs.TryParse(["repair", "project-ids", "--apply", "--map", mapPath], out var parsed).ShouldBeTrue();
        var stdoutWriter = new StringWriter();
        var stderrWriter = new StringWriter();
        var exit = await new ProjectIdsRepairCommands(live, ProjectIdsRepairCommands.RepairLoopOptions.Test, TimeProvider.System)
            .RunAsync(parsed!.ParsedCliArgs, _dataRoot,
                new StandardStreams(TextReader.Null, stdoutWriter, stderrWriter), ct);

        exit.ShouldBe(0, $"stderr: {stderrWriter}");
        return (stdoutWriter.ToString(), live, map);
    }

    private static ProjectIdAliasMap ConvergenceMap() => new(
        [new ProjectIdAliasEntry(Loser, Winner), new ProjectIdAliasEntry(SharedLoser, Winner)],
        [Winner],
        [Dropped]);

    /// <summary>
    ///     The G1 scratch bank: a NULL-only bulk row, a labeled row and two custom-scope rows
    ///     under the alias loser (all fold — D1); one shared row under its writer's key (pins
    ///     shared-only — H9); one metrics row under an unmapped id (pins telemetry-only — D3,
    ///     the b0e32c16 shape); one residue row under the dropped id (deletes with a tombstone);
    ///     a registered-empty id (retires); the winner's own row (needs nothing).
    /// </summary>
    private static async Task SeedConvergenceBankAsync(SqliteConnection connection, CancellationToken ct)
    {
        var now = FixedNow.ToUnixTimeSeconds();
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO projects (id, name, created_at) VALUES (@winner, @winner, @now), (@retired, @retired, @now)",
                new { winner = Winner, retired = Retired, now }, cancellationToken: ct));
        async Task Entry(string hash, string scope, string projectId, string? label, string value)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state) " +
                    "VALUES (@hash, @hash, @value, 'seed.md', 's', @scope, @projectId, @label, @now, @now, 'pending')",
                    new { hash, value, scope, projectId, label, now }, cancellationToken: ct));
        }

        await Entry("gw-own", "project", Winner, "ctx-a", "winner content stays");
        await Entry("gl-labeled", "project", Loser, "ctx-a", "loser labeled folds");
        await Entry("gl-bulk", "project", Loser, null, "loser bulk folds");
        await Entry("gl-custom", "custom", Loser, "ctx-a", "loser custom folds");
        await Entry("gl-custom-bulk", "custom", Loser, null, "loser custom bulk folds");
        await Entry("gs-shared", "shared", SharedLoser, null, "shared content stays put");
        await Entry("gd-residue", "project", Dropped, "ctx-a", "dropped residue deletes");
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO metrics (name, kind, value, unit, project_id, recorded_at) VALUES ('m', 'k', 1, 'u', @id, @now)",
                new { id = MetricsOnly, now }, cancellationToken: ct));
    }

    /// <summary>
    ///     The live-writer condition: one racing write (labeled + custom-NULL) lands under the
    ///     retired loser AFTER the first apply+drain, then quiesces. A single-shot repair would
    ///     leave it behind; the loop re-derives and folds it on pass 2 while reporting the
    ///     measured totals growth instead of a false converged.
    /// </summary>
    private async Task InsertRacingWriterRowsAsync(CancellationToken ct)
    {
        var now = FixedNow.ToUnixTimeSeconds();
        await using var connection = await _factory.OpenBankAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state) VALUES " +
                "('gl-race-labeled', 'gl-race-labeled', 'racing writer labeled', 'seed.md', 's', 'project', @loser, 'ctx-race', @now, @now, 'pending'), " +
                "('gl-race-custom-bulk', 'gl-race-custom-bulk', 'racing writer custom bulk', 'seed.md', 's', 'custom', @loser, NULL, @now, @now, 'pending')",
                new { loser = Loser, now }, cancellationToken: ct));
    }

    private ProjectIdsRepairJob NewRepairJob() =>
        new(new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]),
            TestData.CreateEmbeddingService(), new FakeTimeProvider(FixedNow));

    private ToolGate NewRealGate()
    {
        var migrationGate = new SqliteProjectIdsMigrationGate(_factory);
        var guard = new ProjectRegistrationGuard(_store, NullLogger<ProjectRegistrationGuard>.Instance, migrationGate);
        return new ToolGate(new MemoryAccessGuard(_store), new FakePromotionQueue(),
            new NeverMigratingStore(), guard, migrationGate);
    }

    private MemoryTools NewRealTools(ToolGate gate) =>
        new(_store, gate,
            new SearchDispatcher(_store, new NoOpCodeSearchService(), new NoOpSearchQualityService()),
            new QueryGuardService(new InMemorySettings()), new MemoryWriteService(_store, new FakePromotionQueue()),
            NoOpMeasurementRecorder.Instance, NullLogger<MemoryTools>.Instance);

    private static IReadOnlyList<string> PinsCanon(ProjectIdsFoldPlan plan) =>
        plan.Pinned.Select(pin => $"{pin.Bucket}|{pin.ProjectId}|{pin.Reason}").ToList();

    private static Dictionary<string, long> TotalsById(ProjectIdCensusReport report) =>
        report.Rows.ToDictionary(row => row.ProjectId, row => row.EntryTotal, StringComparer.Ordinal);

    private static string LastNonEmptyLine(string stdout) =>
        stdout.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).Last();

    private static async Task<long> CountAsync(SqliteConnection connection, string sql, CancellationToken ct, object? param = null) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, param, cancellationToken: ct));

    private static async Task<long> CommittedCountAsync(SqliteConnection connection, string projectId, CancellationToken ct) =>
        await CountAsync(connection,
            "SELECT count(*) FROM entries WHERE project_id = @projectId AND scope IN ('project', 'custom')",
            ct, new { projectId });

    /// <summary>
    ///     The test-side "server": the real <see cref="SqliteRepairStore" /> (request row +
    ///     census reads) with the maintenance poll collapsed inline — each committed request is
    ///     applied by the real <see cref="ProjectIdsRepairJob" /> off its stored one-shot map and
    ///     drained, exactly as the poll would. Request/drain counts are the loop's instruments.
    /// </summary>
    private sealed class LiveServerRepairStore(
        SqliteRepairStore inner,
        SqliteConnectionFactory factory,
        SqliteMemoryStore store,
        IFileTypeMatcher matcher,
        Func<CancellationToken, Task> afterFirstApplyAsync) : IRepairStore
    {
        public int RequestCalls { get; private set; }

        public int Drains { get; private set; }

        public Task<ProjectIdCensusReport> ReportProjectIdsAsync(CancellationToken cancellationToken = default) =>
            inner.ReportProjectIdsAsync(cancellationToken);

        public async Task RequestRepairAsync(RepairKind kind, CancellationToken cancellationToken = default, string? projectIdsMapJson = null)
        {
            await inner.RequestRepairAsync(kind, cancellationToken, projectIdsMapJson).ConfigureAwait(false);
            RequestCalls++;
            await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
            await new ProjectIdsRepairJob(matcher, TestData.CreateEmbeddingService(), new FakeTimeProvider(FixedNow))
                .RunAsync(connection, cancellationToken).ConfigureAwait(false);
            await store.EmbedPendingAsync(Winner, null, cancellationToken).ConfigureAwait(false);
            await store.EmbedPendingAsync(SharedLoser, null, cancellationToken).ConfigureAwait(false);
            Drains++;
            if (RequestCalls == 1)
            {
                await afterFirstApplyAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public Task<ReingestRepairReport> ReportReingestAsync(CancellationToken cancellationToken = default) =>
            inner.ReportReingestAsync(cancellationToken);

        public Task<ChunkIndexRepairReport> ReportChunkIndexAsync(CancellationToken cancellationToken = default) =>
            inner.ReportChunkIndexAsync(cancellationToken);
    }
}
