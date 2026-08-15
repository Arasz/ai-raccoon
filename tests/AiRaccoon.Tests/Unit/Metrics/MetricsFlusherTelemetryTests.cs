using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Metrics;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Metrics;

/// <summary>
///     BackgroundInstrumentationCoverageTests requires every hosted service to take
///     IOperationTelemetry and call NoteWork() somewhere in its pass. A flush at this service's
///     cadence (default 30s) is far more frequent than BankMaintenanceHostedService's
///     hourly-or-slower pass, so — following SweepHostedService's precedent, not
///     BankMaintenanceHostedService's unconditional call — NoteWork() fires only when the drained
///     batch was non-empty: an idle flush stays quiet on purpose (WP13 span-volume fix), while a
///     flush that persisted buffered measurements is always worth a span.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MetricsFlusherTelemetryTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

    private static Measurement AMeasurement() =>
        new("test.metric", MeasurementKind.Counter, 1, "count", FixedNow);

    private static MetricsFlusher CreateFlusher(IMeasurementBuffer buffer, IMetricsStore store,
        AiRaccoon.Core.Observability.IOperationTelemetry telemetry) =>
        new(buffer, store, new InMemorySettings(), new FakeTimeProvider(FixedNow), telemetry,
            NullLogger<MetricsFlusher>.Instance);

    private sealed class FakeMetricsStore : IMetricsStore
    {
        public bool ThrowOnSave { get; init; }

        public Task SaveBatchAsync(IReadOnlyList<Measurement> measurements, CancellationToken cancellationToken = default) =>
            ThrowOnSave ? throw new InvalidOperationException("writer boom") : Task.CompletedTask;
    }

    [Fact]
    public async Task FlushOnceAsync_DrainsANonEmptyBatch_EmitsExactlyOneSpan()
    {
        using var probe = new BackgroundTelemetryProbe(MetricsFlusher.OperationName);
        var buffer = new MeasurementBuffer(10);
        buffer.TryEnqueue(AMeasurement());
        var flusher = CreateFlusher(buffer, new FakeMetricsStore(), probe.Telemetry);

        await flusher.FlushOnceAsync(TestContext.Current.CancellationToken);

        probe.Spans.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task FlushOnceAsync_EmptyBuffer_EmitsNoSpan_ButStillRecordsTheCounterAndDuration()
    {
        using var probe = new BackgroundTelemetryProbe(MetricsFlusher.OperationName);
        var buffer = new MeasurementBuffer(10);
        var flusher = CreateFlusher(buffer, new FakeMetricsStore(), probe.Telemetry);

        await flusher.FlushOnceAsync(TestContext.Current.CancellationToken);

        probe.Spans.ShouldBeEmpty("an idle flush must stay quiet on purpose (WP13 span-volume fix)");
        probe.Passes.ShouldHaveSingleItem().Tags["result"].ShouldBe("success");
        probe.Durations.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task FlushOnceAsync_StoreThrowsOnTheBatchWrite_ResultIsNotSuccess()
    {
        using var probe = new BackgroundTelemetryProbe(MetricsFlusher.OperationName);
        var buffer = new MeasurementBuffer(10);
        buffer.TryEnqueue(AMeasurement());
        var flusher = CreateFlusher(buffer, new FakeMetricsStore { ThrowOnSave = true }, probe.Telemetry);

        await flusher.FlushOnceAsync(TestContext.Current.CancellationToken);

        probe.Passes.ShouldHaveSingleItem().Tags["result"].ShouldNotBe("success");
    }
}
