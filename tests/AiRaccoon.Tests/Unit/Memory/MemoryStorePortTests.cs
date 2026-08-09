using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
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
            "local", "/models/custom.onnx", null, TestContext.Current.CancellationToken);

        config.Engine.ShouldBe("local");
        store.Configured.ShouldBe(("local", "/models/custom.onnx", null));
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

    [Fact]
    public async Task GetMetadataAsync_IsPartOfThePort_AndReturnsOnRowRating()
    {
        var store = new RecordingStore();

        var metadata = await store.GetMetadataAsync("acme", "h1", TestContext.Current.CancellationToken);

        metadata.ShouldNotBeNull();
        metadata.Rating.ShouldBe(RatingPolicy.DefaultBaseScore);
    }

    [Fact]
    public async Task DeleteSourcePathAsync_IsPartOfThePort_AndCarriesProjectAndPath()
    {
        var store = new RecordingStore();

        var deleted = await store.DeleteSourcePathAsync("acme", "/repo/docs/api.md",
            TestContext.Current.CancellationToken);

        deleted.ShouldBe(0);
        store.DeletedSourcePath.ShouldBe(("acme", "/repo/docs/api.md"));
    }

    [Fact]
    public async Task ExtractCandidatesAsync_IsPartOfThePort_AndCarriesProjectAndTtlFlag()
    {
        var store = new RecordingStore();
        store.Candidates.Add(new ExtractionCandidateRow("h", "h.md", "v", null, 0.5, 0,
            DateTimeOffset.UnixEpoch, null));

        var rows = await store.ExtractCandidatesAsync("acme", true, TestContext.Current.CancellationToken);

        store.Extracted.ShouldBe(("acme", true));
        rows.ShouldHaveSingleItem();
        rows[0].Hash.ShouldBe("h");
    }

    [Fact]
    public async Task GetSharedIndexAsync_IsPartOfThePort_AndReturnsValuesAndPaths()
    {
        var store = new RecordingStore();
        store.Index = new SharedIndex(["v1"], ["shared/a.md"]);

        var index = await store.GetSharedIndexAsync(TestContext.Current.CancellationToken);

        store.SharedIndexCalls.ShouldBe(1);
        index.Values.ShouldBe(["v1"]);
        index.Paths.ShouldBe(["shared/a.md"]);
    }

    [Fact]
    public async Task GetProjectIdsAsync_IsPartOfThePort_AndReturnsTheProjectList()
    {
        var store = new RecordingStore();

        var projects = await store.GetProjectIdsAsync(TestContext.Current.CancellationToken);

        projects.ShouldBe(["acme"]);
    }

    private sealed class RecordingStore : IMemoryStore
    {
        public (string ProjectId, string Hash)? Shared { get; private set; }

        public string? ListedFilesFor { get; private set; }

        public (string Path, string? Context)? IngestedFile { get; private set; }

        public (string Path, string? Context)? IngestedDirectory { get; private set; }

        public (string Provider, string? Model, string? BaseUrl)? Configured { get; private set; }

        public string? ListedContext { get; private set; }

        public (string ProjectId, string Path)? DeletedSourcePath { get; private set; }

        public Task<MemoryEntry>
            WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<int> DeleteContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<MemoryEntry> ShareAsync(string projectId, string hash,
            CancellationToken cancellationToken = default)
        {
            Shared = (projectId, hash);
            return Task.FromResult(new MemoryEntry(hash, "notes.md", ContextNaming.SharedContext, "value", 1));
        }

        public (string ProjectId, bool IncludeTtlRows)? Extracted { get; private set; }

        public List<ExtractionCandidateRow> Candidates { get; } = [];

        public SharedIndex Index { get; set; } = new([], []);

        public int SharedIndexCalls { get; private set; }

        public Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
            bool includeTtlRows, CancellationToken cancellationToken = default)
        {
            Extracted = (projectId, includeTtlRows);
            return Task.FromResult<IReadOnlyList<ExtractionCandidateRow>>(Candidates);
        }

        public Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default)
        {
            SharedIndexCalls++;
            return Task.FromResult(Index);
        }

        public List<string> ProjectIds { get; } = ["acme"];

        public Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(ProjectIds);

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
            string provider, string? model, string? baseUrl,
            CancellationToken cancellationToken = default)
        {
            Configured = (provider, model, baseUrl);
            return Task.FromResult(new EmbeddingConfig(provider, model ?? "bundled", provider == "local" ? "local" : "remote"));
        }

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbedPendingResult(7, 3));

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
            string? sourceFile = null, string? section = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default)
        {
            ListedContext = context;
            return Task.FromResult<IReadOnlyList<MemoryEntry>>(
                [new MemoryEntry("h1", "note.md", context, "value", 1)]);
        }

        public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EntryMetadata?>(new EntryMetadata(0.5, null));

        public Task<bool> ReplaceFileAsync(string projectId, string path, string fileHash,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> DeleteSourcePathAsync(string projectId, string path,
            CancellationToken cancellationToken = default)
        {
            DeletedSourcePath = (projectId, path);
            return Task.FromResult(0);
        }

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
}
