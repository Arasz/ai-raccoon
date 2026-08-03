using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Store;

public class SweepServiceTests
{
    [Fact]
    public async Task SweepAsync_DryRun_ReportsCandidates_WithoutDeleting()
    {
        var store = new FakeStore();
        var meta = new FakeMetaStore(rating: 0.1);
        var service = new SweepService(store, meta);

        var outcome = await service.SweepAsync("acme", 0.3, 30, dryRun: true, TestContext.Current.CancellationToken);

        outcome.Candidates.Count.ShouldBe(1);
        outcome.Candidates[0].Hash.ShouldBe("old-low");
        outcome.DeletedHashes.ShouldBeEmpty();
        store.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task SweepAsync_RealRun_DeletesOnlyCandidates()
    {
        var store = new FakeStore();
        var meta = new FakeMetaStore(rating: 0.1);
        var service = new SweepService(store, meta);

        var outcome = await service.SweepAsync("acme", 0.3, 30, dryRun: false, TestContext.Current.CancellationToken);

        outcome.DeletedHashes.ShouldContain("old-low");
        store.Deleted.ShouldContain("old-low");
    }

    [Fact]
    public async Task SweepAsync_HighlyRatedEntry_Survives()
    {
        var store = new FakeStore();
        var meta = new FakeMetaStore(rating: 0.9);
        var service = new SweepService(store, meta);

        var outcome = await service.SweepAsync("acme", 0.3, 30, dryRun: false, TestContext.Current.CancellationToken);

        outcome.Candidates.ShouldBeEmpty();
        store.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task SweepAsync_NeverSweepsSharedEntries()
    {
        var store = new FakeStore();
        var meta = new FakeMetaStore(rating: 0.1);
        // The same hash exists in both the project and the shared tier.
        store.SharedHashes.Add("old-low");
        var service = new SweepService(store, meta);

        await service.SweepAsync("acme", 0.3, 30, dryRun: false, TestContext.Current.CancellationToken);

        store.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task SweepAsync_EntryWithoutMetaRow_IsNotCandidate()
    {
        var store = new FakeStore();
        var meta = new FakeMetaStore(rating: null); // no meta row exists
        var service = new SweepService(store, meta);

        var outcome = await service.SweepAsync("acme", 0.3, 30, dryRun: false, TestContext.Current.CancellationToken);

        // Missing meta falls back to DefaultBaseScore (0.5) — above a 0.3 threshold, so the
        // entry must NOT be swept just because it was never searched (FR-MEM-1.15).
        outcome.Candidates.ShouldBeEmpty();
        store.Deleted.ShouldBeEmpty();
    }

    private sealed class FakeStore : IMemoryStore
    {
        public List<string> Deleted { get; } = [];

        public List<string> ListedContexts { get; } = [];

        public HashSet<string> SharedHashes { get; } = new(StringComparer.Ordinal);

        public Task<MemoryEntry>
            WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default)
        {
            Deleted.Add(hash);
            return Task.FromResult(true);
        }

        public Task<int> DeleteContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<MemoryEntry> ShareAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> IngestFileAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string projectId, string provider, string model,
            string? apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default)
        {
            ListedContexts.Add(context);
            if (context == ContextNaming.SharedContext)
            {
                return Task.FromResult<IReadOnlyList<MemoryEntry>>(
                    [.. SharedHashes.Select(h => new MemoryEntry(h, "shared.md", context, "value", 0))]);
            }

            var age = context == ContextNaming.ProjectContext("acme") ? 40 : 0;
            return Task.FromResult<IReadOnlyList<MemoryEntry>>(
            [
                new MemoryEntry("old-low", "note.md", context, "value",
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds() - age * 86_400)
            ]);
        }
    }

    private sealed class FakeMetaStore(double? rating) : MetaStore(null!)
    {
        public override Task<MetaEntry?> GetEntryAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<MetaEntry?>(rating is null
                ? null
                : new MetaEntry(hash, projectId, null, null, 0, 0, null, rating.Value, null));

        public override Task<bool> DeleteAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }
}
