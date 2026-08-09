using System.Diagnostics;
using AiRaccoon.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Sync;
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

/// <summary>
///     Verifies that a tool call emits metrics and traces. The emission is the filter's, not the
///     tool's, so each case runs the tool method inside the filter the server registers.
/// </summary>
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
        using var invocationCollector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);
        using var durationCollector = new MetricCollector<double>(metrics.Meter, OtlpNames.ToolDuration);

        var startedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OtlpNames.MemoryToolsScope,
            Sample = (ref _) => ActivitySamplingResult.AllData,
            ActivityStarted = a => startedActivities.Add(a),
            ActivityStopped = _ => { }
        };
        ActivitySource.AddActivityListener(listener);

        var store = new SimpleFakeStore { Entry = new MemoryEntry("h1", "p.md", "project:acme", "content", 5) };
        var tools = CreateTools(store);

        await WriteThroughFilterAsync(metrics, tools);

        var invocations = invocationCollector.GetMeasurementSnapshot();
        invocations.Count.ShouldBe(1);
        invocations[0].Value.ShouldBe(1);
        invocations[0].Tags["tool"].ShouldBe("memory_write");
        invocations[0].Tags["result"].ShouldBe("success");

        var durations = durationCollector.GetMeasurementSnapshot();
        durations.Count.ShouldBe(1);
        durations[0].Tags["tool"].ShouldBe("memory_write");

        startedActivities.Count.ShouldBeGreaterThanOrEqualTo(1);
        startedActivities.ShouldContain(a => a.OperationName == "tools/call memory_write");
    }

    [Fact]
    public async Task Write_WithStoreError_SetsErrorTags()
    {
        var metrics = new ToolCallMetrics();
        using var invocationCollector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);

        var store = new SimpleFakeStore { ThrowOnWrite = true };
        var tools = CreateTools(store);

        await Should.ThrowAsync<InvalidOperationException>(() => WriteThroughFilterAsync(metrics, tools));

        var invocations = invocationCollector.GetMeasurementSnapshot();
        invocations.Count.ShouldBe(1);
        invocations[0].Tags["result"].ShouldBe("error");
        invocations[0].Tags["error.type"].ShouldBe("InvalidOperationException");
    }

    [Fact]
    public async Task Write_WhenResponseEnvelopeFails_RecordsError_NotSuccess()
    {
        // WrapAsync (queue.GetMetaAsync) runs after the store call succeeds; a failure there
        // must still be recorded as an error, not as a success followed by a no-op RecordError.
        var metrics = new ToolCallMetrics();
        using var invocationCollector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OtlpNames.MemoryToolsScope,
            Sample = (ref _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => { },
            ActivityStopped = stoppedActivities.Add
        };
        ActivitySource.AddActivityListener(listener);

        var store = new SimpleFakeStore { Entry = new MemoryEntry("h1", "p.md", "project:acme", "content", 5) };
        var queue = new FakePromotionQueue { GetMetaError = new InvalidOperationException("meta boom") };
        var tools = new MemoryTools(store, new ToolGate(new MemoryAccessGuard(store), queue));

        await Should.ThrowAsync<InvalidOperationException>(() => WriteThroughFilterAsync(metrics, tools));

        var invocations = invocationCollector.GetMeasurementSnapshot();
        invocations.Count.ShouldBe(1);
        invocations[0].Tags["result"].ShouldBe("error");
        invocations[0].Tags["error.type"].ShouldBe("InvalidOperationException");

        var stopped = stoppedActivities.ShouldHaveSingleItem();
        stopped.Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task Write_Duration_IncludesResponseEnvelopeLatency()
    {
        // The stopwatch must cover the full call, including WrapAsync's queue.GetMetaAsync —
        // not just the bookkeeping before it.
        var metrics = new ToolCallMetrics();
        using var durationCollector = new MetricCollector<double>(metrics.Meter, OtlpNames.ToolDuration);

        var store = new SimpleFakeStore { Entry = new MemoryEntry("h1", "p.md", "project:acme", "content", 5) };
        var queue = new FakePromotionQueue { GetMetaDelay = TimeSpan.FromMilliseconds(60) };
        var tools = new MemoryTools(store, new ToolGate(new MemoryAccessGuard(store), queue));

        await WriteThroughFilterAsync(metrics, tools);

        var durations = durationCollector.GetMeasurementSnapshot();
        durations.Count.ShouldBe(1);
        durations[0].Value.ShouldBeGreaterThanOrEqualTo(0.05);
    }

    // SyncService owns the IsConfigured decision (NullCloudStore); this proves the tool's
    // metrics wrapper still records an error when the service throws it.
    [Fact]
    public async Task Sync_WhenServiceThrowsSyncNotConfigured_RecordsError()
    {
        var metrics = new ToolCallMetrics();
        using var invocationCollector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);

        var store = new SimpleFakeStore();
        var tools = new SyncTools(new SimpleFakeSyncService { Exception = new SyncNotConfiguredException() },
            new SyncCloudStoreFactory(store, NullLoggerFactory.Instance),
            new ToolGate(new MemoryAccessGuard(store), new FakePromotionQueue()));

        var ex = await Should.ThrowAsync<SyncNotConfiguredException>(() =>
            ThroughFilterAsync(metrics, "memory_sync", token => tools.Sync("acme", token)));

        ex.Message.ShouldContain("not configured");

        var invocations = invocationCollector.GetMeasurementSnapshot();
        invocations.Count.ShouldBe(1);
        invocations[0].Tags["result"].ShouldBe("error");
        invocations[0].Tags["tool"].ShouldBe("memory_sync");
        invocations[0].Tags["error.type"].ShouldNotBeNull();
    }

    [Fact]
    public async Task Search_RecordsMetricsSuccessfully()
    {
        var metrics = new ToolCallMetrics();
        using var invocationCollector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);

        var store = new SimpleFakeStore();
        var tools = CreateTools(store);

        await ThroughFilterAsync(metrics, "memory_search",
            token => tools.Search("acme", "query", cancellationToken: token));

        var invocations = invocationCollector.GetMeasurementSnapshot();
        invocations.Count.ShouldBe(1);
        invocations[0].Tags["tool"].ShouldBe("memory_search");
        invocations[0].Tags["result"].ShouldBe("success");
    }

    [Fact]
    public async Task Stats_RecordsMetricsSuccessfully()
    {
        var metrics = new ToolCallMetrics();
        using var invocationCollector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);

        var store = new SimpleFakeStore();
        var tools = CreateTools(store);

        await ThroughFilterAsync(metrics, "memory_stats", token => tools.Stats("acme", token));

        var invocations = invocationCollector.GetMeasurementSnapshot();
        invocations.Count.ShouldBe(1);
        invocations[0].Tags["tool"].ShouldBe("memory_stats");
        invocations[0].Tags["result"].ShouldBe("success");
    }

    private static MemoryTools CreateTools(SimpleFakeStore store) =>
        new(store, new ToolGate(new MemoryAccessGuard(store), new FakePromotionQueue()));

    private static Task WriteThroughFilterAsync(ToolCallMetrics metrics, MemoryTools tools) =>
        ThroughFilterAsync(metrics, "memory_write", token => tools.Write("acme", "content", cancellationToken: token));

    private static Task ThroughFilterAsync(ToolCallMetrics metrics, string toolName, Func<CancellationToken, Task> call) =>
        ToolCallRecorder.ThroughFilterAsync(metrics, toolName, ToolCallRecorder.Arguments(("projectId", "acme")),
            call, TestContext.Current.CancellationToken);

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

        public Task<bool> ReplaceFileAsync(string projectId, string path, string fileHash,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

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

        public Task<bool> SetEntryTtlAsync(string projectId, string hash, int? ttlDays, CancellationToken cancellationToken = default) => Task.FromResult(true);
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

        public Exception? Exception { get; set; }

        public override Task<SyncResult> MemorySyncAsync(string projectId, string? objectKey,
            CancellationToken cancellationToken = default) =>
            Exception is not null ? throw Exception : Task.FromResult(new SyncResult(0, 0, 0));
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

        public Task RequireActiveAsync(string projectId, string workspaceId,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
