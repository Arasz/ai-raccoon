using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Workspace;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Workspace;
using AiRaccoon.Tools;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Tools;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class MemoryToolsTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeStore _store = new();
    private readonly SweepService _sweeper;
    private readonly FakeSyncService _sync = new();
    private readonly MemoryTools _tools;
    private readonly WorkspaceService _workspaces;

    public MemoryToolsTests()
    {
        _workspaces = new WorkspaceService(_store, new FakeWorkspaceStore(), new FakeTimeProvider(FixedNow));
        _sweeper = new SweepService(_store, new FakeTimeProvider(FixedNow));
        _tools = new MemoryTools(_store, _sync, _workspaces, _sweeper, new MemoryAccessGuard(_store),
            new SyncOptions
            {
                Endpoint = "http://test",
                Bucket = "test-bucket",
                AccessKey = "test-key",
                SecretKey = "test-secret"
            },
            new ForgettingPolicyService(_store, new MemoryAccessGuard(_store)));
    }

    [Fact]
    public async Task Write_WithoutProjectId_ThrowsMcpException()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Write("", "content", cancellationToken: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("project_id");
    }

    [Fact]
    public async Task Write_DelegatesToStore_AndMapsResult()
    {
        _store.Entry = new MemoryEntry("h1", "p.md", "project:acme", "content", 5);

        var result = await _tools.Write("acme", "content", agentId: "agent-a",
            cancellationToken: TestContext.Current.CancellationToken);

        result.Hash.ShouldBe("h1");
        result.Context.ShouldBe("project:acme");
        _store.LastRequest!.ProjectId.ShouldBe("acme");
        _store.LastRequest.AgentId.ShouldBe("agent-a");
    }

    [Fact]
    public async Task Search_WithInvalidScope_ThrowsMcpException()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Search("acme", "q", "bogus", cancellationToken: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("scope");
    }

    [Fact]
    public async Task Search_WithAllScope_DelegatesWithSearchScopeAll()
    {
        await _tools.Search("acme", "query", cancellationToken: TestContext.Current.CancellationToken);

        _store.LastQuery!.Scope.ShouldBe(SearchScope.All);
        _store.LastQuery.ProjectId.ShouldBe("acme");
    }

    [Fact]
    public async Task Search_WithFusionParameters_DelegatesThemOnTheQuery()
    {
        await _tools.Search("acme", "query", rrfK: 30, ftsWeight: 2, vectorWeight: 1,
            cancellationToken: TestContext.Current.CancellationToken);

        _store.LastQuery!.RrfK.ShouldBe(30);
        _store.LastQuery.FtsWeight.ShouldBe(2);
        _store.LastQuery.VectorWeight.ShouldBe(1);
    }

    [Fact]
    public async Task Search_WithoutFusionParameters_AppliesDefaults()
    {
        await _tools.Search("acme", "query", cancellationToken: TestContext.Current.CancellationToken);

        _store.LastQuery!.RrfK.ShouldBe(SearchQuery.DefaultRrfK);
        _store.LastQuery.FtsWeight.ShouldBe(1);
        _store.LastQuery.VectorWeight.ShouldBe(1);
    }

    [Fact]
    public async Task Share_DelegatesToStore_AndReportsSharedContext()
    {
        _store.SharedEntry = new MemoryEntry("h1", "p.md", ContextNaming.SharedContext, "v", 1);

        var result = await _tools.Share("acme", "h1", TestContext.Current.CancellationToken);

        result.Shared.ShouldBeTrue();
        result.Context.ShouldBe(ContextNaming.SharedContext);
        _store.Shared.ShouldBe(("acme", "h1"));
    }

    [Fact]
    public async Task Sync_WithoutCredentials_ThrowsMcpExceptionWithSyncNotConfigured()
    {
        _sync.Exception = new SyncNotConfiguredException();

        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Sync("acme", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("sync-not-configured");
    }

    [Fact]
    public async Task Sync_DelegatesAndMapsResult()
    {
        _sync.Result = new SyncResult(3, 2, 5);

        var result = await _tools.Sync("acme", TestContext.Current.CancellationToken);

        result.Sent.ShouldBe(3);
        result.Received.ShouldBe(2);
        result.Reindexed.ShouldBe(5);
    }

    [Fact]
    public async Task WorkspaceBegin_ReturnsWorkspaceIdAndContext()
    {
        var result = await _tools.WorkspaceBegin("acme", "agent-a",
            cancellationToken: TestContext.Current.CancellationToken);

        result.WorkspaceId.ShouldNotBeNullOrWhiteSpace();
        result.Context.ShouldStartWith("workspace:");
    }

    [Fact]
    public async Task WorkspaceConsolidate_WithAll_PromotesEverything()
    {
        _store.Settings[AccessModePolicy.ProjectSettingKey("acme")] = "full";
        _store.EntriesByContext["workspace:ws-1"] =
        [
            new MemoryEntry("h1", "a.md", "workspace:ws-1", "one", 1),
            new MemoryEntry("h2", "b.md", "workspace:ws-1", "two", 2)
        ];

        var result = await _tools.WorkspaceConsolidate("acme", "ws-1", ["all"],
            TestContext.Current.CancellationToken);

        result.Promoted.ShouldBe(2);
    }

    [Fact]
    public async Task Stats_ReturnsEntriesPendingAndContexts()
    {
        _store.Stats = new MemoryStats(5, 2, ["shared", "project:acme"]);

        var result = await _tools.Stats("acme", TestContext.Current.CancellationToken);

        result.Entries.ShouldBe(5);
        result.Pending.ShouldBe(2);
        result.Contexts.ShouldContain("shared");
    }

    [Fact]
    public async Task Sweep_DryRunByDefault_ReportsCandidatesWithoutDeleting()
    {
        _store.EntriesByContext["project:acme"] =
        [
            new MemoryEntry("old", "n.md", "project:acme", "v", FixedNow.ToUnixTimeSeconds() - 40 * 86_400)
        ];
        _store.Rating = 0.1;
        _store.Stats = new MemoryStats(1, 0, ["project:acme"]);

        var result = await _tools.Sweep("acme", cancellationToken: TestContext.Current.CancellationToken);

        result.Candidates.Count.ShouldBe(1);
        result.Deleted.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Configure_WithUnknownProvider_ThrowsMcpException()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Configure("acme", "vectorspace", cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("provider must be 'local' or 'openai'");
        _store.Configured.ShouldBeNull();
    }

    [Fact]
    public async Task Configure_OpenAi_WithoutModel_ThrowsMcpException()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Configure("acme", "openai", baseUrl: "http://localhost:11434/v1", apiKey: "test-key-123",
                cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("model is required");
    }

    [Fact]
    public async Task Configure_OpenAi_WithoutApiKey_ThrowsMcpException()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Configure("acme", "openai", model: "nomic-embed-text",
                cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("embedding-api-key-missing");
    }

    [Fact]
    public async Task Configure_OpenAi_WithApiKey_DelegatesProviderModelBaseUrl()
    {
        await _tools.Configure("acme", "openai", baseUrl: "http://localhost:11434/v1", model: "nomic-embed-text",
            apiKey: "test-key-123", cancellationToken: TestContext.Current.CancellationToken);

        _store.Configured.ShouldBe(("openai", "nomic-embed-text", "http://localhost:11434/v1", "test-key-123"));
    }

    [Fact]
    public async Task Configure_OpenAi_WithoutArgKey_FallsBackToEnvKey()
    {
        var previous = Environment.GetEnvironmentVariable("AIRACCOON_OPENAI_API_KEY");
        Environment.SetEnvironmentVariable("AIRACCOON_OPENAI_API_KEY", "env-key-456");
        try
        {
            await _tools.Configure("acme", "openai", model: "nomic-embed-text",
                cancellationToken: TestContext.Current.CancellationToken);

            _store.Configured.ShouldBe(("openai", "nomic-embed-text", null, "env-key-456"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("AIRACCOON_OPENAI_API_KEY", previous);
        }
    }

    [Fact]
    public async Task Configure_OpenAi_WithoutBaseUrl_DefaultsToTheOpenAiEndpoint()
    {
        await _tools.Configure("acme", "openai", model: "nomic-embed-text", apiKey: "test-key-123",
            cancellationToken: TestContext.Current.CancellationToken);

        _store.Configured.ShouldBe(("openai", "nomic-embed-text", null, "test-key-123"));
    }

    [Fact]
    public async Task Configure_Local_WithoutModel_DelegatesWithBundledDefault()
    {
        await _tools.Configure("acme", "local", cancellationToken: TestContext.Current.CancellationToken);

        _store.Configured.ShouldBe(("local", null, null, null));
    }

    [Fact]
    public async Task Configure_RequiresWriteAccess()
    {
        _store.Settings[AccessModePolicy.ProjectSettingKey("acme")] = "ro";

        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Configure("acme", "local", cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("access-denied");
        _store.Configured.ShouldBeNull();
    }

    private sealed class FakeStore : IMemoryStore
    {
        public MemoryEntry Entry { get; set; } = new("h", "p.md", "project:acme", "v", 0);

        public MemoryEntry? SharedEntry { get; set; }

        public MemoryStats Stats { get; set; } = new(0, 0, []);

        public double Rating { get; set; } = 0.9;

        public MemoryWriteRequest? LastRequest { get; private set; }

        public SearchQuery? LastQuery { get; private set; }

        public (string ProjectId, string Hash)? Shared { get; private set; }

        public Dictionary<string, IReadOnlyList<MemoryEntry>> EntriesByContext { get; } = [];

        public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(Entry);
        }

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return Task.FromResult<IReadOnlyList<MemorySearchResult>>([]);
        }

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<int> DeleteContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(Stats);

        public Task<MemoryEntry> ShareAsync(string projectId, string hash,
            CancellationToken cancellationToken = default)
        {
            Shared = (projectId, hash);
            return Task.FromResult(SharedEntry ?? new MemoryEntry(hash, "p.md", ContextNaming.SharedContext, "v", 1));
        }

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult("{\"root\":\"\"}");

        public Task<int> IngestFileAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(1);

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(1);

        public (string Provider, string? Model, string? BaseUrl, string? ApiKey)? Configured { get; private set; }

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string projectId, string provider, string? model,
            string? baseUrl, string? apiKey, CancellationToken cancellationToken = default)
        {
            Configured = (provider, model, baseUrl, apiKey);
            return Task.FromResult(new EmbeddingConfig(provider, model ?? "bundled", provider == "local" ? "local" : "remote"));
        }

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbedPendingResult(0, 0));

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEntry("new-hash", path, context ?? "project:acme", content, 1));

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(EntriesByContext.TryGetValue(context, out var e) ? e : []);

        public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EntryMetadata?>(new EntryMetadata(Rating, null));
        public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings.TryGetValue(key, out var value) ? value : null);

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Settings[key] = value;
            return Task.CompletedTask;
        }

        public Task SetEntryTtlAsync(string projectId, string hash, double ttlDays,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeSyncService : SyncService
    {
        public FakeSyncService() : base(new FakeCloudStore(), _ => Task.FromResult((Microsoft.Data.Sqlite.SqliteConnection)null!),
            (_, _) => Task.FromResult((Microsoft.Data.Sqlite.SqliteConnection)null!), null!)
        {
        }

        public SyncResult Result { get; set; } = new(0, 0, 0);

        public SyncNotConfiguredException? Exception { get; set; }

        public override Task<SyncResult> MemorySyncAsync(string projectId, string objectKey,
            CancellationToken cancellationToken = default)
        {
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(Result);
        }
    }

    private sealed class FakeCloudStore : ICloudStore
    {
        public Task<CloudObject?> PullAsync(string objectKey, CancellationToken cancellationToken = default) =>
            Task.FromResult<CloudObject?>(null);

        public Task<string> PushAsync(string objectKey, byte[] data, string? etag,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("fake-etag");
    }

    private sealed class FakeWorkspaceStore : IWorkspaceStore
    {
        public Task BeginAsync(string projectId, string workspaceId, DateTimeOffset startedAt,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CloseAsync(string projectId, string workspaceId, WorkspaceStatus status, DateTimeOffset closedAt,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
