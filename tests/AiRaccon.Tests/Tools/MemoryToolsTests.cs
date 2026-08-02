using AiRaccon.Core.Common;
using AiRaccon.Core.Degradation;
using AiRaccon.Core.Memory;
using AiRaccon.Core.Workspace;
using AiRaccon.Infrastructure.Degradation;
using AiRaccon.Infrastructure.Options;
using AiRaccon.Infrastructure.Sqlite;
using AiRaccon.Infrastructure.Sync;
using AiRaccon.Infrastructure.Workspace;
using AiRaccon.Tools;
using ModelContextProtocol;
using Shouldly;
using Xunit;

namespace AiRaccon.Tests.Tools;

public class MemoryToolsTests
{
    private readonly FakeStore _store = new();
    private readonly FakeSyncService _sync = new();
    private readonly FakeMetaStore _meta = new();
    private readonly WorkspaceService _workspaces;
    private readonly SweepService _sweeper;
    private readonly MemoryTools _tools;

    public MemoryToolsTests()
    {
        _workspaces = new WorkspaceService(_store);
        _sweeper = new SweepService(_store, _meta);
        _tools = new MemoryTools(_store, _sync, _workspaces, _sweeper);
    }

    [Fact]
    public async Task Write_WithoutProjectId_ThrowsMcpException()
    {
        var ex = await Should.ThrowAsync<McpException>(() => _tools.Write("", "content", cancellationToken: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("project_id");
    }

    [Fact]
    public async Task Write_DelegatesToStore_AndMapsResult()
    {
        _store.Entry = new MemoryEntry("h1", "p.md", "project:acme", "content", 5);

        var result = await _tools.Write("acme", "content", agentId: "agent-a", cancellationToken: TestContext.Current.CancellationToken);

        result.Hash.ShouldBe("h1");
        result.Context.ShouldBe("project:acme");
        _store.LastRequest!.ProjectId.ShouldBe("acme");
        _store.LastRequest.AgentId.ShouldBe("agent-a");
    }

    [Fact]
    public async Task Search_WithInvalidScope_ThrowsMcpException()
    {
        var ex = await Should.ThrowAsync<McpException>(() => _tools.Search("acme", "q", scope: "bogus", cancellationToken: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("scope");
    }

    [Fact]
    public async Task Search_WithAllScope_DelegatesWithSearchScopeAll()
    {
        await _tools.Search("acme", "query", scope: "all", cancellationToken: TestContext.Current.CancellationToken);

        _store.LastQuery!.Scope.ShouldBe(SearchScope.All);
        _store.LastQuery.ProjectId.ShouldBe("acme");
    }

    [Fact]
    public async Task Share_DelegatesToStore_AndReportsSharedContext()
    {
        _store.SharedEntry = new MemoryEntry("h1", "p.md", ContextNaming.SharedContext, "v", 1);

        var result = await _tools.Share("acme", "h1", cancellationToken: TestContext.Current.CancellationToken);

        result.Shared.ShouldBeTrue();
        result.Context.ShouldBe(ContextNaming.SharedContext);
        _store.Shared.ShouldBe(("acme", "h1"));
    }

    [Fact]
    public async Task Sync_WithoutCredentials_ThrowsMcpExceptionWithSyncNotConfigured()
    {
        _sync.Exception = new SyncNotConfiguredException();

        var ex = await Should.ThrowAsync<McpException>(() => _tools.Sync("acme", cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("sync-not-configured");
    }

    [Fact]
    public async Task Sync_DelegatesAndMapsResult()
    {
        _sync.Result = new SyncResult(3, 2, 5);

        var result = await _tools.Sync("acme", cancellationToken: TestContext.Current.CancellationToken);

        result.Sent.ShouldBe(3);
        result.Received.ShouldBe(2);
        result.Reindexed.ShouldBe(5);
    }

    [Fact]
    public async Task WorkspaceBegin_ReturnsWorkspaceIdAndContext()
    {
        var result = await _tools.WorkspaceBegin("acme", agentId: "agent-a", cancellationToken: TestContext.Current.CancellationToken);

        result.WorkspaceId.ShouldNotBeNullOrWhiteSpace();
        result.Context.ShouldStartWith("workspace:");
    }

    [Fact]
    public async Task WorkspaceConsolidate_WithAll_PromotesEverything()
    {
        _store.EntriesByContext["workspace:ws-1"] =
        [
            new MemoryEntry("h1", "a.md", "workspace:ws-1", "one", 1),
            new MemoryEntry("h2", "b.md", "workspace:ws-1", "two", 2),
        ];

        var result = await _tools.WorkspaceConsolidate("acme", "ws-1", ["all"], cancellationToken: TestContext.Current.CancellationToken);

        result.Promoted.ShouldBe(2);
    }

    [Fact]
    public async Task Stats_ReturnsEntriesPendingAndContexts()
    {
        _store.Stats = new MemoryStats(5, 2, ["shared", "project:acme"]);

        var result = await _tools.Stats("acme", cancellationToken: TestContext.Current.CancellationToken);

        result.Entries.ShouldBe(5);
        result.Pending.ShouldBe(2);
        result.Contexts.ShouldContain("shared");
    }

    [Fact]
    public async Task Sweep_DryRunByDefault_ReportsCandidatesWithoutDeleting()
    {
        _store.EntriesByContext["project:acme"] =
            [new MemoryEntry("old", "n.md", "project:acme", "v", DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 40 * 86_400)];
        _meta.Rating = 0.1;
        _store.Stats = new MemoryStats(1, 0, ["project:acme"]);

        var result = await _tools.Sweep("acme", cancellationToken: TestContext.Current.CancellationToken);

        result.Candidates.Count.ShouldBe(1);
        result.Deleted.Count.ShouldBe(0);
    }

    private sealed class FakeStore : IMemoryStore
    {
        public MemoryEntry Entry { get; set; } = new("h", "p.md", "project:acme", "v", 0);

        public MemoryEntry? SharedEntry { get; set; }

        public MemoryStats Stats { get; set; } = new(0, 0, []);

        public MemoryWriteRequest? LastRequest { get; private set; }

        public SearchQuery? LastQuery { get; private set; }

        public (string ProjectId, string Hash)? Shared { get; private set; }

        public Dictionary<string, IReadOnlyList<MemoryEntry>> EntriesByContext { get; } = [];

        public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(Entry);
        }

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<MemorySearchResult>>([]);
        }

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<int> DeleteContextAsync(string projectId, string context, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default)
            => Task.FromResult(Stats);

        public Task<MemoryEntry> ShareAsync(string projectId, string hash, CancellationToken cancellationToken = default)
        {
            Shared = (projectId, hash);
            return Task.FromResult(SharedEntry ?? new MemoryEntry(hash, "p.md", ContextNaming.SharedContext, "v", 1));
        }

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default)
            => Task.FromResult("{\"root\":\"\"}");

        public Task<int> IngestFileAsync(string projectId, string path, string? context, CancellationToken cancellationToken = default)
            => Task.FromResult(1);

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context, CancellationToken cancellationToken = default)
            => Task.FromResult(1);

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string projectId, string provider, string model, string? apiKey, CancellationToken cancellationToken = default)
            => Task.FromResult(new EmbeddingConfig(provider, model, provider == "local" ? "local" : "remote"));

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit, CancellationToken cancellationToken = default)
            => Task.FromResult(new EmbedPendingResult(0, 0));

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context, CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryEntry("new-hash", path, context ?? "project:acme", content, 1));

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context, CancellationToken cancellationToken = default)
            => Task.FromResult(EntriesByContext.TryGetValue(context, out var e) ? e : []);
    }

    private sealed class FakeSyncService : SyncService
    {
        public SyncResult Result { get; set; } = new(0, 0, 0);

        public SyncNotConfiguredException? Exception { get; set; }

        public FakeSyncService() : base(new SyncOptions(), new FakeCloudSyncFactory())
        {
        }

        public override Task<SyncResult> MemorySyncAsync(string projectId, CancellationToken cancellationToken = default)
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class FakeCloudSyncFactory : ICloudSyncConnectionFactory
    {
        public Task<ICloudSyncConnection> OpenAsync(CancellationToken cancellationToken)
            => throw new NotImplementedException();
    }

    private sealed class FakeMetaStore : MetaStore
    {
        public double Rating { get; set; } = 0.9;

        public FakeMetaStore() : base(null!)
        {
        }

        public override Task<MetaEntry?> GetEntryAsync(string projectId, string hash, CancellationToken cancellationToken = default)
            => Task.FromResult<MetaEntry?>(new MetaEntry(hash, projectId, null, null, 0, 0, null, Rating, null));

        public override Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }
}
