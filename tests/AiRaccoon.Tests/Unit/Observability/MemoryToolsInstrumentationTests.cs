using System.Data.Common;
using System.Diagnostics;
using AiRaccoon.Access;
using AiRaccoon.Core.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Workspace;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sync;
using AiRaccoon.Infrastructure.Workspace;
using AiRaccoon.Observability;
using AiRaccoon.Tools;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>Verifies that MemoryTools methods emit metrics and traces through ToolCallMetrics.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class MemoryToolsInstrumentationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Write_RecordsMetricsAndCreatesActivity()
    {
        var metrics = new ToolCallMetrics();
        using var invocationCollector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_tool_invocations");
        using var durationCollector = new MetricCollector<double>(metrics.Meter, "ai_raccoon_tool_duration_ms");

        var startedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "AiRaccoon.MemoryTools",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => startedActivities.Add(a),
            ActivityStopped = _ => { }
        };
        ActivitySource.AddActivityListener(listener);

        var store = new SimpleFakeStore { Entry = new MemoryEntry("h1", "p.md", "project:acme", "content", 5) };
        var tools = CreateTools(store, metrics);

        await tools.Write("acme", "content", cancellationToken: TestContext.Current.CancellationToken);

        var invocations = invocationCollector.GetMeasurementSnapshot();
        invocations.Count.ShouldBe(1);
        invocations[0].Value.ShouldBe(1);
        invocations[0].Tags["tool"].ShouldBe("memory_write");
        invocations[0].Tags["result"].ShouldBe("success");

        var durations = durationCollector.GetMeasurementSnapshot();
        durations.Count.ShouldBe(1);
        durations[0].Tags["tool"].ShouldBe("memory_write");

        startedActivities.Count.ShouldBeGreaterThanOrEqualTo(1);
        startedActivities.ShouldContain(a => a.OperationName == "memory_write");
    }

    [Fact]
    public async Task Write_WithStoreError_SetsErrorTags()
    {
        var metrics = new ToolCallMetrics();
        using var invocationCollector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_tool_invocations");

        var store = new SimpleFakeStore { ThrowOnWrite = true };
        var tools = CreateTools(store, metrics);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            tools.Write("acme", "content", cancellationToken: TestContext.Current.CancellationToken));

        var invocations = invocationCollector.GetMeasurementSnapshot();
        invocations.Count.ShouldBe(1);
        invocations[0].Tags["result"].ShouldBe("error");
        invocations[0].Tags["error_type"].ShouldBe("InvalidOperationException");
    }

    [Fact]
    public async Task Sync_NotConfigured_PreCheck_RecordsError()
    {
        var metrics = new ToolCallMetrics();
        using var invocationCollector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_tool_invocations");

        var store = new SimpleFakeStore();
        var tools = CreateTools(store, metrics, configureSync: false);

        var ex = await Should.ThrowAsync<ModelContextProtocol.McpException>(() =>
            tools.Sync("acme", cancellationToken: TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("sync-not-configured");

        var invocations = invocationCollector.GetMeasurementSnapshot();
        invocations.Count.ShouldBe(1);
        invocations[0].Tags["result"].ShouldBe("error");
        invocations[0].Tags["tool"].ShouldBe("memory_sync");
        invocations[0].Tags["error_type"].ShouldNotBeNull();
    }

    [Fact]
    public async Task Search_RecordsMetricsSuccessfully()
    {
        var metrics = new ToolCallMetrics();
        using var invocationCollector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_tool_invocations");

        var store = new SimpleFakeStore();
        var tools = CreateTools(store, metrics);

        await tools.Search("acme", "query", cancellationToken: TestContext.Current.CancellationToken);

        var invocations = invocationCollector.GetMeasurementSnapshot();
        invocations.Count.ShouldBe(1);
        invocations[0].Tags["tool"].ShouldBe("memory_search");
        invocations[0].Tags["result"].ShouldBe("success");
    }

    [Fact]
    public async Task Stats_RecordsMetricsSuccessfully()
    {
        var metrics = new ToolCallMetrics();
        using var invocationCollector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_tool_invocations");

        var store = new SimpleFakeStore();
        var tools = CreateTools(store, metrics);

        await tools.Stats("acme", cancellationToken: TestContext.Current.CancellationToken);

        var invocations = invocationCollector.GetMeasurementSnapshot();
        invocations.Count.ShouldBe(1);
        invocations[0].Tags["tool"].ShouldBe("memory_stats");
        invocations[0].Tags["result"].ShouldBe("success");
    }

    private static MemoryTools CreateTools(
        SimpleFakeStore store,
        ToolCallMetrics metrics,
        bool configureSync = true)
    {
        var workspaces = new WorkspaceService(store, new SimpleFakeWorkspaceStore(), new FakeTimeProvider(FixedNow));
        var sweeper = new SweepService(store, new FakeTimeProvider(FixedNow));
        var syncOptions = configureSync
            ? new SyncOptions { Endpoint = "http://test", Bucket = "b", AccessKey = "k", SecretKey = "s" }
            : new SyncOptions();
        return new MemoryTools(store, new SimpleFakeSyncService(), workspaces, sweeper,
            new MemoryAccessGuard(store),
            syncOptions,
            new ForgettingPolicyService(store, new MemoryAccessGuard(store)),
            metrics);
    }

    // ── Minimal fake implementations ──

    private sealed class SimpleFakeStore : IMemoryStore
    {
        public MemoryEntry? Entry { get; set; }
        public bool ThrowOnWrite { get; set; }

        public Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
        {
            if (ThrowOnWrite)
            {
                throw new InvalidOperationException("test error");
            }
            return Task.FromResult(Entry ?? new MemoryEntry("h1", "p.md", "project:test", "content", 1));
        }

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MemorySearchResult>>([]);

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default)
            => Task.FromResult("{}");

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryStats(0, 0, []));

        public Task<MemoryEntry> ShareAsync(string projectId, string hash, CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryEntry(hash, "p.md", "shared", "content", 1));

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default)
            => Task.FromResult(true);

        public Task<int> DeleteContextAsync(string projectId, string context, CancellationToken cancellationToken = default)
            => Task.FromResult(1);

        public Task<int> DeleteSourcePathAsync(string projectId, string path, CancellationToken cancellationToken = default)
            => throw new NotImplementedException();

        public Task<int> IngestFileAsync(string projectId, string path, string? context, CancellationToken cancellationToken = default)
            => Task.FromResult(1);

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context, CancellationToken cancellationToken = default)
            => Task.FromResult(2);

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string projectId, string provider, string? model,
            string? baseUrl, string? apiKey, CancellationToken cancellationToken = default)
            => Task.FromResult(new EmbeddingConfig(provider, model ?? "m", provider == "local" ? "local" : "remote"));

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit, CancellationToken cancellationToken = default)
            => Task.FromResult(new EmbedPendingResult(0, 0));

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context, CancellationToken cancellationToken = default)
            => Task.FromResult(new MemoryEntry("h", path, context ?? "project:test", content, 1));

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

        public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash, CancellationToken cancellationToken = default)
            => Task.FromResult<EntryMetadata?>(null);

        public Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

        public Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetEntryTtlAsync(string projectId, string hash, double ttlDays, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class SimpleFakeSyncService : SyncService
    {
        public SimpleFakeSyncService() : base(
            new SimpleFakeCloudStore(),
            _ => Task.FromResult<SqliteConnection>(null!),
            (_, _) => Task.FromResult<SqliteConnection>(null!),
            TimeProvider.System,
            null!)
        { }

        public override Task<SyncResult> MemorySyncAsync(string projectId, string objectKey,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SyncResult(0, 0, 0));
    }

    private sealed class SimpleFakeCloudStore : ICloudStore
    {
        public Task<CloudObject?> PullAsync(string objectKey, CancellationToken cancellationToken = default)
            => Task.FromResult<CloudObject?>(null);

        public Task<string> PushAsync(string objectKey, byte[] data, string? etag, CancellationToken cancellationToken = default)
            => Task.FromResult("fake-etag");
    }

    private sealed class SimpleFakeWorkspaceStore : IWorkspaceStore
    {
        public Task BeginAsync(string projectId, string workspaceId, DateTimeOffset startedAt,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CloseAsync(string projectId, string workspaceId, WorkspaceStatus status, DateTimeOffset closedAt,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
