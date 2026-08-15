using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Workspace;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     Access-mode gating at the MCP tool boundary (docs/work/features-native-memory/native-memory.feature):
///     reads are allowed in every mode, writes need rw+, removal needs full.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MemoryToolsAccessModeTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly PromotionTools _promotion;
    private readonly ShareTools _share;

    private readonly FakeStore _store = new();
    private readonly SweepTools _sweep;
    private readonly MemoryTools _tools;
    private readonly WorkspaceTools _workspace;

    public MemoryToolsAccessModeTests()
    {
        var access = new MemoryAccessGuard(_store);
        var workspaces = new WorkspaceService(_store, new FakeWorkspaceStore(), new FakeTimeProvider(FixedNow));
        var sweeper = new SweepService(_store, new FakeTimeProvider(FixedNow));
        var queue = new FakePromotionQueue();
        var gate = new ToolGate(access, queue);
        _tools = new MemoryTools(_store, gate, new NoOpSearchQualityService(), NullLogger<MemoryTools>.Instance);
        _share = new ShareTools(_store, gate,
            new SharedExtractionRunner(_store, new SharedExtractionService(), queue,
                new FakeTimeProvider(FixedNow)), queue);
        _workspace = new WorkspaceTools(workspaces, gate);
        _sweep = new SweepTools(sweeper, new ForgettingPolicyService(_store, access), gate);
        _promotion = new PromotionTools(queue, gate);
    }

    private void SetMode(string? global = null, string? perProject = null)
    {
        if (global is not null)
        {
            _store.Settings[AccessModePolicy.GlobalSettingKey] = global;
        }

        if (perProject is not null)
        {
            _store.Settings[AccessModePolicy.ProjectSettingKey("acme-web")] = perProject;
        }
    }

    [Fact]
    public async Task DefaultMode_WriteSucceeds_AndDeleteIsDenied()
    {
        _store.Entry = new MemoryEntry("h1", "p.md", "project:acme-web", "content", 1);

        var written = await _tools.Write("acme-web", "content", cancellationToken: TestContext.Current.CancellationToken);

        written.Data!.Hash.ShouldBe("h1");

        var ex = await Should.ThrowAsync<AccessDeniedException>(() =>
            _tools.Delete("acme-web", "h1", TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("memory_delete requires mode full (current rw)");
    }

    [Fact]
    public async Task RoMode_WriteIsDenied_AndSearchStillWorks()
    {
        SetMode(perProject: "ro");
        _store.SearchResults = [new MemorySearchResult("h1", 0.9, "p.md", "content")];

        var writeEx = await Should.ThrowAsync<AccessDeniedException>(() =>
            _tools.Write("acme-web", "content", cancellationToken: TestContext.Current.CancellationToken));
        writeEx.Message.ShouldContain("memory_write requires mode rw (current ro)");

        var results = await _tools.Search("acme-web", "query", cancellationToken: TestContext.Current.CancellationToken);
        results.Data!.Results.Count.ShouldBe(1);
        results.Data!.Results[0].Snippet.ShouldBe("content");
    }

    [Fact]
    public async Task RoMode_ShareExtractProposeAllowed_PromoteDenied()
    {
        SetMode(perProject: "ro");

        var result = await _share.ShareExtract(["acme-web"], cancellationToken: TestContext.Current.CancellationToken);

        result.Data!.Candidates.ShouldBeEmpty();

        await Should.ThrowAsync<AccessDeniedException>(() =>
            _share.ShareExtract(["acme-web"], "promote", cancellationToken: TestContext.Current.CancellationToken));

        await Should.ThrowAsync<AccessDeniedException>(() =>
            _share.ShareExtract(["acme-web"], autoPromote: true, confirm: true,
                cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FullMode_DeleteRemovesTheEntry()
    {
        SetMode(perProject: "full");

        var result = await _tools.Delete("acme-web", "h1", TestContext.Current.CancellationToken);

        result.Data!.Deleted.ShouldBe(1);
        _store.DeletedHashes.ShouldContain("h1");
    }

    [Fact]
    public async Task FullMode_WorkspaceDiscardRemovesTheWorkspace()
    {
        SetMode(perProject: "full");
        _store.EntriesByContext["workspace:ws-1"] = [];

        await _workspace.WorkspaceDiscard("acme-web", "ws-1", TestContext.Current.CancellationToken);

        _store.DeletedContexts.ShouldContain("workspace:ws-1");
    }

    /// <summary>
    ///     The workspace lifecycle is scoped to a sandbox the caller created and never committed —
    ///     consolidate promotes into the caller's own project, discard removes only
    ///     `workspace:&lt;id&gt;` filtered by project. Neither can reach committed memory, so gating them
    ///     at `full` made workspaces unusable at the default mode for no containment benefit.
    /// </summary>
    [Fact]
    public async Task RwMode_WorkspaceConsolidate_IsAllowed()
    {
        SetMode(global: "rw");
        _store.EntriesByContext["workspace:ws-1"] = [];

        await Should.NotThrowAsync(() => _workspace.WorkspaceConsolidate(
            "acme-web", "ws-1", ["all"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RwMode_WorkspaceDiscard_IsAllowed()
    {
        SetMode(global: "rw");
        _store.EntriesByContext["workspace:ws-1"] = [];

        await _workspace.WorkspaceDiscard("acme-web", "ws-1", TestContext.Current.CancellationToken);

        _store.DeletedContexts.ShouldContain("workspace:ws-1");
    }

    [Fact]
    public async Task RoMode_WorkspaceDiscard_IsStillRefused()
    {
        // Relaxing to Write must not relax all the way to Read.
        SetMode(global: "ro");
        _store.EntriesByContext["workspace:ws-1"] = [];

        await Should.ThrowAsync<AccessDeniedException>(() => _workspace.WorkspaceDiscard(
            "acme-web", "ws-1", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GlobalMode_AppliesToProjectWithoutPerProjectOverride()
    {
        SetMode(global: "rw");

        var result = await _tools.Write("other-app", "content", cancellationToken: TestContext.Current.CancellationToken);

        result.Data!.ShouldNotBeNull();
    }

    [Fact]
    public async Task GlobalModeRo_DeniesWritesForProjectWithoutPerProjectOverride()
    {
        SetMode(global: "ro");

        var ex = await Should.ThrowAsync<AccessDeniedException>(() =>
            _tools.Write("other-app", "content", cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("memory_write requires mode rw (current ro)");
    }

    [Fact]
    public async Task RoMode_SweepDryRun_IsAllowed()
    {
        SetMode(perProject: "ro");
        _store.EntriesByContext["project:acme-web"] =
        [
            new MemoryEntry("old", "n.md", "project:acme-web", "v", FixedNow.ToUnixTimeSeconds() - 40 * 86_400)
        ];
        _store.Rating = 0.1;
        _store.Stats = new MemoryStats(1, 0, ["project:acme-web"]);

        var result = await _sweep.Sweep("acme-web", cancellationToken: TestContext.Current.CancellationToken);

        result.Data!.Candidates.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RwMode_SweepWithoutDryRun_IsDenied()
    {
        var ex = await Should.ThrowAsync<AccessDeniedException>(() =>
            _sweep.Sweep("acme-web", false, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("memory_sweep requires mode full (current rw)");
    }

    [Fact]
    public async Task RoMode_PromotionListAllowed_AndDiscardDenied()
    {
        SetMode(perProject: "ro");

        var list = await _promotion.List("acme-web", cancellationToken: TestContext.Current.CancellationToken);
        list.Data!.Rows.ShouldBeEmpty();

        var ex = await Should.ThrowAsync<AccessDeniedException>(() =>
            _promotion.Discard("acme-web", "h1", TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("memory_promotion_discard requires mode rw (current ro)");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PromotionList_RejectsANonPositiveLimit(int limit)
    {
        SetMode(perProject: "rw");

        var ex = await Should.ThrowAsync<McpException>(() =>
            _promotion.List("acme-web", limit, cancellationToken: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("invalid-params: limit must be at least 1");
    }

    /// <summary>Permits every guarded call, so a denial in a test comes from the access mode and not the store.</summary>
    private sealed class FakeStore : FakeMemoryStore
    {
        public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

        public MemoryEntry Entry { get; set; } = new("h", "p.md", "project:acme-web", "v", 0);

        public double Rating { get; set; } = 0.9;

        public MemoryStats Stats { get; set; } = new(0, 0, []);

        public List<string> DeletedHashes { get; } = [];

        public List<string> DeletedContexts { get; } = [];

        public Dictionary<string, IReadOnlyList<MemoryEntry>> EntriesByContext { get; } = [];

        public IReadOnlyList<MemorySearchResult> SearchResults { get; set; } = [];

        public override Task<MemoryEntry> WriteAsync(MemoryWriteRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Entry);

        public override Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(SearchResults);

        public override Task<bool> DeleteAsync(string projectId, string hash,
            CancellationToken cancellationToken = default)
        {
            DeletedHashes.Add(hash);
            return Task.FromResult(true);
        }

        public override Task<int> DeleteContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default)
        {
            DeletedContexts.Add(context);
            return Task.FromResult(1);
        }

        public override Task<MemoryStats> GetStatsAsync(string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Stats);

        public override Task<MemoryEntryResult> ShareAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEntryResult(new MemoryEntry(hash, "p.md", ContextNaming.SharedContext, "v", 1), true));

        public override Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
            bool includeTtlRows, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractionCandidateRow>>([]);

        public override Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SharedIndex([], []));

        public override Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(["acme-web"]);

        public override Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult("{\"root\":\"\"}");

        public override Task<int> IngestFileAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(1);

        public override Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(1);

        public override Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingConfig(provider, model ?? "bundled", provider == "local" ? "local" : "remote"));

        public override Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbedPendingResult(0, 0));

        public override Task<MemoryEntryResult> AddContentAsync(string projectId, string path, string content,
            string? context, string? sourceFile = null, string? section = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEntryResult(new MemoryEntry("new-hash", path, context ?? "project:acme-web", content, 1), true));

        public override Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(EntriesByContext.TryGetValue(context, out var entries) ? entries : []);

        public override Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<EntryMetadata?>(new EntryMetadata(Rating, 30));

        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(Settings.TryGetValue(key, out var value) ? value : null);

        public override Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
        {
            Settings[key] = value;
            return Task.CompletedTask;
        }

        public override Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(
                Settings.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal));

        public override Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            Settings.Remove(key);
            return Task.CompletedTask;
        }

        public override Task<bool> SetEntryTtlAsync(string projectId, string hash, int? ttlDays,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FakeSyncService() : SyncService(new FakeCloudStore(), _ => Task.FromResult<SqliteConnection>(null!),
        (_, _) => Task.FromResult<SqliteConnection>(null!), (_, _) => Task.FromResult<SqliteConnection>(null!), TimeProvider.System, null!)
    {
        public override Task<SyncResult> MemorySyncAsync(string projectId, string? objectKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SyncResult(0, 0, 0));
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
        public Task BeginAsync(Workspace workspace, DateTimeOffset startedAt,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CloseAsync(string projectId, string workspaceId, WorkspaceStatus status, DateTimeOffset closedAt,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<Workspace> RequireActiveAsync(string projectId, string workspaceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new Workspace(workspaceId, projectId));
    }
}
