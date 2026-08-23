using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     WP11 (log-values-as-metrics): EventId 1003's "Embed drain pass finished for {Corpus}: {Rows}
///     row(s)" carries a value that today only exists as log text. This is that value recorded as
///     the same numbers the log line already computed — one computation, two destinations.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EmbedDrainMetricsTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("embed-drain-metrics");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeTimeProvider _timeProvider = new(FixedNow);

    public EmbedDrainMetricsTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private EmbedDrainService NewService(IEventPump<EmbedDrainRequest> pump, RecordingMeasurementRecorder measurements,
        IEntryEmbedder? entry = null, ICodeEmbedder? code = null) =>
        new(pump, _factory, entry ?? new StubEntryEmbedder(), code ?? new StubCodeEmbedder(),
            new SqliteSettingsStore(_factory), measurements, _timeProvider, TestTelemetry.None,
            NullLogger<EmbedDrainService>.Instance);

    [Fact]
    public async Task OneFullPass_RecordsDrainRowsAndDurationForTheCorpus()
    {
        var pump = TestData.NewEmbedDrainPump();
        var measurements = new RecordingMeasurementRecorder();
        var entry = new StubEntryEmbedder { RowsToReturn = 5 };
        var service = NewService(pump, measurements, entry: entry);
        var request = new EmbedDrainRequest(EmbedCorpus.Memory);

        await service.DrainOnceAsync(request, TestContext.Current.CancellationToken);

        var rows = measurements.Recorded.Single(m => m.Name == "drain.memory.rows");
        rows.Kind.ShouldBe(MeasurementKind.Histogram);
        rows.Value.ShouldBe(5);
        rows.ProjectId.ShouldBe(MetricsConfigKeys.SelfMetricsProjectId);

        var duration = measurements.Recorded.Single(m => m.Name == "drain.memory.duration_ms");
        duration.Kind.ShouldBe(MeasurementKind.Histogram);
        duration.ProjectId.ShouldBe(MetricsConfigKeys.SelfMetricsProjectId);
    }

    [Fact]
    public async Task CodeCorpusPass_RecordsUnderTheCodeSeriesName()
    {
        var pump = TestData.NewEmbedDrainPump();
        var measurements = new RecordingMeasurementRecorder();
        var code = new StubCodeEmbedder { RowsToReturn = 3 };
        var service = NewService(pump, measurements, code: code);
        var request = new EmbedDrainRequest(EmbedCorpus.Code);

        await service.DrainOnceAsync(request, TestContext.Current.CancellationToken);

        measurements.Recorded.Single(m => m.Name == "drain.code.rows").Value.ShouldBe(3);
        measurements.Recorded.ShouldContain(m => m.Name == "drain.code.duration_ms");
    }

    private sealed class StubEntryEmbedder : IEntryEmbedder
    {
        public int RowsToReturn { get; set; }

        public Task<int> EmbedPendingBatchAsync(SqliteConnection connection, int limit,
            CancellationToken cancellationToken) => Task.FromResult(RowsToReturn);

        public Task<EmbeddingConfig> StartMigrationAsync(SqliteConnection connection, string provider, string? model,
            string? baseUrl, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DrainMigrationAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task ReconcileVecDimensionsAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task EmbedIfConfiguredAsync(SqliteConnection connection, long id, string value,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> EmbedPendingAsync(SqliteConnection connection, string projectId, int? limit,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<QueryVector> EmbedQueryAsync(SqliteConnection connection, string query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<EmbeddingSettings> ReadSettingsAsync(SqliteConnection connection,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubCodeEmbedder : ICodeEmbedder
    {
        public int RowsToReturn { get; set; }

        public Task<int> EmbedPendingBatchAsync(SqliteConnection connection, int limit,
            CancellationToken cancellationToken) => Task.FromResult(RowsToReturn);

        public Task<QueryVector> EmbedQueryAsync(SqliteConnection connection, string query,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> HasPendingWorkAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> ReconcileFingerprintAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
