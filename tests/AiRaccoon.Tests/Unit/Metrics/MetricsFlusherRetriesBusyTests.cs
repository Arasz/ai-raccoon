using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Metrics;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Metrics;

/// <summary>
///     WP12 Fix C: <c>MetricsFlusher.TrySaveBatchAsync</c> used to drop a batch on the FIRST
///     <c>SqliteException</c>, including a transient BUSY/LOCKED from the same write-lock convoy
///     WP12 fixes elsewhere. It now retries a code-5/6 failure up to 3 attempts with a short
///     bounded backoff before falling back to the existing drop + 970 log.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MetricsFlusherRetriesBusyTests
{
    private const int Sqlite5Busy = 5;
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

    private static Measurement AMeasurement() => new("test.metric", MeasurementKind.Counter, 1, "count", FixedNow);

    [Fact]
    public async Task FlushOnceAsync_StoreThrowsBusyTwiceThenSucceeds_SavesTheBatch()
    {
        var buffer = new MeasurementBuffer(1000);
        buffer.TryEnqueue(AMeasurement());
        var time = new FakeTimeProvider(FixedNow);
        var store = new FlakyMetricsStore(2);
        var flusher = new MetricsFlusher(buffer, store, new InMemorySettings(), time, TestTelemetry.None,
            new FakeLogger<MetricsFlusher>());

        var flushTask = flusher.FlushOnceAsync(TestContext.Current.CancellationToken);
        await AdvanceUntilDoneAsync(time, flushTask, TestContext.Current.CancellationToken);

        store.BatchCallCount.ShouldBe(3, "two BUSY failures, then the successful attempt");
        store.SavedBatchSizes.ShouldContain(1, "the batch must have actually landed, not just stopped throwing");
    }

    [Fact]
    public async Task FlushOnceAsync_StoreThrowsBusyThreeTimes_DropsTheBatchAndLogs970Once()
    {
        var buffer = new MeasurementBuffer(1000);
        buffer.TryEnqueue(AMeasurement());
        var time = new FakeTimeProvider(FixedNow);
        var store = new FlakyMetricsStore(3);
        var logger = new FakeLogger<MetricsFlusher>();
        var flusher = new MetricsFlusher(buffer, store, new InMemorySettings(), time, TestTelemetry.None, logger);

        var flushTask = flusher.FlushOnceAsync(TestContext.Current.CancellationToken);
        await AdvanceUntilDoneAsync(time, flushTask, TestContext.Current.CancellationToken);

        store.BatchCallCount.ShouldBe(3, "three attempts, all BUSY — never a fourth");
        store.SavedBatchSizes.ShouldBeEmpty("the batch was dropped, not saved");
        logger.Collector.GetSnapshot().Count(r => r.Id.Id == 970).ShouldBe(1, "the drop logs exactly once, not per retry");
    }

    /// <summary>
    ///     Drives a bounded, short retry backoff on a <see cref="FakeTimeProvider" /> that never
    ///     advances on its own: not a wall-clock wait, a poll bounded by a fake-clock step count
    ///     (mirrors <c>MetricsFlusherTests.AdvanceUntilAsync</c>).
    /// </summary>
    private static async Task AdvanceUntilDoneAsync(FakeTimeProvider time, Task task, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200 && !task.IsCompleted; attempt++)
        {
            time.Advance(TimeSpan.FromMilliseconds(50));
            await Task.Delay(5, cancellationToken);
        }

        await task;
    }

    /// <summary>
    ///     Throws <see cref="SqliteException" /> (code 5, BUSY) the given number of times before
    ///     succeeding, counted only for the size-1 batch under test — a self-metrics write (size 3:
    ///     duration/batch_size/dropped) always succeeds so it never pollutes the retry count.
    /// </summary>
    private sealed class FlakyMetricsStore(int failuresBeforeSuccess) : IMetricsStore
    {
        private int _failuresLeft = failuresBeforeSuccess;

        public int BatchCallCount { get; private set; }

        public List<int> SavedBatchSizes { get; } = [];

        public Task SaveBatchAsync(IReadOnlyList<Measurement> measurements, CancellationToken cancellationToken = default)
        {
            if (measurements.Count != 1)
            {
                return Task.CompletedTask;
            }

            BatchCallCount++;
            if (_failuresLeft > 0)
            {
                _failuresLeft--;
                throw new SqliteException("database is locked", Sqlite5Busy);
            }

            SavedBatchSizes.Add(measurements.Count);
            return Task.CompletedTask;
        }
    }
}
