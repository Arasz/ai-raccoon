using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Metrics;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Metrics;

/// <summary>
///     MetricsFlusher's own contract: a paused flusher (no ExecuteAsync loop running) writes
///     exactly what FlushOnceAsync is told to, self-instrumentation bypasses the buffer entirely
///     (cannot recurse by construction), a failed write is swallowed and not retried, and the
///     periodic loop follows ADR-0062 — wait for the observable (TimerArmed / Flushes), never for
///     the clock (docs/plans/2026-08-15-performance-metrics-implementation.md, WP3).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MetricsFlusherTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

    private static Measurement AMeasurement(string name = "test.metric") =>
        new(name, MeasurementKind.Counter, 1, "count", FixedNow);

    private static MetricsFlusher CreateFlusher(IMeasurementBuffer buffer, IMetricsStore store,
        ISettingsStore? settings = null, FakeTimeProvider? time = null) =>
        new(buffer, store, settings ?? new InMemorySettings(), time ?? new FakeTimeProvider(FixedNow),
            TestTelemetry.None, NullLogger<MetricsFlusher>.Instance);

    [Fact]
    public async Task FlushOnceAsync_BurstWithinCapacity_WritesAllMeasurementsWhole()
    {
        var buffer = new MeasurementBuffer(1000);
        for (var i = 0; i < 600; i++)
        {
            buffer.TryEnqueue(AMeasurement());
        }

        var store = new FakeMetricsStore();
        var flusher = CreateFlusher(buffer, store);

        await flusher.FlushOnceAsync(TestContext.Current.CancellationToken);

        store.Saved.Count(m => m.Name == "test.metric").ShouldBe(600);
    }

    [Fact]
    public async Task FlushOnceAsync_ExactlyAtCapacity_WritesAllWithNoDrop()
    {
        var buffer = new MeasurementBuffer(1000);
        for (var i = 0; i < 1000; i++)
        {
            buffer.TryEnqueue(AMeasurement());
        }

        var store = new FakeMetricsStore();
        var flusher = CreateFlusher(buffer, store);

        await flusher.FlushOnceAsync(TestContext.Current.CancellationToken);

        store.Saved.Count(m => m.Name == "test.metric").ShouldBe(1000);
        store.Saved.Single(m => m.Name == "metrics.dropped").Value.ShouldBe(0);
    }

    [Fact]
    public async Task FlushOnceAsync_BeyondCapacity_DropsExactlyTheOverflowAndReportsIt()
    {
        var buffer = new MeasurementBuffer(1000);
        for (var i = 0; i < 1200; i++)
        {
            buffer.TryEnqueue(AMeasurement());
        }

        var store = new FakeMetricsStore();
        var flusher = CreateFlusher(buffer, store);

        await flusher.FlushOnceAsync(TestContext.Current.CancellationToken);

        store.Saved.Count(m => m.Name == "test.metric").ShouldBe(1000);
        store.Saved.Single(m => m.Name == "metrics.dropped").Value.ShouldBe(200);
    }

    [Fact]
    public async Task FlushOnceAsync_AlwaysRecordsItsOwnDurationAndBatchSize()
    {
        var buffer = new MeasurementBuffer(1000);
        var store = new FakeMetricsStore();
        var flusher = CreateFlusher(buffer, store);

        await flusher.FlushOnceAsync(TestContext.Current.CancellationToken);

        store.Saved.ShouldContain(m => m.Name == "metrics.flush.duration_ms");
        store.Saved.Single(m => m.Name == "metrics.flush.batch_size").Value.ShouldBe(0);
    }

    /// <summary>AC5: self-metrics land even when the buffer can accept nothing at all.</summary>
    [Fact]
    public async Task FlushOnceAsync_BufferAtZeroCapacity_StillWritesSelfMetrics()
    {
        var buffer = new MeasurementBuffer(0);
        buffer.TryEnqueue(AMeasurement()).ShouldBeFalse("capacity 0 means every enqueue is dropped");

        var store = new FakeMetricsStore();
        var flusher = CreateFlusher(buffer, store);

        await flusher.FlushOnceAsync(TestContext.Current.CancellationToken);

        store.Saved.ShouldContain(m => m.Name == "metrics.flush.duration_ms");
        store.Saved.ShouldContain(m => m.Name == "metrics.flush.batch_size");
        store.Saved.Single(m => m.Name == "metrics.dropped").Value.ShouldBe(1);
    }

    /// <summary>No-recursion gate: self-metrics bypass the buffer entirely, so a flush cannot move EnqueuedCount.</summary>
    [Fact]
    public async Task FlushOnceAsync_NeverEnqueuesIntoTheBuffer()
    {
        var buffer = new MeasurementBuffer(1000);
        buffer.TryEnqueue(AMeasurement());
        buffer.TryEnqueue(AMeasurement());
        var before = buffer.EnqueuedCount;

        var store = new FakeMetricsStore();
        var flusher = CreateFlusher(buffer, store);
        await flusher.FlushOnceAsync(TestContext.Current.CancellationToken);

        buffer.EnqueuedCount.ShouldBe(before, "self-instrumentation must never route through TryEnqueue — it cannot recurse by construction");
    }

    [Fact]
    public async Task FlushOnceAsync_StoreThrowsOnEveryWrite_DoesNotThrowAndDoesNotRetry()
    {
        var buffer = new MeasurementBuffer(1000);
        buffer.TryEnqueue(AMeasurement());
        var store = new FakeMetricsStore { ThrowOnSave = true };
        var flusher = CreateFlusher(buffer, store);

        await Should.NotThrowAsync(() => flusher.FlushOnceAsync(TestContext.Current.CancellationToken));

        // One attempt for the drained batch, one for self-metrics — never more (no retry loop).
        store.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task FlushOnceAsync_EmptyBuffer_StillCompletesAndRecordsSelfMetrics()
    {
        var buffer = new MeasurementBuffer(1000);
        var store = new FakeMetricsStore();
        var flusher = CreateFlusher(buffer, store);

        await flusher.FlushOnceAsync(TestContext.Current.CancellationToken);

        store.Saved.Single(m => m.Name == "metrics.flush.batch_size").Value.ShouldBe(0);
    }

    // ── ADR-0062: wait for the observable (TimerArmed / Flushes), never for the clock. No Task.Delay. ──

    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan AbsenceWindow = TimeSpan.FromMilliseconds(150);

    private static async Task AdvanceUntilAsync(FakeTimeProvider time, AiRaccoon.Infrastructure.Maintenance.TickSignal signal,
        long target, TimeSpan step, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + SignalTimeout;
        while (DateTime.UtcNow < deadline)
        {
            time.Advance(step);
            if (await signal.WaitAsync(target, AbsenceWindow, cancellationToken))
            {
                return;
            }
        }

        throw new TimeoutException($"signal {target} did not fire within {SignalTimeout}");
    }

    [Fact]
    public async Task ExecuteAsync_FlushesAtTheConfiguredInterval()
    {
        var settings = new InMemorySettings();
        settings.Values[MetricsConfigKeys.FlushIntervalSecondsGlobal] = "5";
        var time = new FakeTimeProvider(FixedNow);
        var buffer = new MeasurementBuffer(1000);
        var store = new FakeMetricsStore();
        var flusher = CreateFlusher(buffer, store, settings, time);

        using var cts = new CancellationTokenSource();
        var run = flusher.StartAsync(cts.Token);

        (await flusher.TimerArmed.WaitAsync(1, SignalTimeout, TestContext.Current.CancellationToken)).ShouldBeTrue();
        await AdvanceUntilAsync(time, flusher.Flushes, 1, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        run.IsFaulted.ShouldBeFalse();
        store.Saved.ShouldContain(m => m.Name == "metrics.flush.duration_ms");

        await flusher.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExecuteAsync_MissingSetting_FallsBackToDefaultInterval()
    {
        var time = new FakeTimeProvider(FixedNow);
        var buffer = new MeasurementBuffer(1000);
        var store = new FakeMetricsStore();
        var flusher = CreateFlusher(buffer, store, new InMemorySettings(), time);

        using var cts = new CancellationTokenSource();
        var run = flusher.StartAsync(cts.Token);

        (await flusher.TimerArmed.WaitAsync(1, SignalTimeout, TestContext.Current.CancellationToken)).ShouldBeTrue();
        await AdvanceUntilAsync(time, flusher.Flushes, 1,
            TimeSpan.FromSeconds(MetricsConfigKeys.DefaultFlushIntervalSeconds), TestContext.Current.CancellationToken);

        run.IsFaulted.ShouldBeFalse();

        await flusher.StopAsync(TestContext.Current.CancellationToken);
    }

    // ── Blocker 2: StopAsync must drain and flush the buffer, bounded, before the host stops. ──

    [Fact]
    public async Task StopAsync_DrainsAndFlushesTheBufferBeforeStopping()
    {
        var buffer = new MeasurementBuffer(1000);
        buffer.TryEnqueue(AMeasurement());
        buffer.TryEnqueue(AMeasurement());
        var store = new FakeMetricsStore();
        var flusher = CreateFlusher(buffer, store);

        await flusher.StopAsync(TestContext.Current.CancellationToken);

        store.Saved.Count(m => m.Name == "test.metric").ShouldBe(2,
            "a session that only ever enqueues must not lose its buffered rows on shutdown");
    }

    [Fact]
    public async Task StopAsync_EmptyBuffer_StillCompletesAndRecordsSelfMetrics()
    {
        var buffer = new MeasurementBuffer(1000);
        var store = new FakeMetricsStore();
        var flusher = CreateFlusher(buffer, store);

        await flusher.StopAsync(TestContext.Current.CancellationToken);

        store.Saved.ShouldContain(m => m.Name == "metrics.flush.duration_ms");
    }

    [Fact]
    public async Task StopAsync_StoreHangsOnTheFinalFlush_ReturnsWithinTheBoundedTimeout_InsteadOfHanging()
    {
        var buffer = new MeasurementBuffer(1000);
        buffer.TryEnqueue(AMeasurement());
        var store = new HangingMetricsStore();
        var time = new FakeTimeProvider(FixedNow);
        var flusher = CreateFlusher(buffer, store, time: time);

        var stopTask = flusher.StopAsync(CancellationToken.None);
        time.Advance(MetricsFlusher.ShutdownFlushTimeout + TimeSpan.FromSeconds(1));

        (await Task.WhenAny(stopTask, Task.Delay(SignalTimeout, TestContext.Current.CancellationToken)))
            .ShouldBe(stopTask, "a hanging store must not hang shutdown");
        await stopTask;
    }

    private sealed class FakeMetricsStore : IMetricsStore
    {
        public List<Measurement> Saved { get; } = [];
        public int CallCount { get; private set; }
        public bool ThrowOnSave { get; init; }

        public Task SaveBatchAsync(IReadOnlyList<Measurement> measurements, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (ThrowOnSave)
            {
                throw new InvalidOperationException("writer boom");
            }

            Saved.AddRange(measurements);
            return Task.CompletedTask;
        }
    }

    /// <summary>Never completes on its own — only cancellation (the shutdown-flush timeout) ends the wait.</summary>
    private sealed class HangingMetricsStore : IMetricsStore
    {
        public Task SaveBatchAsync(IReadOnlyList<Measurement> measurements, CancellationToken cancellationToken = default) =>
            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
