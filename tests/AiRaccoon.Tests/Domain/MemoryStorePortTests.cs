using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Domain;

public class MemoryStorePortTests
{
    [Fact]
    public async Task ShareAsync_IsPartOfThePort_AndReturnsTheSharedEntry()
    {
        var store = new RecordingStore();

        var entry = await store.ShareAsync("acme", "abc123", TestContext.Current.CancellationToken);

        entry.Context.ShouldBe(ContextNaming.SharedContext);
        store.Shared.ShouldBe(("acme", "abc123"));
    }

    [Fact]
    public async Task ListFilesAsync_IsPartOfThePort_AndReturnsTheJsonTree()
    {
        var store = new RecordingStore();

        var tree = await store.ListFilesAsync("acme", TestContext.Current.CancellationToken);

        tree.ShouldBe("{\"root\":\"\"}");
        store.ListedFilesFor.ShouldBe("acme");
    }

    [Fact]
    public async Task IngestFileAsync_IsPartOfThePort_AndCarriesPathAndContext()
    {
        var store = new RecordingStore();

        await store.IngestFileAsync("acme", "docs/api.md", "docs:api", TestContext.Current.CancellationToken);

        store.IngestedFile.ShouldBe(("docs/api.md", "docs:api"));
    }

    [Fact]
    public async Task IngestDirectoryAsync_IsPartOfThePort_AndCarriesPathAndContext()
    {
        var store = new RecordingStore();

        await store.IngestDirectoryAsync("acme", "/docs", "project-docs", TestContext.Current.CancellationToken);

        store.IngestedDirectory.ShouldBe(("/docs", "project-docs"));
    }

    [Fact]
    public async Task ConfigureEmbeddingAsync_IsPartOfThePort_AndReturnsTheConfig()
    {
        var store = new RecordingStore();

        var config = await store.ConfigureEmbeddingAsync(
            "acme", "local", "/models/nomic.gguf", null, TestContext.Current.CancellationToken);

        config.Engine.ShouldBe("local");
        store.Configured.ShouldBe(("local", "/models/nomic.gguf", null));
    }

    [Fact]
    public async Task EmbedPendingAsync_IsPartOfThePort_AndReportsProcessedAndPending()
    {
        var store = new RecordingStore();

        var result = await store.EmbedPendingAsync("acme", 10, TestContext.Current.CancellationToken);

        result.Processed.ShouldBe(7);
        result.Pending.ShouldBe(3);
    }

    [Fact]
    public async Task ListContextAsync_IsPartOfThePort_AndReturnsEntriesForTheContext()
    {
        var store = new RecordingStore();

        var entries = await store.ListContextAsync("acme", "workspace:ws-1", TestContext.Current.CancellationToken);

        entries.Count.ShouldBe(1);
        store.ListedContext.ShouldBe("workspace:ws-1");
    }

    private sealed class RecordingStore : IMemoryStore
    {
        public (string ProjectId, string Hash)? Shared { get; private set; }

        public string? ListedFilesFor { get; private set; }

        public (string Path, string? Context)? IngestedFile { get; private set; }

        public (string Path, string? Context)? IngestedDirectory { get; private set; }

        public (string Provider, string Model, string? ApiKey)? Configured { get; private set; }

        public string? ListedContext { get; private set; }

        public Task<MemoryEntry>
            WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> DeleteContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<MemoryEntry> ShareAsync(string projectId, string hash,
            CancellationToken cancellationToken = default)
        {
            Shared = (projectId, hash);
            return Task.FromResult(new MemoryEntry(hash, "notes.md", ContextNaming.SharedContext, "value", 1));
        }

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default)
        {
            ListedFilesFor = projectId;
            return Task.FromResult("{\"root\":\"\"}");
        }

        public Task<int> IngestFileAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default)
        {
            IngestedFile = (path, context);
            return Task.FromResult(1);
        }

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default)
        {
            IngestedDirectory = (path, context);
            return Task.FromResult(3);
        }

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(
            string projectId, string provider, string model, string? apiKey,
            CancellationToken cancellationToken = default)
        {
            Configured = (provider, model, apiKey);
            return Task.FromResult(new EmbeddingConfig(provider, model, provider == "local" ? "local" : "remote"));
        }

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbedPendingResult(7, 3));

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default)
        {
            ListedContext = context;
            return Task.FromResult<IReadOnlyList<MemoryEntry>>(
                [new MemoryEntry("h1", "note.md", context, "value", 1)]);
        }
    }
}
