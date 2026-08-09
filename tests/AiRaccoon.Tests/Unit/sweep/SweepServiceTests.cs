using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Degradation;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.sweep;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SweepServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static SweepService Service(FakeStore store) => new(store, new FakeTimeProvider(FixedNow));

    [Fact]
    public async Task SweepAsync_DryRun_ReportsCandidates_WithoutDeleting()
    {
        var store = new FakeStore { Rating = 0.1 };
        var service = Service(store);

        var outcome = await service.SweepAsync("acme", 0.3, true, TestContext.Current.CancellationToken);

        outcome.Candidates.Count.ShouldBe(1);
        outcome.Candidates[0].Hash.ShouldBe("old-low");
        outcome.DeletedHashes.ShouldBeEmpty();
        store.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task SweepAsync_RealRun_DeletesOnlyCandidates()
    {
        var store = new FakeStore { Rating = 0.1 };
        var service = Service(store);

        var outcome = await service.SweepAsync("acme", 0.3, false, TestContext.Current.CancellationToken);

        outcome.DeletedHashes.ShouldContain("old-low");
        store.Deleted.ShouldContain("old-low");
    }

    [Fact]
    public async Task SweepAsync_HighlyRatedEntry_Survives()
    {
        var store = new FakeStore { Rating = 0.9 };
        var service = Service(store);

        var outcome = await service.SweepAsync("acme", 0.3, false, TestContext.Current.CancellationToken);

        outcome.Candidates.ShouldBeEmpty();
        store.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task SweepAsync_EntryWithoutPerEntryTtl_IsNeverSwept()
    {
        // The global sweep.ttl_days knob was removed: only entries with an explicit
        // per-entry TTL can degrade.
        var store = new FakeStore { Rating = 0.1, TtlDays = null };
        var service = Service(store);

        var outcome = await service.SweepAsync("acme", 0.3, false, TestContext.Current.CancellationToken);

        outcome.Candidates.ShouldBeEmpty();
        store.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task SweepAsync_NeverSweepsSharedEntries()
    {
        var store = new FakeStore { Rating = 0.1 };
        // The same hash exists in both the project and the shared tier.
        store.SharedHashes.Add("old-low");
        var service = Service(store);

        await service.SweepAsync("acme", 0.3, false, TestContext.Current.CancellationToken);

        store.Deleted.ShouldBeEmpty();
    }

    [Fact]
    public async Task SweepAsync_EntryWithoutMetadataRow_IsNotCandidate()
    {
        var store = new FakeStore { Rating = null }; // no on-row metadata exists
        var service = Service(store);

        var outcome = await service.SweepAsync("acme", 0.3, false, TestContext.Current.CancellationToken);

        // Missing metadata falls back to DefaultBaseScore (0.5) — above a 0.3 threshold, so the
        // entry must NOT be swept just because it was never searched (FR-MEM-1.15; see docs/work/features-agent-memory/spec-issue-1.md).
        outcome.Candidates.ShouldBeEmpty();
        store.Deleted.ShouldBeEmpty();
    }

    /// <summary>Rescued from the deleted extension-host test suite (ADR-0016): SweepService deletes
    /// through IMemoryStore, not around it — the sweep candidate it selects is the same hash the store
    /// records as deleted, with no side channel between selection and deletion.</summary>
    [Fact]
    public async Task SweepAsync_DeletesThroughTheStore_NotAroundIt()
    {
        var store = new SweepableStore();
        var service = new SweepService(store, TimeProvider.System);

        await service.SweepAsync("acme", 0.3, dryRun: false, TestContext.Current.CancellationToken);

        store.Deleted.ShouldContain("old-low");
    }

    private sealed class FakeStore : IMemoryStore
    {
        public double? Rating { get; set; } = 0.1;

        public int? TtlDays { get; set; } = 30;

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

        public Task<bool> ReplaceFileAsync(string projectId, string path, string fileHash,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> DeleteSourcePathAsync(string projectId, string path,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotImplementedException();



    public Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
    public Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
        bool includeTtlRows, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
        public Task<AddContentResult> ShareAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<int> IngestFileAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<AddContentResult> AddContentAsync(string projectId, string path, string content, string? context,
            string? sourceFile = null, string? section = null, CancellationToken cancellationToken = default) =>
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
                    FixedNow.ToUnixTimeSeconds() - age * 86_400)
            ]);
        }

        public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EntryMetadata?>(Rating is null
                ? null
                : new EntryMetadata(Rating.Value, TtlDays));

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;


        public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> SetEntryTtlAsync(string projectId, string hash, int? ttlDays,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    /// <summary>Minimal recording IMemoryStore backing one sweep-eligible entry ("old-low", rating 0.1,
    /// ttlDays 1), moved from the deleted extension-host test suite (ADR-0016).</summary>
    private sealed class SweepableStore : IMemoryStore
    {
        public List<string> Deleted { get; } = [];

        public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default) =>
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

        public Task<bool> ReplaceFileAsync(string projectId, string path, string fileHash,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> DeleteSourcePathAsync(string projectId, string path,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
            bool includeTtlRows, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<AddContentResult> ShareAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<int> IngestFileAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<AddContentResult> AddContentAsync(string projectId, string path, string content, string? context,
            string? sourceFile = null, string? section = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default)
        {
            if (context == ContextNaming.SharedContext)
            {
                return Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
            }

            var ancient = DateTimeOffset.UtcNow.AddYears(-5).ToUnixTimeSeconds();
            return Task.FromResult<IReadOnlyList<MemoryEntry>>(
                [new MemoryEntry("old-low", "note.md", context, "value", ancient)]);
        }

        public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EntryMetadata?>(new EntryMetadata(0.1, 1));

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<bool> SetEntryTtlAsync(string projectId, string hash, int? ttlDays,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
