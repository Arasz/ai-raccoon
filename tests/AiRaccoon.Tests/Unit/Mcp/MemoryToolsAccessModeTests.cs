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

/// <summary>
///     Access-mode gating at the MCP tool boundary (FR-NM-2 scenarios 1-4, 7-8; see docs/work/features-native-memory/native-memory.feature): reads are
///     allowed in every mode, writes need rw+, removal needs full.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MemoryToolsAccessModeTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeStore _store = new();
    private readonly MemoryTools _tools;

    public MemoryToolsAccessModeTests()
    {
        var workspaces = new WorkspaceService(_store, new FakeWorkspaceStore(), new FakeTimeProvider(FixedNow));
        var sweeper = new SweepService(_store, new FakeTimeProvider(FixedNow));
        _tools = new MemoryTools(_store, new FakeSyncService(), workspaces, sweeper,
            new MemoryAccessGuard(_store), new SyncCloudStoreFactory(_store, NullLoggerFactory.Instance),
            new ForgettingPolicyService(_store, new MemoryAccessGuard(_store)),
            new ToolCallMetrics(), new SharedExtractionService());
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

    // Scenario 1: default mode is rw.
    [Fact]
    public async Task DefaultMode_WriteSucceeds_AndDeleteIsDenied()
    {
        _store.Entry = new MemoryEntry("h1", "p.md", "project:acme-web", "content", 1);

        var written = await _tools.Write("acme-web", "content", cancellationToken: TestContext.Current.CancellationToken);

        written.Hash.ShouldBe("h1");

        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Delete("acme-web", "h1", TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("access-denied: memory_delete requires mode full (current rw)");
    }

    // Scenario 2: ro allows reading only.
    [Fact]
    public async Task RoMode_WriteIsDenied_AndSearchStillWorks()
    {
        SetMode(perProject: "ro");
        _store.SearchResults = [new MemorySearchResult("h1", 1, 0.9, "p.md", "content")];

        var writeEx = await Should.ThrowAsync<McpException>(() =>
            _tools.Write("acme-web", "content", cancellationToken: TestContext.Current.CancellationToken));
        writeEx.Message.ShouldContain("access-denied: memory_write requires mode rw (current ro)");

        var results = await _tools.Search("acme-web", "query", cancellationToken: TestContext.Current.CancellationToken);
        results.Results.Count.ShouldBe(1);
        results.Results[0].Snippet.ShouldBe("content");
    }

    // Scenario: propose is a read (allowed in ro); promote is a write (denied below rw).
    [Fact]
    public async Task RoMode_ShareExtractProposeAllowed_PromoteDenied()
    {
        SetMode(perProject: "ro");

        var result = await _tools.ShareExtract(["acme-web"], cancellationToken: TestContext.Current.CancellationToken);

        result.Candidates.ShouldBeEmpty();

        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.ShareExtract(["acme-web"], mode: "promote", cancellationToken: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("access-denied");
    }

    // Scenario 3: full allows removal.
    [Fact]
    public async Task FullMode_DeleteRemovesTheEntry()
    {
        SetMode(perProject: "full");

        var result = await _tools.Delete("acme-web", "h1", TestContext.Current.CancellationToken);

        result.Deleted.ShouldBe(1);
        _store.DeletedHashes.ShouldContain("h1");
    }

    // Scenario 4: full allows workspace discard.
    [Fact]
    public async Task FullMode_WorkspaceDiscardRemovesTheWorkspace()
    {
        SetMode(perProject: "full");
        _store.EntriesByContext["workspace:ws-1"] = [];

        var result = await _tools.WorkspaceDiscard("acme-web", "ws-1", TestContext.Current.CancellationToken);

        _store.DeletedContexts.ShouldContain("workspace:ws-1");
    }

    // Scenario 7: the global mode applies to projects without a per-project mode.
    [Fact]
    public async Task GlobalMode_AppliesToProjectWithoutPerProjectOverride()
    {
        SetMode(global: "rw");

        var result = await _tools.Write("other-app", "content", cancellationToken: TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
    }

    // Scenario 8: the global mode can be tightened to ro.
    [Fact]
    public async Task GlobalModeRo_DeniesWritesForProjectWithoutPerProjectOverride()
    {
        SetMode(global: "ro");

        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Write("other-app", "content", cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("access-denied: memory_write requires mode rw (current ro)");
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

        var result = await _tools.Sweep("acme-web", cancellationToken: TestContext.Current.CancellationToken);

        result.Candidates.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RwMode_SweepWithoutDryRun_IsDenied()
    {
        var ex = await Should.ThrowAsync<McpException>(() =>
            _tools.Sweep("acme-web", false, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("access-denied: memory_sweep requires mode full (current rw)");
    }

    private sealed class FakeStore : IMemoryStore
    {
        public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);

        public MemoryEntry Entry { get; set; } = new("h", "p.md", "project:acme-web", "v", 0);

        public double Rating { get; set; } = 0.9;

        public MemoryStats Stats { get; set; } = new(0, 0, []);

        public List<string> DeletedHashes { get; } = [];

        public List<string> DeletedContexts { get; } = [];

        public Dictionary<string, IReadOnlyList<MemoryEntry>> EntriesByContext { get; } = [];

        public IReadOnlyList<MemorySearchResult> SearchResults { get; set; } = [];

        public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default) => Task.FromResult(Entry);

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MemorySearchResult>>(SearchResults);

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default)
        {
            DeletedHashes.Add(hash);
            return Task.FromResult(true);
        }

        public Task<int> DeleteContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default)
        {
            DeletedContexts.Add(context);
            return Task.FromResult(1);
        }

        public Task<int> DeleteSourcePathAsync(string projectId, string path,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(Stats);

        public Task<MemoryEntry> ShareAsync(string projectId, string hash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEntry(hash, "p.md", ContextNaming.SharedContext, "v", 1));

        public Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
            bool includeTtlRows, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractionCandidateRow>>([]);

        public Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new SharedIndex([], []));

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult("{\"root\":\"\"}");

        public Task<int> IngestFileAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(1);

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(1);

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingConfig(provider, model ?? "bundled", provider == "local" ? "local" : "remote"));

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbedPendingResult(0, 0));

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEntry("new-hash", path, context ?? "project:acme-web", content, 1));

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(EntriesByContext.TryGetValue(context, out var entries) ? entries : []);

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
        (_, _) => Task.FromResult<SqliteConnection>(null!), TimeProvider.System, null!)
    {
        public override Task<SyncResult> MemorySyncAsync(string projectId, string objectKey,
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
        public Task BeginAsync(string projectId, string workspaceId, DateTimeOffset startedAt,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CloseAsync(string projectId, string workspaceId, WorkspaceStatus status, DateTimeOffset closedAt,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
