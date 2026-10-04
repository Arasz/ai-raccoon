using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Observability;
using AiRaccoon.Tests;
using AiRaccoon.Tests.TestHelpers;
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
        var tools = new WatchTools(new NoOpWatchService(), new ToolGate(new AllowAllGuard(), new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard()));

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
        var tools = new WatchTools(new NoOpWatchService(), new ToolGate(new AllowAllGuard(), new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard()));

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
        var tools = new WatchTools(new NoOpWatchService(), new ToolGate(new AllowAllGuard(), new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard()));

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
        var tools = new WatchTools(new NoOpWatchService(), new ToolGate(new DenyWriteGuard(), new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard()));

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
            new ToolGate(new DenyWriteGuard(), new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard()));

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

    private sealed class SimpleFakeStore : PermissiveFakeMemoryStore
    {
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

}
