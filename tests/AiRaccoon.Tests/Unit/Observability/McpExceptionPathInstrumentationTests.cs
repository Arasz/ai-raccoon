using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Observability;
using AiRaccoon.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     Every tool call must record an invocation metric, including paths that throw McpException
///     (invalid-params, access-denied) — the contract is "every call emits", not "every
///     non-McpException call emits". The filter is what emits, so each case runs through it.
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
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);
        var tools = new WatchTools(new FakeWatchService(), new ToolGate(new AllowAllGuard(), new FakePromotionQueue()));

        await Should.ThrowAsync<McpException>(() =>
            ThroughFilterAsync(metrics, "memory_watch_add", "", token => tools.Add("", "/repo", token)));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Count.ShouldBe(1);
        snapshot[0].Tags["tool"].ShouldBe("memory_watch_add");
        snapshot[0].Tags["result"].ShouldBe("error");
    }

    [Fact]
    public async Task WatchStatus_MissingProjectId_RecordsErrorMetric()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);
        var tools = new WatchTools(new FakeWatchService(), new ToolGate(new AllowAllGuard(), new FakePromotionQueue()));

        await Should.ThrowAsync<McpException>(() =>
            ThroughFilterAsync(metrics, "memory_watch_status", "", token => tools.Status("", token)));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Count.ShouldBe(1);
        snapshot[0].Tags["tool"].ShouldBe("memory_watch_status");
        snapshot[0].Tags["result"].ShouldBe("error");
    }

    [Fact]
    public async Task WatchRemove_MissingProjectId_RecordsErrorMetric()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);
        var tools = new WatchTools(new FakeWatchService(), new ToolGate(new AllowAllGuard(), new FakePromotionQueue()));

        await Should.ThrowAsync<McpException>(() =>
            ThroughFilterAsync(metrics, "memory_watch_remove", "", token => tools.Remove("", "/repo", token)));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Count.ShouldBe(1);
        snapshot[0].Tags["tool"].ShouldBe("memory_watch_remove");
        snapshot[0].Tags["result"].ShouldBe("error");
    }

    [Fact]
    public async Task WatchAdd_AccessDenied_RecordsErrorMetric()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);
        var tools = new WatchTools(new FakeWatchService(), new ToolGate(new DenyWriteGuard(), new FakePromotionQueue()));

        await Should.ThrowAsync<McpException>(() =>
            ThroughFilterAsync(metrics, "memory_watch_add", "proj-a", token => tools.Add("proj-a", "/repo", token)));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Count.ShouldBe(1);
        snapshot[0].Tags["tool"].ShouldBe("memory_watch_add");
        snapshot[0].Tags["result"].ShouldBe("error");
    }

    [Fact]
    public async Task Sync_AccessDenied_RecordsErrorMetric()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);
        var store = new SimpleFakeStore();
        var tools = new SyncTools(new SimpleFakeSyncService(),
            new SyncCloudStoreFactory(store, NullLoggerFactory.Instance),
            new ToolGate(new DenyWriteGuard(), new FakePromotionQueue()));

        await Should.ThrowAsync<McpException>(() =>
            ThroughFilterAsync(metrics, "memory_sync", "proj-a", token => tools.Sync("proj-a", token)));

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.Count.ShouldBe(1);
        snapshot[0].Tags["tool"].ShouldBe("memory_sync");
        snapshot[0].Tags["result"].ShouldBe("error");
    }

    private static Task ThroughFilterAsync(ToolCallMetrics metrics, string toolName, string projectId,
        Func<CancellationToken, Task> call) =>
        ToolCallRecorder.ThroughFilterAsync(metrics, toolName, ToolCallRecorder.Arguments(("projectId", projectId)),
            call, TestContext.Current.CancellationToken);

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

        public Task<MemoryEntryResult> ShareAsync(string projectId, string hash, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEntryResult(new MemoryEntry(hash, "p.md", "shared", "c", 1), true));

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<int> DeleteContextAsync(string projectId, string context, CancellationToken cancellationToken = default) => Task.FromResult(1);

        public Task<bool> ReplaceFileAsync(string projectId, string path, string fileHash,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<int> DeleteSourcePathAsync(string projectId, string path, CancellationToken cancellationToken = default) => Task.FromResult(0);

        public Task<int> IngestFileAsync(string projectId, string path, string? context, CancellationToken cancellationToken = default) => Task.FromResult(1);

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context, CancellationToken cancellationToken = default) => Task.FromResult(1);

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingConfig(provider, model ?? "m", provider == "local" ? "local" : "remote"));

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit, CancellationToken cancellationToken = default) => Task.FromResult(new EmbedPendingResult(0, 0));

        public Task<MemoryEntryResult> AddContentAsync(string projectId, string path, string content, string? context, string? sourceFile = null, string? section = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEntryResult(new MemoryEntry("h", path, context ?? "project:test", content, 1), true));

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

        public Task<bool> SetEntryTtlAsync(string projectId, string hash, int? ttlDays, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class SimpleFakeSyncService() : SyncService(new SimpleFakeCloudStore(),
        _ => Task.FromResult<SqliteConnection>(null!),
        (_, _) => Task.FromResult<SqliteConnection>(null!),
        (_, _) => Task.FromResult<SqliteConnection>(null!),
        TimeProvider.System,
        null!)
    {
        public override Task<SyncResult> MemorySyncAsync(string projectId, string? objectKey = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new SyncResult(0, 0, 0));
    }

    private sealed class SimpleFakeCloudStore : ICloudStore
    {
        public Task<CloudObject?> PullAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult<CloudObject?>(null);

        public Task<string> PushAsync(string objectKey, byte[] data, string? etag, CancellationToken cancellationToken = default) => Task.FromResult("fake-etag");
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
