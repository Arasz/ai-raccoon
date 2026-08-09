using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Promotion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Extraction;

/// <summary>
///     PromoteAsync snapshots the queue, then drains and shares each row; a row gone by drain
///     time — raced by a concurrent discard — must never be shared. Fakes make the race
///     deterministic instead of depending on real thread timing.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PromotionQueueServicePromoteRaceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task PromoteAsync_NeverShares_ARowAnotherCallerAlreadyClaimed()
    {
        var queue = new RaceyQueueStore();
        queue.Rows.Add(new PromotionQueueRow("acme", "h1", "h1.md", "fact one", null, 5.0, [], 0, 0));
        queue.Rows.Add(new PromotionQueueRow("acme", "h2", "h2.md", "fact two", null, 3.0, [], 0, 0));
        // Simulates memory_promotion_discard("acme", "h1") winning the race between this
        // PromoteAsync's ListAsync snapshot and the moment it reaches h1's turn to drain.
        queue.AlreadyGoneHashes.Add("h1");

        var store = new RecordingShareStore();
        var metrics = new SpyMetrics();
        var service = new PromotionQueueService(queue, store, new UniformCountEvictionPolicy(),
            metrics, NullLogger<PromotionQueueService>.Instance, new FakeTimeProvider(FixedNow));

        var outcome = await service.PromoteAsync(["acme"], 10, TestContext.Current.CancellationToken);

        store.SharedHashes.ShouldNotContain("h1",
            "h1 was already claimed by a concurrent discard — sharing it would resurrect a rejected " +
            "candidate into the sweep-exempt shared tier with no way to undo it");
        outcome.PromotedHashes.ShouldNotContain("h1");
        store.SharedHashes.ShouldBe(["h2"]);
        outcome.PromotedHashes.ShouldBe(["h2"]);
        metrics.Snapshots.ShouldHaveSingleItem().Stats.TotalCount.ShouldBe(0,
            "h1 was claimed by a concurrent discard and h2 was drained by this call — the store is empty; " +
            "RecordSnapshot reports its real state, not this call's own delta");
    }

    private sealed class RaceyQueueStore : IPromotionQueueStore
    {
        public List<PromotionQueueRow> Rows { get; } = [];
        public HashSet<string> AlreadyGoneHashes { get; } = new(StringComparer.Ordinal);

        public Task<int> UpsertAsync(string projectId, IReadOnlyList<QueueCandidate> rows,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PromotionQueueRow>> ListAsync(string? projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PromotionQueueRow>>(
                Rows.Where(r => projectId is null || r.ProjectId == projectId).ToList());

        public Task<IReadOnlyList<PromotionQueueRow>> DiscardAsync(string projectId, string? hash,
            CancellationToken cancellationToken = default)
        {
            if (hash is not null && AlreadyGoneHashes.Contains(hash))
            {
                // The concurrent caller's claim already applied to the store — this call gets
                // nothing back, but the row is genuinely gone by the time GetStatsAsync runs.
                Rows.RemoveAll(r => r.ProjectId == projectId && r.Hash == hash);
                return Task.FromResult<IReadOnlyList<PromotionQueueRow>>([]);
            }

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
    }

    private sealed class RecordingShareStore : IMemoryStore
    {
        public List<string> SharedHashes { get; } = [];

        public Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SharedIndex([], []));

        public Task<MemoryEntry> ShareAsync(string projectId, string hash,
            CancellationToken cancellationToken = default)
        {
            SharedHashes.Add(hash);
            return Task.FromResult(new MemoryEntry(hash, $"{hash}.md", "shared", "value", 0));
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

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
            string? sourceFile = null, string? section = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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

        public Task<int> DeleteSourcePathAsync(string projectId, string path,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetEntryTtlAsync(string projectId, string hash, double ttlDays,
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
}
