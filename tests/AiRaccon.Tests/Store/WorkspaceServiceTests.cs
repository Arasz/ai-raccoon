using AiRaccon.Core.Common;
using AiRaccon.Core.Memory;
using AiRaccon.Core.Workspace;
using AiRaccon.Infrastructure.Sqlite;
using AiRaccon.Infrastructure.Workspace;
using Shouldly;
using Xunit;

namespace AiRaccon.Tests.Store;

public class WorkspaceServiceTests
{
    [Fact]
    public async Task BeginAsync_ReturnsActiveWorkspace_WithWorkspaceContext()
    {
        var store = new FakeStore();
        var service = new WorkspaceService(store);

        var workspace = await service.BeginAsync("acme", "agent-a", "refactor", TestContext.Current.CancellationToken);

        workspace.ProjectId.ShouldBe("acme");
        workspace.Status.ShouldBe(WorkspaceStatus.Active);
        workspace.Context.ShouldStartWith("workspace:");
        workspace.Id.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetStatusAsync_ListsEntriesInTheWorkspaceContext()
    {
        var store = new FakeStore();
        store.EntriesByContext["workspace:ws-1"] =
            [new MemoryEntry("h1", "note.md", "workspace:ws-1", "draft", 1)];
        var service = new WorkspaceService(store);

        var entries = await service.GetStatusAsync("acme", "ws-1", TestContext.Current.CancellationToken);

        entries.Count.ShouldBe(1);
        store.LastListedContext.ShouldBe("workspace:ws-1");
    }

    [Fact]
    public async Task ConsolidateAsync_PromotesKeptHashes_ThenDeletesTheWorkspaceContext()
    {
        var store = new FakeStore();
        store.EntriesByContext["workspace:ws-1"] =
        [
            new MemoryEntry("h1", "note.md", "workspace:ws-1", "durable fact", 1),
            new MemoryEntry("h2", "todo.md", "workspace:ws-1", "noise", 2),
        ];
        var service = new WorkspaceService(store);

        var result = await service.ConsolidateAsync("acme", "ws-1", ["h1"], TestContext.Current.CancellationToken);

        result.Promoted.ShouldBe(1);
        result.Discarded.ShouldBe(2); // workspace context deleted entirely
        var promoted = store.PromotedContent.ShouldHaveSingleItem();
        promoted.Content.ShouldBe("durable fact");
        promoted.Path.ShouldBe("note.md");
        promoted.Context.ShouldBe("project:acme");
        store.DeletedContexts.ShouldContain("workspace:ws-1");
    }

    [Fact]
    public async Task ConsolidateAsync_WithAll_PromotesEveryEntry()
    {
        var store = new FakeStore();
        store.EntriesByContext["workspace:ws-1"] =
        [
            new MemoryEntry("h1", "a.md", "workspace:ws-1", "one", 1),
            new MemoryEntry("h2", "b.md", "workspace:ws-1", "two", 2),
        ];
        var service = new WorkspaceService(store);

        var result = await service.ConsolidateAsync("acme", "ws-1", ["all"], TestContext.Current.CancellationToken);

        result.Promoted.ShouldBe(2);
        store.PromotedContent.Count.ShouldBe(2);
    }

    [Fact]
    public async Task DiscardAsync_DeletesTheWorkspaceContext()
    {
        var store = new FakeStore();
        store.EntriesByContext["workspace:ws-1"] =
            [new MemoryEntry("h9", "scratch.md", "workspace:ws-1", "draft", 1)];
        var service = new WorkspaceService(store);

        var deleted = await service.DiscardAsync("acme", "ws-1", TestContext.Current.CancellationToken);

        deleted.ShouldBe(1);
        store.DeletedContexts.ShouldContain("workspace:ws-1");
    }

    private sealed class FakeStore : IMemoryStore
    {
        public Dictionary<string, IReadOnlyList<MemoryEntry>> EntriesByContext { get; } = [];

        public List<(string Path, string Content, string Context)> PromotedContent { get; } = [];

        public List<string> DeletedContexts { get; } = [];

        public string? LastListedContext { get; private set; }

        public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryEntry("new-hash", request.Content, request.Context ?? ContextNaming.ProjectContext(request.ProjectId), request.Content, 1));

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context, CancellationToken cancellationToken = default)
        {
            PromotedContent.Add((path, content, context ?? ContextNaming.ProjectContext(projectId)));
            return Task.FromResult(new MemoryEntry("new-hash", path, context ?? ContextNaming.ProjectContext(projectId), content, 1));
        }

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<int> DeleteContextAsync(string projectId, string context, CancellationToken cancellationToken = default)
        {
            DeletedContexts.Add(context);
            return Task.FromResult(EntriesByContext.TryGetValue(context, out var entries) ? entries.Count : 0);
        }

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<MemoryEntry> ShareAsync(string projectId, string hash, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<int> IngestFileAsync(string projectId, string path, string? context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string projectId, string provider, string model, string? apiKey, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context, CancellationToken cancellationToken = default)
        {
            LastListedContext = context;
            return Task.FromResult(EntriesByContext.TryGetValue(context, out var entries) ? entries : []);
        }
    }
}
