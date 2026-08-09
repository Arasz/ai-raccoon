using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.storage;

/// <summary>
///     promotion_queue_entries_ad (ADR-0022): a queue row is a promise about an entries row that
///     may no longer exist. This covers every real deletion path, the NOT EXISTS sibling guard,
///     and the shared-scope no-op — not just one call site.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class PromotionQueueInvalidationTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-queue-invalidation");
    private readonly string _contentRoot = TestData.CreateTempRoot("ai-raccoon-queue-invalidation-content");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeTimeProvider _clock;
    private readonly SqliteMemoryStore _store;
    private readonly SqlitePromotionQueueStore _queueStore;

    public PromotionQueueInvalidationTests()
    {
        var options = new InfrastructureOptions
        {
            DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User
        };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _clock = new FakeTimeProvider(FixedNow);
        _store = new SqliteMemoryStore(_factory, _clock, new StubChunker(), new EmbeddingService(), NullLogger<SqliteMemoryStore>.Instance);
        _queueStore = new SqlitePromotionQueueStore(_factory, _clock);
    }

    public void Dispose()
    {
        foreach (var root in new[] { _dataRoot, _contentRoot })
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static QueueCandidate Candidate(string hash, string value, double score) =>
        new(hash, $"{hash}.md", value, null, score, ["organic-write"]);

    [Fact]
    public async Task DeletingAnEntry_DropsItsQueuedCandidate()
    {
        var entry = await _store.WriteAsync(
            new MemoryWriteRequest("acme", "doomed fact", null, null, null, null, null),
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
        await _store.ReplaceFileAsync("acme", path, "file-hash-2", TestContext.Current.CancellationToken);

        (await _queueStore.ListAsync("acme", TestContext.Current.CancellationToken))
            .Select(r => r.Hash).ShouldNotContain(oldHash,
                "re-ingest deletes the old chunk row under a new hash; the queue must not keep the dead one");
    }

    /// <summary>
    ///     uq_entries_committed_bucket permits the same (project, hash) under two context labels.
    ///     This is the required prove-the-check-fails step for ADR-0022: written first against a
    ///     trigger with no NOT EXISTS guard (observed red), then against the guarded trigger (green).
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
                INSERT INTO entries (id, hash, path, value, scope, project_id, context_label, created_at, updated_at, embed_state)
                VALUES (2, 'shared-hash', 'p.md', 'v', 'custom', 'acme', 'ctx-b', 1, 1, 'embedded');
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
                "entry id=2 still backs this hash; deleting its sibling must not drop the candidate");
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
            new MemoryWriteRequest("acme", "still here", null, null, null, null, null),
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

    private sealed class StubChunker : IChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) =>
            text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }
}
