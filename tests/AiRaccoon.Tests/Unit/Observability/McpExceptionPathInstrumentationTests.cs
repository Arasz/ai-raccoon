using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using AiRaccoon.Core.Workspace;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Workspace;
using AiRaccoon.Observability;
using AiRaccoon.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     Every tool call must record an invocation metric, including paths that throw McpException
///     (invalid-params, access-denied) — the ADR-0002 contract is "every call emits", not "every
///     non-McpException call emits". These pin the previously-unrecorded escape paths.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ObservabilityCollection.Name)]
public class McpExceptionPathInstrumentationTests
{
    [Fact]
    public async Task WatchAdd_MissingProjectId_RecordsErrorMetric()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_tool_invocations");
        var tools = new WatchTools(new FakeWatchService(), new AllowAllGuard(), metrics);

        await Should.ThrowAsync<McpException>(() =>
            tools.Add("", "/repo", TestContext.Current.CancellationToken));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Count.ShouldBe(1);
        snapshot[0].Tags["tool"].ShouldBe("memory_watch_add");
        snapshot[0].Tags["result"].ShouldBe("error");
    }

    [Fact]
    public async Task WatchStatus_MissingProjectId_RecordsErrorMetric()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_tool_invocations");
        var tools = new WatchTools(new FakeWatchService(), new AllowAllGuard(), metrics);

        await Should.ThrowAsync<McpException>(() =>
            tools.Status("", TestContext.Current.CancellationToken));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Count.ShouldBe(1);
        snapshot[0].Tags["tool"].ShouldBe("memory_watch_status");
        snapshot[0].Tags["result"].ShouldBe("error");
    }

    [Fact]
    public async Task WatchRemove_MissingProjectId_RecordsErrorMetric()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_tool_invocations");
        var tools = new WatchTools(new FakeWatchService(), new AllowAllGuard(), metrics);

        await Should.ThrowAsync<McpException>(() =>
            tools.Remove("", "/repo", TestContext.Current.CancellationToken));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Count.ShouldBe(1);
        snapshot[0].Tags["tool"].ShouldBe("memory_watch_remove");
        snapshot[0].Tags["result"].ShouldBe("error");
    }

    [Fact]
    public async Task WatchAdd_AccessDenied_RecordsErrorMetric()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_tool_invocations");
        var tools = new WatchTools(new FakeWatchService(), new DenyWriteGuard(), metrics);

        await Should.ThrowAsync<McpException>(() =>
            tools.Add("proj-a", "/repo", TestContext.Current.CancellationToken));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Count.ShouldBe(1);
        snapshot[0].Tags["tool"].ShouldBe("memory_watch_add");
        snapshot[0].Tags["result"].ShouldBe("error");
    }

    [Fact]
    public async Task Sync_AccessDenied_RecordsErrorMetric()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_tool_invocations");
        var store = new SimpleFakeStore();
        var tools = CreateMemoryTools(store, metrics, new DenyWriteGuard());

        await Should.ThrowAsync<McpException>(() =>
            tools.Sync("proj-a", TestContext.Current.CancellationToken));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Count.ShouldBe(1);
        snapshot[0].Tags["tool"].ShouldBe("memory_sync");
        snapshot[0].Tags["result"].ShouldBe("error");
    }

    private static MemoryTools CreateMemoryTools(SimpleFakeStore store, ToolCallMetrics metrics, IMemoryAccessGuard guard)
    {
        var workspaces = new WorkspaceService(store, new FakeWorkspaceStore(), new FakeTimeProvider());
        var sweeper = new SweepService(store, new FakeTimeProvider());
        return new MemoryTools(store, new SimpleFakeSyncService(), workspaces, sweeper,
            guard,
            new SyncCloudStoreFactory(store, NullLoggerFactory.Instance),
            new ForgettingPolicyService(store, guard),
            metrics, new SharedExtractionService());
    }

    private sealed class AllowAllGuard : IMemoryAccessGuard
    {
        public Task<AccessMode> ResolveAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(AccessMode.Full);

        public Task EnsureAsync(string projectId, AccessRequirement requirement, string toolName,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class DenyWriteGuard : IMemoryAccessGuard
    {
        public Task<AccessMode> ResolveAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(AccessMode.Ro);

        public Task EnsureAsync(string projectId, AccessRequirement requirement, string toolName,
            CancellationToken cancellationToken = default)
        {
            if (requirement != AccessRequirement.Read)
            {
                throw new McpException($"access-denied: project '{projectId}' is read-only");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class SimpleFakeStore : IMemoryStore
    {
        public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new MemoryEntry("h", "p.md", "project:test", "c", 1));

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MemorySearchResult>>([]);

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult("{}");

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(new MemoryStats(0, 0, []));

        public Task<MemoryEntry> ShareAsync(string projectId, string hash, CancellationToken cancellationToken = default) => Task.FromResult(new MemoryEntry(hash, "p.md", "shared", "c", 1));

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<int> DeleteContextAsync(string projectId, string context, CancellationToken cancellationToken = default) => Task.FromResult(1);

        public Task<int> DeleteSourcePathAsync(string projectId, string path, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<int> IngestFileAsync(string projectId, string path, string? context, CancellationToken cancellationToken = default) => Task.FromResult(1);

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context, CancellationToken cancellationToken = default) => Task.FromResult(1);

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingConfig(provider, model ?? "m", provider == "local" ? "local" : "remote"));

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit, CancellationToken cancellationToken = default) => Task.FromResult(new EmbedPendingResult(0, 0));

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEntry("h", path, context ?? "project:test", content, 1));

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

        public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash, CancellationToken cancellationToken = default) => Task.FromResult<EntryMetadata?>(null);

        public Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId, bool includeTtlRows,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractionCandidateRow>>([]);

        public Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SharedIndex([], []));

        public Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>([]);

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());

        public Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetEntryTtlAsync(string projectId, string hash, double ttlDays, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SimpleFakeSyncService : SyncService
    {
        public SimpleFakeSyncService() : base(
            new SimpleFakeCloudStore(),
            _ => Task.FromResult<SqliteConnection>(null!),
            (_, _) => Task.FromResult<SqliteConnection>(null!),
            (_, _) => Task.FromResult<SqliteConnection>(null!),
            TimeProvider.System,
            null!)
        {
        }

        public override Task<SyncResult> MemorySyncAsync(string projectId, string objectKey,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SyncResult(0, 0, 0));
    }

    private sealed class SimpleFakeCloudStore : ICloudStore
    {
        public Task<CloudObject?> PullAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult<CloudObject?>(null);

        public Task<string> PushAsync(string objectKey, byte[] data, string? etag, CancellationToken cancellationToken = default) => Task.FromResult("fake-etag");
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

    private sealed class FakeWatchService : IWatchService
    {
        public Task AddAsync(string projectId, string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RemoveAsync(string projectId, string path, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<WatchStatus>> StatusAsync(string projectId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WatchStatus>>([]);

        public Task<bool> IsEnabledAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> IsPathAllowedAsync(string projectId, string path, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
