using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Promotion;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Extraction;

/// <summary>
///     Honest promotion accounting (fixes 1+2 of the count-math defect, measured 2026-08-09):
///     multi-chunk files coalesce to ONE shared row while every chunk used to be reported
///     promoted. The contract: claimed = promoted + absorbed + skipped + failures, where
///     promoted means the share actually CREATED a shared row (MemoryEntryResult.Created),
///     absorbed means the chunk was folded into an already-represented file (or lost an
///     insert race), and skipped means a whitespace-normalized value twin.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class PromotionQueueServicePromoteAccountingTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 9, 0, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-promote-accounting");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeTimeProvider _clock;
    private readonly RecordingMetrics _metrics;
    private readonly PromotionQueueService _service;

    public PromotionQueueServicePromoteAccountingTests()
    {
        var options = new InfrastructureOptions
        {
            DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User
        };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _clock = new FakeTimeProvider(FixedNow);
        var store = new SqliteMemoryStore(_factory, _clock, new StubChunker(), new EmbeddingService(),
            NullLogger<SqliteMemoryStore>.Instance);
        var queueStore = new SqlitePromotionQueueStore(_factory, _clock);
        _metrics = new RecordingMetrics();
        _service = new PromotionQueueService(queueStore, store, new UniformCountEvictionPolicy(),
            _metrics, NullLogger<PromotionQueueService>.Instance, _clock);
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    /// <summary>Seeds a project-scoped row with a REAL file path (the ingest shape: one row per
    /// chunk, path = source path, distinct values/hashes). Raw insert, because the shared-tier
    /// path scheme (value-addressed) is what is under test.</summary>
    private async Task SeedChunkAsync(string projectId, string hash, string path, string value,
        CancellationToken ct)
    {
        await using var connection = await _factory.OpenBankAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO entries (hash, path, value, scope, project_id, context_label, created_at, updated_at, embed_state)
            VALUES (@hash, @path, @value, 'project', @projectId, NULL, 1, 1, 'embedded')
            """,
            new { hash, path, value, projectId }, cancellationToken: ct));
    }

    private async Task<long> SharedValueCountAsync(string value, CancellationToken ct)
    {
        await using var connection = await _factory.OpenBankAsync(ct);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM entries WHERE scope = 'shared' AND value = @value",
            new { value }, cancellationToken: ct));
    }

    private static QueueCandidate Candidate(string hash, string path, string value, double score) =>
        new(hash, path, value, null, score, ["organic-write"]);

    [Fact]
    public async Task Promote_TwoChunksOfOneFile_BothChunksShared()
    {
        // Two chunks of the same source file with DISTINCT content: every promoted chunk
        // becomes its own shared row (value-addressed shared/<sha256(value)>.md). One file
        // may hold many shared rows — only identical chunk VALUES dedupe (see the value-twin
        // test). This was previously a bug: the file-path-addressed shared row coalesced all
        // chunks of a file into one row and silently dropped the rest.
        await SeedChunkAsync("acme", "h1", "docs/a.md", "chunk one", TestContext.Current.CancellationToken);
        await SeedChunkAsync("acme", "h2", "docs/a.md", "chunk two", TestContext.Current.CancellationToken);
        await _service.ProposeAsync("acme",
            [Candidate("h1", "docs/a.md", "chunk one", 5.0), Candidate("h2", "docs/a.md", "chunk two", 4.0)],
            TestContext.Current.CancellationToken);

        var outcome = await _service.PromoteAsync(["acme"], 2, TestContext.Current.CancellationToken);

        outcome.PromotedHashes.ShouldBe(["h1", "h2"],
            "every promoted chunk is shared — one file may hold many shared rows");
        outcome.Absorbed.ShouldBe(0, "distinct chunk contents never coalesce");
        outcome.SkippedDuplicates.ShouldBe(0);
        (await SharedValueCountAsync("chunk one", TestContext.Current.CancellationToken)).ShouldBe(1);
        (await SharedValueCountAsync("chunk two", TestContext.Current.CancellationToken)).ShouldBe(1,
            "both chunks of the file landed in the shared tier");
        (await _service.ListAsync("acme", 10, TestContext.Current.CancellationToken)).ShouldBeEmpty(
            "both chunks were claimed off the queue");
    }

    [Fact]
    public async Task Promote_BatchValueTwin_SkippedNotSecondRow()
    {
        // Two rows in ONE call, different paths, identical value: the first creates the shared
        // row; the refreshed in-batch index must classify the second as a value-twin SKIP —
        // not insert a second shared row (the stale-snapshot double insert).
        await SeedChunkAsync("acme", "h1", "one.md", "identical fact", TestContext.Current.CancellationToken);
        await SeedChunkAsync("acme", "h2", "two.md", "identical fact", TestContext.Current.CancellationToken);
        await _service.ProposeAsync("acme",
            [Candidate("h1", "one.md", "identical fact", 5.0), Candidate("h2", "two.md", "identical fact", 4.0)],
            TestContext.Current.CancellationToken);

        var outcome = await _service.PromoteAsync(["acme"], 2, TestContext.Current.CancellationToken);

        outcome.PromotedHashes.ShouldBe(["h1"]);
        outcome.SkippedDuplicates.ShouldBe(1,
            "h2's value is already in shared after h1's share — the refreshed index must skip it");
        outcome.Absorbed.ShouldBe(0);
        (await SharedValueCountAsync("identical fact", TestContext.Current.CancellationToken)).ShouldBe(1,
            "an identical chunk from a second path is a duplicate — one shared row, not two");
    }

    [Fact]
    public async Task Promote_ConcurrentSameContent_ExactlyOneCreated()
    {
        // The cross-call race: two PromoteAsync calls share the same (path, hash) content.
        // Both snapshots predate the winner's insert; the loser's ShareAsync returns
        // Created=false (the ON CONFLICT DO NOTHING loser, affected == 0). Exactly ONE call
        // may claim a promotion — counting "promoted sums to 1" would also pass on the broken
        // implementation, so each call's own PromotedHashes AND Absorbed are asserted.
        var queue = new RaceyQueueStore();
        queue.Rows.Add(new PromotionQueueRow("acme", "h1", "x.md", "shared fact", null, 5.0, [], 0, 0));
        queue.Rows.Add(new PromotionQueueRow("beta", "h1", "x.md", "shared fact", null, 5.0, [], 0, 0));
        var store = new RaceLosingShareStore { HashPaths = { ["h1"] = "x.md" } };
        var service = new PromotionQueueService(queue, store, new UniformCountEvictionPolicy(),
            new SpyMetrics(), NullLogger<PromotionQueueService>.Instance, new FakeTimeProvider(FixedNow));

        var first = await service.PromoteAsync(["acme"], 10, TestContext.Current.CancellationToken);
        var second = await service.PromoteAsync(["beta"], 10, TestContext.Current.CancellationToken);

        first.PromotedHashes.ShouldBe(["h1"], "the winner created the shared row");
        first.Absorbed.ShouldBe(0);
        second.PromotedHashes.ShouldBeEmpty("the loser's insert was a no-op — it must not claim a promotion");
        second.Absorbed.ShouldBe(1, "the race loser is absorbed, not skipped: its snapshot predates the winner's row");
        store.CreatedShares.ShouldBe(["h1"], "exactly one share actually created a row");
        store.SharedRows.ShouldBe(1, "exactly one shared row exists for the raced content");
    }

    [Fact]
    public async Task Promote_TwoChunksOfOneFile_LogLinePinsFieldOrder()
    {
        // The live test caught the EventId 702 args swapped (promoted/absorbed). Pin the order:
        // {Promoted} shared, {Absorbed} absorbed into existing files, {Skipped} duplicate-skipped.
        var logger = new ListLogger();
        var service = new PromotionQueueService(
            new SqlitePromotionQueueStore(_factory, _clock),
            new SqliteMemoryStore(_factory, _clock, new StubChunker(), new EmbeddingService(),
                NullLogger<SqliteMemoryStore>.Instance),
            new UniformCountEvictionPolicy(), new SpyMetrics(), logger, _clock);
        await SeedChunkAsync("acme", "h1", "docs/a.md", "chunk one", TestContext.Current.CancellationToken);
        await SeedChunkAsync("acme", "h2", "docs/a.md", "chunk two", TestContext.Current.CancellationToken);
        await _service.ProposeAsync("acme",
            [Candidate("h1", "docs/a.md", "chunk one", 5.0), Candidate("h2", "docs/a.md", "chunk two", 4.0)],
            TestContext.Current.CancellationToken);

        await service.PromoteAsync(["acme"], 2, TestContext.Current.CancellationToken);

        logger.Messages.ShouldContain(m => m.Contains(
            "Promoted from the queue for acme: 2 shared, 0 absorbed (already shared), 0 duplicate-skipped"),
            "the log line must report promoted and absorbed in their real order — a swapped pair was shipped once and caught live");
    }

    /// <summary>Queue store whose rows the test controls (copy of the race-tests shape).</summary>
    private sealed class RaceyQueueStore : IPromotionQueueStore
    {
        public List<PromotionQueueRow> Rows { get; } = [];

        public Task<int> UpsertAsync(string projectId, IReadOnlyList<QueueCandidate> rows,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PromotionQueueRow>> ListAsync(string? projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PromotionQueueRow>>(
                Rows.Where(r => projectId is null || r.ProjectId == projectId).ToList());

        public Task<IReadOnlyList<PromotionQueueRow>> DiscardAsync(string projectId, string? hash,
            CancellationToken cancellationToken = default)
        {
            var removed = Rows.Where(r => r.ProjectId == projectId && (hash == null || r.Hash == hash)).ToList();
            Rows.RemoveAll(removed.Contains);
            return Task.FromResult<IReadOnlyList<PromotionQueueRow>>(removed);
        }

        public Task<PromotionQueueStats> GetStatsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PromotionQueueStats(Rows.Count, null,
                Rows.GroupBy(r => r.ProjectId).ToDictionary(g => g.Key, g => g.Count())));

        public Task<PromotionWaitStats> GetWaitStatsAsync(string? projectId,
            CancellationToken cancellationToken = default)
        {
            var scoped = Rows.Where(r => projectId is null || r.ProjectId == projectId).ToList();
            return Task.FromResult(new PromotionWaitStats(scoped.Count, null, null,
                Rows.Select(r => r.ProjectId).Distinct(StringComparer.Ordinal).Count()));
        }

        public Task<PromotionQueueRow?> EvictVictimAsync(string projectId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> ClearStaleAsync(string projectId, int currentScorerVersion,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task RememberDiscardsAsync(string projectId, IReadOnlyList<string> hashes,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> PruneRejectedAsync(string projectId,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    /// <summary>
    ///     Store modeling the cross-call insert race deterministically: GetSharedIndexAsync
    ///     returns the index as of each call's start (concurrent calls' inserts are invisible
    ///     to it), and ShareAsync simulates SQLite's ON CONFLICT DO NOTHING — the first share of
    ///     a hash creates (Created=true), every later share returns the existing row
    ///     (Created=false, affected == 0 on the real store).
    /// </summary>
    private sealed class RaceLosingShareStore : IMemoryStore
    {
        public List<string> CreatedShares { get; } = [];
        public Dictionary<string, string> HashPaths { get; } = new(StringComparer.Ordinal);
        private readonly HashSet<string> _createdByHash = new(StringComparer.Ordinal);

        public int SharedRows => _createdByHash.Count;

        public Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SharedIndex([], []));

        public Task<MemoryEntryResult> ShareAsync(string projectId, string hash,
            CancellationToken cancellationToken = default)
        {
            var entry = new MemoryEntry(hash, HashPaths.GetValueOrDefault(hash) ?? $"{hash}.md", "shared", "value", 0);
            if (_createdByHash.Contains(hash))
            {
                // The concurrent caller's insert landed first: the DO NOTHING loser reports
                // affected == 0 and re-reads the winner's row.
                return Task.FromResult(new MemoryEntryResult(entry, Created: false));
            }

            _createdByHash.Add(hash);
            CreatedShares.Add(hash);
            return Task.FromResult(new MemoryEntryResult(entry, Created: true));
        }

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> DeleteContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
            bool includeTtlRows, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> IngestFileAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<MemoryEntryResult> AddContentAsync(string projectId, string path, string content,
            string? context, string? sourceFile = null, string? section = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ReplaceFileAsync(string projectId, string path, string fileHash,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<int> DeleteSourcePathAsync(string projectId, string path,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> SetEntryTtlAsync(string projectId, string hash, int? ttlDays,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class SpyMetrics : IPromotionQueueMetrics
    {
        public List<(PromotionQueueStats Stats, int Capacity)> Snapshots { get; } = [];

        public void RecordEviction(string projectId, double victimScore, string reason) { }
        public void RecordPromoted(string projectId, double waitSeconds) { }
        public void RecordDiscarded(string projectId, double waitSeconds) { }
        public void RecordSnapshot(PromotionQueueStats stats, int capacity) => Snapshots.Add((stats, capacity));
    }

    private sealed class StubChunker : IChunker
    {
        public IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) =>
            text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
    }

    private sealed class ListLogger : ILogger<PromotionQueueService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    private sealed class RecordingMetrics : IPromotionQueueMetrics
    {
        public List<(PromotionQueueStats Stats, int Capacity)> Snapshots { get; } = [];

        public void RecordEviction(string projectId, double victimScore, string reason) { }
        public void RecordPromoted(string projectId, double waitSeconds) { }
        public void RecordDiscarded(string projectId, double waitSeconds) { }
        public void RecordSnapshot(PromotionQueueStats stats, int capacity) => Snapshots.Add((stats, capacity));
    }
}
