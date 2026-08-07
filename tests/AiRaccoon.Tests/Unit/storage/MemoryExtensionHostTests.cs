using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using AiRaccoon.Core.Workspace;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Workspace;
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

    [Fact]
    public async Task DeleteSourcePath_RunsOnDeleteHookWithThePath_ThenDelegatesToStore()
    {
        var recorder = new RecordingExtension("first");
        var inner = new StubStore();
        var host = new MemoryExtensionHost(inner, [recorder]);

        var deleted = await host.DeleteSourcePathAsync("acme", "/repo/docs/api.md",
            TestContext.Current.CancellationToken);

        recorder.Calls.ShouldContain("OnDeleteAsync");
        recorder.LastDeletedPath.ShouldBe("/repo/docs/api.md");
        inner.DeletedSourcePaths.ShouldHaveSingleItem();
        inner.DeletedSourcePaths[0].ShouldBe(("acme", "/repo/docs/api.md"));
        deleted.ShouldBe(0);
    }

    [Fact]
    public async Task OnSourceChanged_DispatchesToAllExtensionsInRegistrationOrder()
    {
        var first = new RecordingExtension("first");
        var second = new RecordingExtension("second");
        var host = new MemoryExtensionHost(new StubStore(), [first, second]);

        await host.OnSourceChangedAsync(
            new SourceChangedContext("acme", "/repo/docs/api.md", SourceChangeKind.Changed),
            TestContext.Current.CancellationToken);

        first.Calls.ShouldContain("OnSourceChangedAsync");
        second.Calls.ShouldContain("OnSourceChangedAsync");
        first.SourceChanges.ShouldHaveSingleItem();
        first.SourceChanges[0].ShouldBe(("acme", "/repo/docs/api.md", SourceChangeKind.Changed));
    }

    [Fact]
    public async Task OnSourceChanged_DefaultNoOpExtension_IsTolerated()
    {
        var recorder = new RecordingExtension("recorder");
        var host = new MemoryExtensionHost(new StubStore(), [new DefaultNoOpExtension(), recorder]);

        await host.OnSourceChangedAsync(
            new SourceChangedContext("acme", "/repo/other.md", SourceChangeKind.Deleted),
            TestContext.Current.CancellationToken);

        recorder.SourceChanges.ShouldHaveSingleItem();
    }

    /// <summary>Characterization test: SweepService deletes through IMemoryStore, which the host wraps — an
    /// extension observing forgetting still can via OnDeleteAsync, with no dedicated sweep hook (ADR-0013).</summary>
    [Fact]
    public async Task Sweep_DeletesThroughHost_ReachesExtensionOnDeleteHook()
    {
        var recorder = new RecordingExtension("first");
        var inner = new SweepableStore();
        var host = new MemoryExtensionHost(inner, [recorder]);
        var sweepService = new SweepService(host, TimeProvider.System);

        await sweepService.SweepAsync("acme", 0.3, dryRun: false, TestContext.Current.CancellationToken);

        recorder.Calls.ShouldContain("OnDeleteAsync");
    }

    /// <summary>Characterization test: WorkspaceService.ConsolidateAsync deletes the workspace context through
    /// IMemoryStore, which the host wraps — an extension observing consolidation still can via OnDeleteAsync,
    /// with no dedicated consolidation hook (ADR-0013).</summary>
    [Fact]
    public async Task Consolidate_DeletesWorkspaceContextThroughHost_ReachesExtensionOnDeleteHook()
    {
        var recorder = new RecordingExtension("first");
        var inner = new WorkspaceableStore();
        var host = new MemoryExtensionHost(inner, [recorder]);
        var workspaceService = new WorkspaceService(host, new NoOpWorkspaceStore(), TimeProvider.System);

        await workspaceService.ConsolidateAsync("acme", "ws-1", [], TestContext.Current.CancellationToken);

        recorder.Calls.ShouldContain("OnDeleteAsync");
    }

    private static string CreateTempRoot() =>
        TestData.CreateTempRoot("airaccoon-store-tests");

    private sealed class RecordingExtension(string name) : IMemoryExtension
    {
        public List<string> Calls { get; } = [];
        public string Name { get; } = name;

        public List<(string ProjectId, string Path, SourceChangeKind ChangeKind)> SourceChanges { get; } = [];

        public string? LastDeletedPath { get; private set; }

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
            LastDeletedPath = context.Path;
            return Task.CompletedTask;
        }

        public Task OnSourceChangedAsync(SourceChangedContext context, CancellationToken cancellationToken)
        {
            Calls.Add("OnSourceChangedAsync");
            SourceChanges.Add((context.ProjectId, context.Path, context.ChangeKind));
            return Task.CompletedTask;
        }
    }

    /// <summary>An extension that predates the source-change hook: it inherits the interface's default no-op.</summary>
    private sealed class DefaultNoOpExtension : IMemoryExtension
    {
        public string Name => "no-op";

        public Task OnWriteAsync(WriteContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OnSearchAsync(SearchContext context, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task OnDeleteAsync(DeleteContext context, CancellationToken cancellationToken) => Task.CompletedTask;
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



    public Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
    public Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
        bool includeTtlRows, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();

    public Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException();
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

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingConfig(provider, model ?? "bundled", "local"));

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbedPendingResult(0, 0));

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
            string? sourceFile = null, string? section = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

        public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EntryMetadata?>(new EntryMetadata(0.5, null));

        public List<(string ProjectId, string Path)> DeletedSourcePaths { get; } = [];

        public Task<int> DeleteSourcePathAsync(string projectId, string path,
            CancellationToken cancellationToken = default)
        {
            DeletedSourcePaths.Add((projectId, path));
            return Task.FromResult(0);
        }

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;


        public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task SetEntryTtlAsync(string projectId, string hash, double ttlDays,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>Minimal IMemoryStore backing one sweep-eligible entry, for SweepService's characterization test.</summary>
    private sealed class SweepableStore : IMemoryStore
    {
        public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<int> DeleteContextAsync(string projectId, string context,
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

        public Task<MemoryEntry> ShareAsync(string projectId, string hash,
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

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
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

        public Task SetEntryTtlAsync(string projectId, string hash, double ttlDays,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    /// <summary>Minimal IMemoryStore backing one workspace entry, for WorkspaceService's characterization test.</summary>
    private sealed class WorkspaceableStore : IMemoryStore
    {
        public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> DeleteContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(1);

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

        public Task<MemoryEntry> ShareAsync(string projectId, string hash,
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

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
            string? sourceFile = null, string? section = null, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemoryEntry>>(
                [new MemoryEntry("h1", "note.md", context, "draft", 1)]);

        public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task SetEntryTtlAsync(string projectId, string hash, double ttlDays,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class NoOpWorkspaceStore : IWorkspaceStore
    {
        public Task BeginAsync(string projectId, string workspaceId, DateTimeOffset startedAt,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CloseAsync(string projectId, string workspaceId, WorkspaceStatus status, DateTimeOffset closedAt,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
