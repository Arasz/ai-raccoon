using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Workspace;
using AiRaccoon.Infrastructure.Workspace;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Store;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class WorkspaceServiceTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static WorkspaceService Service(IMemoryStore store, out FakeWorkspaceStore workspaceStore)
    {
        workspaceStore = new FakeWorkspaceStore();
        return new WorkspaceService(store, workspaceStore, new FakeTimeProvider(FixedNow));
    }

    [Fact]
    public async Task BeginAsync_ReturnsActiveWorkspace_WithWorkspaceContext()
    {
        var store = new FakeStore();
        var service = Service(store, out var workspaceStore);

        var workspace = await service.BeginAsync("acme", TestContext.Current.CancellationToken);

        workspace.ProjectId.ShouldBe("acme");
        workspace.Status.ShouldBe(WorkspaceStatus.Active);
        workspace.Context.ShouldStartWith("workspace:");
        workspace.Id.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task BeginAsync_RecordsActiveWorkspaceInStore()
    {
        var store = new FakeStore();
        var service = Service(store, out var workspaceStore);

        var workspace = await service.BeginAsync("acme", TestContext.Current.CancellationToken);

        var record = workspaceStore.Begun.ShouldHaveSingleItem();
        record.ProjectId.ShouldBe("acme");
        record.WorkspaceId.ShouldBe(workspace.Id);
        record.Status.ShouldBe(WorkspaceStatus.Active);
        record.StartedAt.ShouldNotBe(default);
    }

    [Fact]
    public async Task GetStatusAsync_ListsEntriesInTheWorkspaceContext()
    {
        var store = new FakeStore();
        store.EntriesByContext["workspace:ws-1"] =
            [new MemoryEntry("h1", "note.md", "workspace:ws-1", "draft", 1)];
        var service = Service(store, out _);

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
            new MemoryEntry("h2", "todo.md", "workspace:ws-1", "noise", 2)
        ];
        var service = Service(store, out var workspaceStore);

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
    public async Task ConsolidateAsync_ClosesTheWorkspaceRecord()
    {
        var store = new FakeStore();
        store.EntriesByContext["workspace:ws-1"] =
            [new MemoryEntry("h1", "note.md", "workspace:ws-1", "durable fact", 1)];
        var service = Service(store, out var workspaceStore);

        await service.ConsolidateAsync("acme", "ws-1", ["all"], TestContext.Current.CancellationToken);

        var closed = workspaceStore.Closed.ShouldHaveSingleItem();
        closed.ProjectId.ShouldBe("acme");
        closed.WorkspaceId.ShouldBe("ws-1");
        closed.Status.ShouldBe(WorkspaceStatus.Closed);
    }

    [Fact]
    public async Task ConsolidateAsync_WithAll_PromotesEveryEntry()
    {
        var store = new FakeStore();
        store.EntriesByContext["workspace:ws-1"] =
        [
            new MemoryEntry("h1", "a.md", "workspace:ws-1", "one", 1),
            new MemoryEntry("h2", "b.md", "workspace:ws-1", "two", 2)
        ];
        var service = Service(store, out _);

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
        var service = Service(store, out _);

        var deleted = await service.DiscardAsync("acme", "ws-1", TestContext.Current.CancellationToken);

        deleted.ShouldBe(1);
        store.DeletedContexts.ShouldContain("workspace:ws-1");
    }

    [Fact]
    public async Task DiscardAsync_ClosesTheWorkspaceRecord()
    {
        var store = new FakeStore();
        store.EntriesByContext["workspace:ws-1"] =
            [new MemoryEntry("h9", "scratch.md", "workspace:ws-1", "draft", 1)];
        var service = Service(store, out var workspaceStore);

        await service.DiscardAsync("acme", "ws-1", TestContext.Current.CancellationToken);

        var closed = workspaceStore.Closed.ShouldHaveSingleItem();
        closed.ProjectId.ShouldBe("acme");
        closed.WorkspaceId.ShouldBe("ws-1");
        closed.Status.ShouldBe(WorkspaceStatus.Closed);
    }

    private sealed class FakeStore : IMemoryStore
    {
        public Dictionary<string, IReadOnlyList<MemoryEntry>> EntriesByContext { get; } = [];

        public List<(string Path, string Content, string Context)> PromotedContent { get; } = [];

        public List<string> DeletedContexts { get; } = [];

        public string? LastListedContext { get; private set; }

        public Task<MemoryEntry>
            WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEntry("new-hash", request.Content,
                request.Context ?? ContextNaming.ProjectContext(request.ProjectId), request.Content, 1));

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
            CancellationToken cancellationToken = default)
        {
            PromotedContent.Add((path, content, context ?? ContextNaming.ProjectContext(projectId)));
            return Task.FromResult(new MemoryEntry("new-hash", path, context ?? ContextNaming.ProjectContext(projectId),
                content, 1));
        }

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<int> DeleteContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default)
        {
            DeletedContexts.Add(context);
            return Task.FromResult(EntriesByContext.TryGetValue(context, out var entries) ? entries.Count : 0);
        }

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) => throw new NotImplementedException();

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

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string projectId, string provider, string? model,
            string? baseUrl, string? apiKey, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default)
        {
            LastListedContext = context;
            return Task.FromResult(EntriesByContext.TryGetValue(context, out var entries) ? entries : []);
        }

        public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EntryMetadata?>(new EntryMetadata(0.5, null));

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetEntryTtlAsync(string projectId, string hash, double ttlDays,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeWorkspaceStore : IWorkspaceStore
    {
        public List<(string ProjectId, string WorkspaceId, WorkspaceStatus Status, DateTimeOffset StartedAt)> Begun { get; } = [];

        public List<(string ProjectId, string WorkspaceId, WorkspaceStatus Status, DateTimeOffset ClosedAt)> Closed { get; } = [];

        public Task BeginAsync(string projectId, string workspaceId, DateTimeOffset startedAt,
            CancellationToken cancellationToken = default)
        {
            Begun.Add((projectId, workspaceId, WorkspaceStatus.Active, startedAt));
            return Task.CompletedTask;
        }

        public Task CloseAsync(string projectId, string workspaceId, WorkspaceStatus status, DateTimeOffset closedAt,
            CancellationToken cancellationToken = default)
        {
            Closed.Add((projectId, workspaceId, status, closedAt));
            return Task.CompletedTask;
        }
    }
}
