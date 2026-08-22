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

    /// <summary>
    ///     F7 (owner ruling): self-metrics are behind the same "did something" condition as
    ///     pass.NoteWork() — a flush that drained a non-empty batch records duration/batch-size/drops.
    /// </summary>
    [Fact]
    public async Task FlushOnceAsync_NonEmptyBatch_RecordsItsOwnDurationAndBatchSize()
    {
        var buffer = new MeasurementBuffer(1000);
        buffer.TryEnqueue(AMeasurement());
        var store = new FakeMetricsStore();
        var flusher = CreateFlusher(buffer, store);

        await flusher.FlushOnceAsync(TestContext.Current.CancellationToken);

        store.Saved.ShouldContain(m => m.Name == "metrics.flush.duration_ms");
        store.Saved.Single(m => m.Name == "metrics.flush.batch_size").Value.ShouldBe(1);
    }

    /// <summary>
    ///     F7 (owner ruling): "it should be behind condition and emit something only when flush was
    ///     done - we don't need performance metrics for empty run." An idle tick (nothing drained)
    ///     writes nothing at all — not even the duration or batch-size-zero rows it used to write
    ///     unconditionally.
    /// </summary>
    [Fact]
    public async Task FlushOnceAsync_EmptyBatch_RecordsNoSelfMetrics()
    {
        var buffer = new MeasurementBuffer(1000);
        var store = new FakeMetricsStore();
        var flusher = CreateFlusher(buffer, store);

        await flusher.FlushOnceAsync(TestContext.Current.CancellationToken);

        store.Saved.ShouldBeEmpty("an idle flush must write nothing (F7): no self-metrics for an empty run");
    }

    /// <summary>
    ///     Finding 5: self-metrics were written with ProjectId = null, and MetricsReportService
    ///     filters `project_id = @ProjectId`, which can never match null — no surface could ever
    ///     show the drop count. Tagging them with the self-metrics sentinel project id makes them
    ///     reachable through that same equality filter, without polluting any real project's report.
    /// </summary>
    [Fact]
    public async Task FlushOnceAsync_TagsSelfMetrics_WithTheSelfMetricsProjectId()
    {
        var buffer = new MeasurementBuffer(1000);
        buffer.TryEnqueue(AMeasurement());
        var store = new FakeMetricsStore();
        var flusher = CreateFlusher(buffer, store);

        await flusher.FlushOnceAsync(TestContext.Current.CancellationToken);

        store.Saved.Where(m => MetricsConfigKeys.SelfMetricNames.Contains(m.Name))
            .ShouldAllBe(m => m.ProjectId == MetricsConfigKeys.SelfMetricsProjectId);
    }

    /// <summary>
    ///     F7 supersedes AC5: AC5 originally required self-metrics to land even at zero buffer
    ///     capacity, so a drop would always be visible. The owner's ruling is stronger and wins — an
    ///     empty run (nothing drained, batch.Count == 0) writes nothing, even though a drop happened,
    ///     because "did something" is defined the same way pass.NoteWork() already defines it. The
    ///     drop is not lost forever: DroppedCount is cumulative, so it surfaces on the next flush that
    ///     drains something.
    /// </summary>
    [Fact]
    public async Task FlushOnceAsync_BufferAtZeroCapacity_WritesNoSelfMetrics()
    {
        var buffer = new MeasurementBuffer(0);
        buffer.TryEnqueue(AMeasurement()).ShouldBeFalse("capacity 0 means every enqueue is dropped");

        var store = new FakeMetricsStore();
        var flusher = CreateFlusher(buffer, store);

        await flusher.FlushOnceAsync(TestContext.Current.CancellationToken);

        store.Saved.ShouldBeEmpty("nothing was drained, so F7's condition is not met even though a drop occurred");
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

    // ── ADR-0062: wait for the observable (TimerArmed / Flushes), never for the clock. No Task.Delay. ──

    /// <summary>Step budget for the advance-until helpers: a count of fake-clock steps, never a wall-clock deadline (PR #464).</summary>
    private const int MaxAdvanceSteps = 200;
    private static readonly TimeSpan AbsenceWindow = TimeSpan.FromMilliseconds(150);

    private static async Task AdvanceUntilAsync(FakeTimeProvider time, AiRaccoon.Infrastructure.Maintenance.TickSignal signal,
        long target, TimeSpan step, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxAdvanceSteps; attempt++)
        {
            time.Advance(step);
            if (await signal.WaitAsync(target, AbsenceWindow, cancellationToken))
            {
                return;
            }
        }

        throw new TimeoutException($"signal {target} did not fire within {MaxAdvanceSteps} fake-clock steps");
    }

    [Fact]
    public async Task ExecuteAsync_FlushesAtTheConfiguredInterval()
    {
        var settings = new InMemorySettings();
        settings.Values[MetricsConfigKeys.FlushIntervalSecondsGlobal] = "5";
        var time = new FakeTimeProvider(FixedNow);
        var buffer = new MeasurementBuffer(1000);
        buffer.TryEnqueue(AMeasurement()); // F7: self-metrics only land when a flush drains something
        var store = new FakeMetricsStore();
        var flusher = CreateFlusher(buffer, store, settings, time);

        using var cts = new CancellationTokenSource();
        var run = flusher.StartAsync(cts.Token);

        await flusher.TimerArmed.WaitAsync(1, TestContext.Current.CancellationToken);
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

        await flusher.TimerArmed.WaitAsync(1, TestContext.Current.CancellationToken);
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
    public async Task StopAsync_EmptyBuffer_RecordsNoSelfMetrics()
    {
        var buffer = new MeasurementBuffer(1000);
        var store = new FakeMetricsStore();
        var flusher = CreateFlusher(buffer, store);

        await flusher.StopAsync(TestContext.Current.CancellationToken);

        store.Saved.ShouldBeEmpty("F7: a shutdown flush that drained nothing writes no self-metrics");
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

        // The bound is fake time (ShutdownFlushTimeout, advanced above): StopAsync must complete
        // on its own; only the test's token ends a genuine hang (PR #464).
        await stopTask.WaitAsync(TestContext.Current.CancellationToken);
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
