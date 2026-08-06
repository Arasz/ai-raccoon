using System.Text.Json;
using System.Text.Json.Nodes;
using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Workspace;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Workspace;
using AiRaccoon.Observability;
using AiRaccoon.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class MemoryToolsTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeStore _store = new();
    private readonly FakeSyncService _sync = new();
    private readonly MemoryTools _tools;

    public MemoryToolsTests()
    {
        var workspaces = new WorkspaceService(_store, new FakeWorkspaceStore(), new FakeTimeProvider(FixedNow));
        var sweeper = new SweepService(_store, new FakeTimeProvider(FixedNow));
        _tools = new MemoryTools(_store, _sync, workspaces, sweeper, new MemoryAccessGuard(_store),
            new SyncCloudStoreFactory(_store, NullLoggerFactory.Instance),
            new ForgettingPolicyService(_store, new MemoryAccessGuard(_store)),
            new ToolCallMetrics(), new SharedExtractionService());
    }

    private void SeedSyncSettings(string? objectKey = null)
    {
        _store.Settings[SyncSettingsKeys.Endpoint] = "http://test";
        _store.Settings[SyncSettingsKeys.Bucket] = "test-bucket";
        _store.Settings[SyncSettingsKeys.AccessKey] = "test-key";
        _store.Settings[SyncSettingsKeys.SecretKey] = "test-secret";
        if (objectKey is not null)
        {
            _store.Settings[SyncSettingsKeys.ObjectKey] = objectKey;
        }
    }

    [Fact]
    public async Task Write_WithoutProjectId_ThrowsMcpException()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Write("", "content", cancellationToken: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("project_id");
    }

    [Fact]
    public async Task ShareExtract_Propose_ReturnsCandidates_WithoutSharing()
    {
        _store.Candidates.Add(new ExtractionCandidateRow("h1", "h1.md",
            "organic fact about job-search-ai-assistant", null, 0.5, 0,
            DateTimeOffset.UtcNow.AddDays(-5), null));

        var result = await _tools.ShareExtract(["acme"], cancellationToken: TestContext.Current.CancellationToken);

        result.Candidates.ShouldHaveSingleItem();
        result.Candidates[0].Hash.ShouldBe("h1");
        result.Candidates[0].Reasons.ShouldContain("organic-write");
        result.PromotedHashes.ShouldBeEmpty();
        _store.Shared.ShouldBeNull();
    }

    [Fact]
    public async Task ShareExtract_Promote_SharesTheTopCandidates()
    {
        _store.Candidates.Add(new ExtractionCandidateRow("h1", "h1.md",
            "organic fact about job-search-ai-assistant", null, 0.5, 0,
            DateTimeOffset.UtcNow.AddDays(-5), null));

        var result = await _tools.ShareExtract(["acme"], mode: "promote",
            cancellationToken: TestContext.Current.CancellationToken);

        result.PromotedHashes.ShouldBe(["h1"]);
        _store.Shared.ShouldBe(("acme", "h1"));
    }

    [Fact]
    public async Task ShareExtract_DefaultLimit_MatchesSharedConstant()
    {
        // 25 eligible rows: the default limit must bind at the shared constant, not a literal.
        for (var i = 0; i < 25; i++)
        {
            _store.Candidates.Add(new ExtractionCandidateRow($"h{i:00}", $"h{i:00}.md",
                $"organic fact number {i}", null, 0.5, 0,
                DateTimeOffset.UtcNow.AddDays(-5), null));
        }

        var result = await _tools.ShareExtract(["acme"],
            cancellationToken: TestContext.Current.CancellationToken);

        result.Candidates.Count.ShouldBe(SharedExtractionService.DefaultCandidateLimit);
    }

    [Fact]
    public async Task ShareExtract_AlreadySharedValue_IsExcluded()
    {
        _store.Candidates.Add(new ExtractionCandidateRow("h1", "h1.md", "same fact", null, 0.5, 0,
            DateTimeOffset.UtcNow.AddDays(-5), null));
        _store.Index = new SharedIndex(["samefact"], []);

        var result = await _tools.ShareExtract(["acme"], cancellationToken: TestContext.Current.CancellationToken);

        result.Candidates.ShouldBeEmpty();
        _store.Shared.ShouldBeNull();
    }

    [Fact]
    public async Task ShareExtract_InvalidProjectIds_ThrowsTyped()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.ShareExtract([], cancellationToken: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("invalid-params");
    }

    [Fact]
    public async Task ShareExtract_InvalidMode_ThrowsTyped()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.ShareExtract(["acme"], mode: "auto", cancellationToken: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("invalid-params");
    }

    [Fact]
    public async Task ShareExtract_InvalidLimit_ThrowsTyped()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.ShareExtract(["acme"], limit: 0, cancellationToken: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("invalid-params");
    }

    [Fact]
    public async Task ShareExtract_AutoPromote_WithoutConfirm_IsGated()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.ShareExtract(["acme"], autoPromote: true, cancellationToken: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("confirm-required");
        ex.Message.ShouldContain("ALL projects");
        _store.Shared.ShouldBeNull();
    }

    [Fact]
    public async Task ShareExtract_AutoPromote_WithConfirm_PromotesInCall()
    {
        _store.Candidates.Add(new ExtractionCandidateRow("h1", "h1.md",
            "organic fact about job-search-ai-assistant", null, 0.5, 0,
            DateTimeOffset.UtcNow.AddDays(-5), null));

        var result = await _tools.ShareExtract(["acme"], autoPromote: true, confirm: true,
            cancellationToken: TestContext.Current.CancellationToken);

        result.PromotedHashes.ShouldBe(["h1"]);
        _store.Shared.ShouldBe(("acme", "h1"));
    }

    [Fact]
    public async Task ShareExtract_AutoPromote_IsDisabledByDefault()
    {
        _store.Candidates.Add(new ExtractionCandidateRow("h1", "h1.md",
            "organic fact about job-search-ai-assistant", null, 0.5, 0,
            DateTimeOffset.UtcNow.AddDays(-5), null));

        var result = await _tools.ShareExtract(["acme"], cancellationToken: TestContext.Current.CancellationToken);

        result.PromotedHashes.ShouldBeEmpty();
        _store.Shared.ShouldBeNull();
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
    public async Task Write_ForwardsSourceFile_ToStore()
    {
        await _tools.Write("acme", "content", sourceFile: "docs/adr/0001-test.md", section: "decision",
            cancellationToken: TestContext.Current.CancellationToken);

        _store.LastRequest!.SourceFile.ShouldBe("docs/adr/0001-test.md");
        _store.LastRequest.Section.ShouldBe("decision");
    }

    [Fact]
    public async Task Search_ForwardsContextLabel_ToStore()
    {
        await _tools.Search("acme", "query", contextLabel: "docs:adr",
            cancellationToken: TestContext.Current.CancellationToken);

        _store.LastQuery!.ContextLabel.ShouldBe("docs:adr");
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
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Sync("acme", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("sync-not-configured");
    }

    [Fact]
    public async Task Sync_DelegatesAndMapsResult()
    {
        SeedSyncSettings();
        _sync.Result = new SyncResult(3, 2, 5);

        var result = await _tools.Sync("acme", TestContext.Current.CancellationToken);

        result.Sent.ShouldBe(3);
        result.Received.ShouldBe(2);
        result.Reindexed.ShouldBe(5);
        _sync.LastObjectKey.ShouldBe("memory-acme.db");
    }

    [Fact]
    public async Task Sync_UsesConfiguredObjectKeyFromSettings()
    {
        SeedSyncSettings(objectKey: "bank.db");

        await _tools.Sync("acme", TestContext.Current.CancellationToken);

        _sync.LastObjectKey.ShouldBe("bank.db");
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
    public async Task List_ReturnsFilesAsJsonObject()
    {
        var result = await _tools.List("acme", CancellationToken.None);

        result.Files.ShouldBeOfType<JsonObject>();
        JsonSerializer.Serialize(result).ShouldContain("\"files\":{\"root\":\"\"}");
    }

    [Fact]
    public async Task List_PreservesNestedFileTree()
    {
        _store.FilesJson = "{\"a.md\":{},\"dir\":{\"b.md\":{}}}";

        var result = await _tools.List("acme", CancellationToken.None);

        JsonSerializer.Serialize(result).ShouldContain("\"files\":{\"a.md\":{},\"dir\":{\"b.md\":{}}}");
    }

    [Fact]
    public async Task WorkspaceDiscard_ReportsDiscardedKey()
    {
        _store.Settings[AccessModePolicy.ProjectSettingKey("acme")] = "full";

        var result = await _tools.WorkspaceDiscard("acme", "ws-1", CancellationToken.None);

        var json = JsonSerializer.Serialize(result);
        json.ShouldContain("\"discarded\":");
        json.ShouldNotContain("\"deleted\":");
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

        public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

        public string FilesJson { get; set; } = "{\"root\":\"\"}";

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

        public Task<int> DeleteSourcePathAsync(string projectId, string path,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(Stats);

        public Task<MemoryEntry> ShareAsync(string projectId, string hash,
            CancellationToken cancellationToken = default)
        {
            Shared = (projectId, hash);
            return Task.FromResult(SharedEntry ?? new MemoryEntry(hash, "p.md", ContextNaming.SharedContext, "v", 1));
        }

        public List<ExtractionCandidateRow> Candidates { get; } = [];

        public SharedIndex Index { get; set; } = new([], []);

        public Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
            bool includeTtlRows, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractionCandidateRow>>(Candidates);

        public Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Index);

        public List<string> ProjectIds { get; } = ["acme"];

        public Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(ProjectIds);

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(FilesJson);

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
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEntry("new-hash", path, context ?? "project:acme", content, 1));

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(EntriesByContext.TryGetValue(context, out var e) ? e : []);

        public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EntryMetadata?>(new EntryMetadata(Rating, 30));

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Settings.TryGetValue(key, out var value) ? value : null);

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Settings[key] = value;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                Settings.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));

        public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            Settings.Remove(key);
            return Task.CompletedTask;
        }


        public Task SetEntryTtlAsync(string projectId, string hash, double ttlDays,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeSyncService() : SyncService(new FakeCloudStore(), _ => Task.FromResult<SqliteConnection>(null!),
        (_, _) => Task.FromResult<SqliteConnection>(null!), (_, _) => Task.FromResult<SqliteConnection>(null!), TimeProvider.System, null!)
    {
        public SyncResult Result { get; set; } = new(0, 0, 0);

        public SyncNotConfiguredException? Exception { get; set; }

        public string? LastObjectKey { get; private set; }

        public override Task<SyncResult> MemorySyncAsync(string projectId, string objectKey,
            CancellationToken cancellationToken = default)
        {
            LastObjectKey = objectKey;
            return Exception is not null ? throw Exception : Task.FromResult(Result);
        }
    }

    private sealed class FakeCloudStore : ICloudStore
    {
        public Task<CloudObject?> PullAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult<CloudObject?>(null);

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
