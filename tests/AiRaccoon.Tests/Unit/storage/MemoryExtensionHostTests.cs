using AiRaccoon.Core.Common;
using AiRaccoon.Core.Degradation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.storage;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MemoryExtensionHostTests
{
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
    public async Task Search_DelegatesToInnerStore_AndRunsExtensionsAfter()
    {
        var recorder = new RecordingExtension("first");
        var inner = new StubStore([new MemorySearchResult("h1", 0, 0.9, "note.md", "s")]);
        var host = new MemoryExtensionHost(inner, [recorder]);

        var results = await host.SearchAsync(new SearchQuery("acme", "q"), TestContext.Current.CancellationToken);

        results.ShouldHaveSingleItem();
        recorder.Calls.ShouldContain("OnSearchAsync");
    }

    private static string CreateTempRoot() =>
        TestData.CreateTempRoot("airaccoon-store-tests");

    private sealed class RecordingExtension(string name) : IMemoryExtension
    {
        public List<string> Calls { get; } = [];
        public string Name { get; } = name;

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

        public Task<IReadOnlyList<SweepCandidate>> OnSweepAsync(SweepContext context,
            CancellationToken cancellationToken)
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

    private sealed class StubStore(IReadOnlyList<MemorySearchResult>? results = null) : IMemoryStore
    {
        private readonly IReadOnlyList<MemorySearchResult> _results = results ?? [];

        public int Writes { get; private set; }

        public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
        {
            Writes++;
            return Task.FromResult(new MemoryEntry("h1", "note.md", "project:acme", request.Content, 1));
        }

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_results);

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<int> DeleteContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(new MemoryStats(0, 0, []));

        public Task<MemoryEntry> ShareAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEntry(hash, "shared/note.md", ContextNaming.SharedContext, "v", 1));

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult("{}");

        public Task<int> IngestFileAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(1);

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(1);

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string projectId, string provider, string? model,
            string? baseUrl, string? apiKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingConfig(provider, model ?? "bundled", "local"));

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbedPendingResult(0, 0));

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

        public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EntryMetadata?>(new EntryMetadata(0.5, null));

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetEntryTtlAsync(string projectId, string hash, double ttlDays,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
