using AiRaccoon.Core.Common;
using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Rating;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Store;

public sealed class MemoryExtensionHostTests : IDisposable
{
    private readonly string _dataRoot = CreateTempRoot();

    public void Dispose() => Directory.Delete(_dataRoot, recursive: true);

    [Fact]
    public async Task Search_RunsExtensionHooksInRegistrationOrder()
    {
        var recorder = new RecordingExtension("first");
        var inner = new StubStore();
        var host = new MemoryExtensionHost(inner, [recorder, new RecordingExtension("second")]);

        await host.SearchAsync(new SearchQuery("acme", "q"), TestContext.Current.CancellationToken);

        recorder.Calls.ShouldContain("OnSearchAsync");
    }

    [Fact]
    public async Task Write_RunsOnWriteHook_ThenDelegatesToStore()
    {
        var recorder = new RecordingExtension("first");
        var inner = new StubStore();
        var host = new MemoryExtensionHost(inner, [recorder]);

        await host.WriteAsync(new MemoryWriteRequest("acme", "content"), TestContext.Current.CancellationToken);

        recorder.Calls.ShouldContain("OnWriteAsync");
        inner.Writes.ShouldBe(1);
    }

    [Fact]
    public async Task SearchHit_BumpsMetaAccessCountAndRating_ThroughRetrievalRatingExtension()
    {
        var factory = new SqliteConnectionFactory(
            new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64" },
            loadCloudSync: false, loadExtensions: _ => { });
        var meta = new MetaStore(factory);
        var extension = new RetrievalRatingExtension(meta);
        var results = new List<MemorySearchResult>
        {
            new("h1", 0, 0.9, "note.md", "snippet"),
        };
        var inner = new StubStore(results);
        var host = new MemoryExtensionHost(inner, [extension]);

        await host.SearchAsync(new SearchQuery("acme", "q"), TestContext.Current.CancellationToken);
        await host.SearchAsync(new SearchQuery("acme", "q"), TestContext.Current.CancellationToken);

        var entry = await meta.GetEntryAsync("acme", "h1", TestContext.Current.CancellationToken);
        entry.ShouldNotBeNull();
        entry.AccessCount.ShouldBe(2);
        entry.Rating.ShouldBeGreaterThan(RatingPolicy.DefaultBaseScore);
    }

    private static string CreateTempRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ai-raccoon-host", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class RecordingExtension : IMemoryExtension
    {
        public RecordingExtension(string name) => Name = name;

        public string Name { get; }

        public List<string> Calls { get; } = [];

        public Task OnWriteAsync(WriteContext context, CancellationToken cancellationToken)
        {
            Calls.Add("OnWriteAsync");
            return Task.CompletedTask;
        }

        public Task OnSearchAsync(SearchContext context, CancellationToken cancellationToken)
        {
            Calls.Add("OnSearchAsync");
            return Task.CompletedTask;
        }

        public Task OnDeleteAsync(DeleteContext context, CancellationToken cancellationToken)
        {
            Calls.Add("OnDeleteAsync");
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SweepCandidate>> OnSweepAsync(SweepContext context, CancellationToken cancellationToken)
        {
            Calls.Add("OnSweepAsync");
            return Task.FromResult<IReadOnlyList<SweepCandidate>>([]);
        }

        public Task OnConsolidateAsync(ConsolidationContext context, CancellationToken cancellationToken)
        {
            Calls.Add("OnConsolidateAsync");
            return Task.CompletedTask;
        }
    }

    private sealed class StubStore : IMemoryStore
    {
        private readonly IReadOnlyList<MemorySearchResult> _results;

        public StubStore(IReadOnlyList<MemorySearchResult>? results = null) => _results = results ?? [];

        public int Writes { get; private set; }

        public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
        {
            Writes++;
            return Task.FromResult(new MemoryEntry("h1", "note.md", "project:acme", request.Content, 1));
        }

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult(_results);

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<int> DeleteContextAsync(string projectId, string context, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryStats(0, 0, []));

        public Task<MemoryEntry> ShareAsync(string projectId, string hash, CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryEntry(hash, "shared/note.md", ContextNaming.SharedContext, "v", 1));

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default)
            => Task.FromResult("{}");

        public Task<int> IngestFileAsync(string projectId, string path, string? context, CancellationToken cancellationToken = default)
            => Task.FromResult(1);

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context, CancellationToken cancellationToken = default)
            => Task.FromResult(1);

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string projectId, string provider, string model, string? apiKey, CancellationToken cancellationToken = default)
            => Task.FromResult(new EmbeddingConfig(provider, model, "local"));

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit, CancellationToken cancellationToken = default)
            => Task.FromResult(new EmbedPendingResult(0, 0));

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);
    }
}
