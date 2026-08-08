using System.Diagnostics;
using AiRaccoon.Access;
using AiRaccoon.Core.Memory;
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
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>Verifies that MemoryTools methods emit metrics and traces through ToolCallMetrics.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ObservabilityCollection.Name)]
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
            Sample = (ref _) => ActivitySamplingResult.AllData,
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
    public async Task Write_WhenResponseEnvelopeFails_RecordsError_NotSuccess()
    {
        // WrapAsync (queue.GetMetaAsync) runs after the store call succeeds; a failure there
        // must still be recorded as an error, not as a success followed by a no-op RecordError.
        var metrics = new ToolCallMetrics();
        using var invocationCollector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_tool_invocations");
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "AiRaccoon.MemoryTools",
            Sample = (ref _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => { },
            ActivityStopped = stoppedActivities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var store = new SimpleFakeStore { Entry = new MemoryEntry("h1", "p.md", "project:acme", "content", 5) };
        var queue = new FakePromotionQueue { GetMetaError = new InvalidOperationException("meta boom") };
        var tools = new MemoryTools(store, new ToolGate(new MemoryAccessGuard(store), queue), metrics);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            tools.Write("acme", "content", cancellationToken: TestContext.Current.CancellationToken));

        var invocations = invocationCollector.GetMeasurementSnapshot();
        invocations.Count.ShouldBe(1);
        invocations[0].Tags["result"].ShouldBe("error");
        invocations[0].Tags["error_type"].ShouldBe("InvalidOperationException");

        var stopped = stoppedActivities.ShouldHaveSingleItem();
        stopped.Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Write_Duration_IncludesResponseEnvelopeLatency()
    {
        // The stopwatch must cover the full call, including WrapAsync's queue.GetMetaAsync —
        // not just the bookkeeping before it.
        var metrics = new ToolCallMetrics();
        using var durationCollector = new MetricCollector<double>(metrics.Meter, "ai_raccoon_tool_duration_ms");

        var store = new SimpleFakeStore { Entry = new MemoryEntry("h1", "p.md", "project:acme", "content", 5) };
        var queue = new FakePromotionQueue { GetMetaDelay = TimeSpan.FromMilliseconds(60) };
        var tools = new MemoryTools(store, new ToolGate(new MemoryAccessGuard(store), queue), metrics);

        await tools.Write("acme", "content", cancellationToken: TestContext.Current.CancellationToken);

        var durations = durationCollector.GetMeasurementSnapshot();
        durations.Count.ShouldBe(1);
        durations[0].Value.ShouldBeGreaterThanOrEqualTo(50.0);
    }

    [Fact]
    public async Task Sync_NotConfigured_PreCheck_RecordsError()
    {
        var metrics = new ToolCallMetrics();
        using var invocationCollector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_tool_invocations");

        var store = new SimpleFakeStore();
        var tools = new SyncTools(new SimpleFakeSyncService(),
            new SyncCloudStoreFactory(store, NullLoggerFactory.Instance),
            new ToolGate(new MemoryAccessGuard(store), new FakePromotionQueue()), metrics);

        var ex = await Should.ThrowAsync<SyncNotConfiguredException>(() =>
            tools.Sync("acme", TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("not configured");

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

        await tools.Stats("acme", TestContext.Current.CancellationToken);

        var invocations = invocationCollector.GetMeasurementSnapshot();
        invocations.Count.ShouldBe(1);
        invocations[0].Tags["tool"].ShouldBe("memory_stats");
        invocations[0].Tags["result"].ShouldBe("success");
    }

    private static MemoryTools CreateTools(
        SimpleFakeStore store,
        ToolCallMetrics metrics)
    {
        return new MemoryTools(store, new ToolGate(new MemoryAccessGuard(store), new FakePromotionQueue()), metrics);
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

        public Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MemorySearchResult>>([]);

        public Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult("{}");

        public Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default) => Task.FromResult(new MemoryStats(0, 0, []));


        public Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId,
            bool includeTtlRows, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<MemoryEntry> ShareAsync(string projectId, string hash, CancellationToken cancellationToken = default) => Task.FromResult(new MemoryEntry(hash, "p.md", "shared", "content", 1));

        public Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<int> DeleteContextAsync(string projectId, string context, CancellationToken cancellationToken = default) => Task.FromResult(1);

        public Task<int> DeleteSourcePathAsync(string projectId, string path, CancellationToken cancellationToken = default) => throw new NotImplementedException();

        public Task<int> IngestFileAsync(string projectId, string path, string? context, CancellationToken cancellationToken = default) => Task.FromResult(1);

        public Task<int> IngestDirectoryAsync(string projectId, string path, string? context, CancellationToken cancellationToken = default) => Task.FromResult(2);

        public Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new EmbeddingConfig(provider, model ?? "m", provider == "local" ? "local" : "remote"));

        public Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit, CancellationToken cancellationToken = default) => Task.FromResult(new EmbedPendingResult(0, 0));

        public Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context, string? sourceFile = null, string? section = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MemoryEntry("h", path, context ?? "project:test", content, 1));

        public Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MemoryEntry>>([]);

        public Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash, CancellationToken cancellationToken = default) => Task.FromResult<EntryMetadata?>(null);

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

    private sealed class SimpleFakeWorkspaceStore : IWorkspaceStore
    {
        public Task BeginAsync(string projectId, string workspaceId, DateTimeOffset startedAt,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CloseAsync(string projectId, string workspaceId, WorkspaceStatus status, DateTimeOffset closedAt,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
