using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     promotion_queue_entries_ad (ADR-0023): a queue row is a promise about an entries row that
///     may no longer exist. This covers every real deletion path, the NOT EXISTS sibling guard,
///     and the shared-scope no-op — not just one call site.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class PromotionQueueInvalidationTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);
    private readonly FakeTimeProvider _clock;
    private readonly string _contentRoot = TestData.CreateTempRoot("ai-raccoon-queue-invalidation-content");

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-queue-invalidation");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqlitePromotionQueueStore _queueStore;
    private readonly SqliteMemoryStore _store;

    public PromotionQueueInvalidationTests()
    {
        var options = new InfrastructureOptions
        {
            DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User
        };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _clock = new FakeTimeProvider(FixedNow);
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance, new SqliteMemorySourceStore(_factory), new StubChunker(), _clock, TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        _queueStore = new SqlitePromotionQueueStore(_factory, _clock);
    }

    public void Dispose()
    {
        foreach (var root in new[] { _dataRoot, _contentRoot })
        {
            TestData.DeleteTempRoot(root);
        }
    }

    private static QueueCandidate Candidate(string hash, string value, double score) => new(hash, $"{hash}.md", value, null, score, ["organic-write"]);

    [Fact]
    public async Task DeletingAnEntry_DropsItsQueuedCandidate()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "doomed fact"),
            TestContext.Current.CancellationToken);
        await _queueStore.UpsertAsync("acme", [Candidate(entry.Hash, "doomed fact", 1.0)],
            TestContext.Current.CancellationToken);

        await _store.DeleteAsync("acme", entry.Hash, TestContext.Current.CancellationToken);

        (await _queueStore.ListAsync("acme", TestContext.Current.CancellationToken)).ShouldBeEmpty(
            "the trigger must drop the queue row once the entry it points at is gone");
    }

    [Fact]
    public async Task ReplacingAWatchedFile_DropsQueuedCandidatesForItsOldChunks()
    {
        await SetScopeAsync("acme");
        var path = Path.Combine(_contentRoot, "adr.md");
        await File.WriteAllTextAsync(path, "original adr content", TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", path, null, TestContext.Current.CancellationToken);
        var oldHash = await SingleEntryHashAsync("acme", path);
        await _queueStore.UpsertAsync("acme", [Candidate(oldHash, "original adr content", 1.0)],
            TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(path, "revised adr content", TestContext.Current.CancellationToken);
        await _store.ReplaceIfFileChangedAsync("acme", path, "file-hash-2", TestContext.Current.CancellationToken);

        (await _queueStore.ListAsync("acme", TestContext.Current.CancellationToken))
            .Select(r => r.Hash).ShouldNotContain(oldHash,
                "re-ingest deletes the old chunk row under a new hash; the queue must not keep the dead one");
    }

    /// <summary>
    ///     uq_entries_committed_bucket permits the same (project, hash) under two context labels.
    ///     This is the required prove-the-check-fails step for ADR-0023: written first against a
    ///     trigger with no NOT EXISTS guard (observed red), then against the guarded trigger (green).
    ///     The surviving sibling (id=2) is project-scoped — post-H4 the guard only treats a
    ///     project-scope backer as "still live" (see DeletingTheProjectEntry_WithOnlyACustomScopeSiblingSurviving_DropsTheQueuedCandidate
    ///     below for the case where the *only* surviving sibling is not project-scoped).
    /// </summary>
    [Fact]
    public async Task DeletingOneOfTwoEntriesSharingAHash_KeepsTheQueuedCandidate()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO entries (id, hash, path, value, scope, project_id, context_label, created_at, updated_at, embed_state)
                VALUES (1, 'shared-hash', 'p.md', 'v', 'custom', 'acme', 'ctx-a', 1, 1, 'embedded');
                INSERT INTO entries (id, hash, path, value, scope, project_id, created_at, updated_at, embed_state)
                VALUES (2, 'shared-hash', 'p.md', 'v', 'project', 'acme', 1, 1, 'embedded');
                """);
        }

        await _queueStore.UpsertAsync("acme", [Candidate("shared-hash", "v", 1.0)],
            TestContext.Current.CancellationToken);

        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync("DELETE FROM entries WHERE id = 1");
        }

        (await _queueStore.ListAsync("acme", TestContext.Current.CancellationToken))
            .Select(r => r.Hash).ShouldContain("shared-hash",
                "entry id=2 is project-scoped and still backs this hash; deleting its custom-scope sibling must not drop the candidate");
    }

    /// <summary>
    ///     Inverted by ADR-0046. H4 asserted the opposite on the premise that "ShareAsync resolves
    ///     candidates with `scope = 'project'`", so a hash surviving only as a custom-scope sibling
    ///     was unpromotable and its queue row was an orphan. ShareAsync now resolves any row inside
    ///     the project (ProjectRows), so that sibling is a live backer and the candidate stands.
    /// </summary>
    [Fact]
    public async Task DeletingTheProjectEntry_WithACustomScopeSiblingSurviving_KeepsTheQueuedCandidate()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO entries (id, hash, path, value, scope, project_id, created_at, updated_at, embed_state)
                VALUES (1, 'shared-hash', 'p.md', 'v', 'project', 'acme', 1, 1, 'embedded');
                INSERT INTO entries (id, hash, path, value, scope, project_id, context_label, created_at, updated_at, embed_state)
                VALUES (2, 'shared-hash', 'p.md', 'v', 'custom', 'acme', 'ctx-a', 1, 1, 'embedded');
                """);
        }

        await _queueStore.UpsertAsync("acme", [Candidate("shared-hash", "v", 1.0)],
            TestContext.Current.CancellationToken);

        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync("DELETE FROM entries WHERE id = 1");
        }

        (await _queueStore.ListAsync("acme", TestContext.Current.CancellationToken))
            .Select(r => r.Hash).ShouldContain("shared-hash",
                "a custom-scope sibling is inside the project, so it still backs this hash and " +
                "ShareAsync can resolve it (ADR-0046)");
    }

    /// <summary>
    ///     Prove-the-check-fails companion to the test above: with the trigger removed entirely,
    ///     nothing here spontaneously drops the queue row, so the assertion above is attributable
    ///     to the trigger's guard and not some incidental side effect of the delete. Everything
    ///     stays on one connection — a fresh OpenBankAsync() re-runs EnsureAsync, whose
    ///     `CREATE TRIGGER IF NOT EXISTS` would silently recreate the dropped trigger.
    /// </summary>
    [Fact]
    public async Task WithTheTriggerDropped_DeletingTheProjectEntry_LeavesTheQueuedCandidateUntouched()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("DROP TRIGGER promotion_queue_entries_ad");
        await connection.ExecuteAsync(
            """
            INSERT INTO entries (id, hash, path, value, scope, project_id, created_at, updated_at, embed_state)
            VALUES (1, 'shared-hash', 'p.md', 'v', 'project', 'acme', 1, 1, 'embedded');
            INSERT INTO entries (id, hash, path, value, scope, project_id, context_label, created_at, updated_at, embed_state)
            VALUES (2, 'shared-hash', 'p.md', 'v', 'custom', 'acme', 'ctx-a', 1, 1, 'embedded');
            INSERT INTO promotion_queue (project_id, hash, path, value, score, reasons, created_at, updated_at)
            VALUES ('acme', 'shared-hash', 'p.md', 'v', 1.0, '[]', 1, 1);
            """);

        await connection.ExecuteAsync("DELETE FROM entries WHERE id = 1");

        var survivor = await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT hash FROM promotion_queue WHERE project_id = 'acme' AND hash = 'shared-hash'");
        survivor.ShouldBe("shared-hash",
            "with no trigger at all the row must survive; if it didn't, the fixture above would prove nothing");
    }

    /// <summary>Regression: a queue row backed by a live project-scope entry must not be disturbed
    /// by an unrelated delete in the same project.</summary>
    [Fact]
    public async Task DeletingAnUnrelatedEntry_DoesNotAffectAQueueRowBackedByALiveProjectScopeEntry()
    {
        var kept = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "kept fact"),
            TestContext.Current.CancellationToken);
        await _queueStore.UpsertAsync("acme", [Candidate(kept.Hash, "kept fact", 1.0)],
            TestContext.Current.CancellationToken);
        var unrelated = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "unrelated fact"),
            TestContext.Current.CancellationToken);

        await _store.DeleteAsync("acme", unrelated.Hash, TestContext.Current.CancellationToken);

        (await _queueStore.ListAsync("acme", TestContext.Current.CancellationToken))
            .Select(r => r.Hash).ShouldContain(kept.Hash);
    }

    /// <summary>
    ///     The orphan definition must match what ShareAsync can resolve. H5 read that as
    ///     "no project-scope entry backs this hash"; ADR-0046 makes it "no row inside the project"
    ///     (<see cref="ProjectRows" />), so a custom-scope row is a live backer and only a queue row
    ///     with no entry at all is an orphan. Both survivors are asserted so the prune cannot pass
    ///     by removing everything.
    /// </summary>
    [Fact]
    public async Task Prune_RemovesOnlyQueueRowsNothingInTheProjectBacks()
    {
        var live = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "still project-scoped"),
            TestContext.Current.CancellationToken);
        await _queueStore.UpsertAsync("acme", [Candidate(live.Hash, "still project-scoped", 2.0)],
            TestContext.Current.CancellationToken);

        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO entries (id, hash, path, value, scope, project_id, context_label, created_at, updated_at, embed_state)
                VALUES (100, 'custom-only-hash', 'p.md', 'v', 'custom', 'acme', 'ctx-a', 1, 1, 'embedded');
                """);
        }

        await _queueStore.UpsertAsync("acme", [Candidate("custom-only-hash", "v", 1.0)],
            TestContext.Current.CancellationToken);
        // No entries row at all: the one genuine orphan.
        await _queueStore.UpsertAsync("acme", [Candidate("unbacked-hash", "v", 0.5)],
            TestContext.Current.CancellationToken);

        var dryRun = await _queueStore.PruneOrphansAsync(apply: false, TestContext.Current.CancellationToken);
        dryRun.PerProject.ShouldBe(new Dictionary<string, int> { ["acme"] = 1 },
            "only the unbacked hash is an orphan; the project-scope and custom-scope rows both back theirs");

        var applied = await _queueStore.PruneOrphansAsync(apply: true, TestContext.Current.CancellationToken);
        applied.TotalOrphans.ShouldBe(1);
        (await _queueStore.ListAsync("acme", TestContext.Current.CancellationToken))
            .Select(r => r.Hash).Order().ShouldBe(new[] { live.Hash, "custom-only-hash" }.Order(),
                "both backed candidates must survive prune");

        var rerun = await _queueStore.PruneOrphansAsync(apply: true, TestContext.Current.CancellationToken);
        rerun.TotalOrphans.ShouldBe(0, "idempotent — nothing left to remove on a second pass");
    }

    /// <summary>Orphans pre-dating the trigger — no backing entries row at all, unlike the sibling
    /// scenario above where a row backs the hash under a different context.</summary>
    [Fact]
    public async Task Prune_ReportsOrphansWithoutApply_AndRemovesThemWithApply()
    {
        await _queueStore.UpsertAsync("acme", [Candidate("orphan-1", "gone", 1.0)],
            TestContext.Current.CancellationToken);
        await _queueStore.UpsertAsync("other", [Candidate("orphan-2", "also gone", 1.0)],
            TestContext.Current.CancellationToken);
        var live = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "still here"),
            TestContext.Current.CancellationToken);
        await _queueStore.UpsertAsync("acme", [Candidate(live.Hash, "still here", 2.0)],
            TestContext.Current.CancellationToken);

        var dryRun = await _queueStore.PruneOrphansAsync(apply: false, TestContext.Current.CancellationToken);

        dryRun.TotalOrphans.ShouldBe(2);
        dryRun.PerProject.ShouldBe(new Dictionary<string, int> { ["acme"] = 1, ["other"] = 1 });
        (await _queueStore.ListAsync(null, TestContext.Current.CancellationToken)).Count.ShouldBe(3,
            "a dry run must not delete anything");

        var applied = await _queueStore.PruneOrphansAsync(apply: true, TestContext.Current.CancellationToken);

        applied.TotalOrphans.ShouldBe(2);
        (await _queueStore.ListAsync(null, TestContext.Current.CancellationToken))
            .Select(r => r.Hash).ShouldBe([live.Hash], "the live candidate survives; only the orphans are removed");

        var rerun = await _queueStore.PruneOrphansAsync(apply: true, TestContext.Current.CancellationToken);
        rerun.TotalOrphans.ShouldBe(0, "prune must be idempotent — nothing left to remove on a second pass");
    }

    /// <summary>
    ///     The verb as a user runs it. The store-level test above calls ReportPruneOrphansAsync/
    ///     RequestPruneOrphansAsync directly and so cannot see the argv-to-handler wiring, which is
    ///     where this shipped broken. ADR-0075 amendment: --apply commits an outbox request rather
    ///     than deleting inline, so the queue itself is unchanged immediately after either form —
    ///     the actual delete is PromotionQueuePruneJob's job, exercised separately.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PruneVerb_ParsedFromArgv_ReachesTheStore(bool apply)
    {
        await _queueStore.UpsertAsync("acme", [Candidate("orphan-1", "gone", 1.0)],
            TestContext.Current.CancellationToken);

        string[] argv = apply ? ["extract", "prune", "--apply"] : ["extract", "prune"];
        CliArgs.TryParse(argv, out var parsed);
        parsed!.Errors.ShouldBeEmpty();

        var stdout = new StringWriter();
        var exit = await new ExtractCommands(_queueStore)
            .PruneAsync(parsed.ParsedCliArgs, new StandardStreams(TextReader.Null, stdout, TextWriter.Null), TestContext.Current.CancellationToken);

        exit.ShouldBe(0);
        stdout.ToString().ShouldContain("1 orphaned candidate(s)");
        var remaining = (await _queueStore.ListAsync(null, TestContext.Current.CancellationToken)).Count;
        remaining.ShouldBe(1, "the report/request split never deletes synchronously; PromotionQueuePruneJob does");
    }

    /// <summary>
    ///     A re-ingest deletes every chunk of the path and re-inserts them; a chunk whose text did not
    ///     change returns under the same content hash. Its candidate must survive that round trip.
    /// </summary>
    [Fact]
    public async Task ReplacingAWatchedFile_KeepsCandidatesForChunksThatDidNotChange()
    {
        await SetScopeAsync("acme");
        var path = Path.Combine(_contentRoot, "multi.md");
        await File.WriteAllTextAsync(path, "stable paragraph\n\noriginal second paragraph",
            TestContext.Current.CancellationToken);
        await _store.IngestFileAsync("acme", path, null, TestContext.Current.CancellationToken);
        var stableHash = await HashOfAsync("acme", "stable paragraph");
        await _queueStore.UpsertAsync("acme", [Candidate(stableHash, "stable paragraph", 1.0)],
            TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(path, "stable paragraph\n\nrevised second paragraph",
            TestContext.Current.CancellationToken);
        await _store.ReplaceIfFileChangedAsync("acme", path, "file-hash-2", TestContext.Current.CancellationToken);

        (await HashOfAsync("acme", "stable paragraph")).ShouldBe(stableHash,
            "the unchanged chunk is re-inserted under the same content hash");
        (await _queueStore.ListAsync("acme", TestContext.Current.CancellationToken))
            .Select(r => r.Hash).ShouldContain(stableHash,
                "its entry is alive again after the replace, so the candidate must not have been dropped");
    }

    private async Task<string> HashOfAsync(string projectId, string value)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<string>(
            "SELECT hash FROM entries WHERE project_id = @projectId AND value = @value",
            new { projectId, value });
    }

    private async Task<string> SingleEntryHashAsync(string projectId, string sourceFile)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleAsync<string>(
            "SELECT hash FROM entries WHERE project_id = @projectId AND source_file = @sourceFile",
            new { projectId, sourceFile });
    }

    private Task SetScopeAsync(string projectId) =>
        _store.SetSettingAsync(IngestScopeKeys.ScopeProject(projectId), IngestScopeKeys.Serialize([_contentRoot]),
            TestContext.Current.CancellationToken);
}
