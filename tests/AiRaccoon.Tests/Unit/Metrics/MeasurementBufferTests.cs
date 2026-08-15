using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Metrics;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Metrics;

/// <summary>
///     The capped buffer's own arithmetic: a burst within capacity is whole, exactly-capacity is
///     whole, and beyond capacity drops exactly the overflow and counts it
///     (docs/plans/2026-08-15-performance-metrics-implementation.md, WP3 AC3, drop-count gate).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MeasurementBufferTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

    private static Measurement AMeasurement() =>
        new("test.metric", MeasurementKind.Counter, 1, "count", FixedNow);

    [Fact]
    public void TryEnqueue_BurstWithinCapacity_AllSucceedAndDrainWhole()
    {
        var buffer = new MeasurementBuffer(1000);

        for (var i = 0; i < 600; i++)
        {
            buffer.TryEnqueue(AMeasurement()).ShouldBeTrue();
        }

        buffer.DroppedCount.ShouldBe(0);
        buffer.DrainAll().Count.ShouldBe(600);
    }

    [Fact]
    public void TryEnqueue_ExactlyAtCapacity_AllSucceedNoDrop()
    {
        var buffer = new MeasurementBuffer(1000);

        for (var i = 0; i < 1000; i++)
        {
            buffer.TryEnqueue(AMeasurement()).ShouldBeTrue();
        }

        buffer.DroppedCount.ShouldBe(0);
        buffer.DrainAll().Count.ShouldBe(1000);
    }

    [Fact]
    public void TryEnqueue_BeyondCapacity_DropsExactlyTheOverflowAndReportsIt()
    {
        var buffer = new MeasurementBuffer(1000);
        var accepted = 0;

        for (var i = 0; i < 1200; i++)
        {
            if (buffer.TryEnqueue(AMeasurement()))
            {
                accepted++;
            }
        }

        accepted.ShouldBe(1000);
        buffer.DroppedCount.ShouldBe(200);
        buffer.DrainAll().Count.ShouldBe(1000);
    }

    [Fact]
    public void DrainAll_ResetsOccupancy_SoTheBufferAcceptsAgain()
    {
        var buffer = new MeasurementBuffer(2);
        buffer.TryEnqueue(AMeasurement());
        buffer.TryEnqueue(AMeasurement());
        buffer.TryEnqueue(AMeasurement()).ShouldBeFalse(); // dropped: at capacity

        buffer.DrainAll().Count.ShouldBe(2);

        buffer.TryEnqueue(AMeasurement()).ShouldBeTrue("drained buffer has room again");
    }

    [Fact]
    public void EnqueuedCount_TracksSuccessfulEnqueues_UnaffectedByDraining()
    {
        var buffer = new MeasurementBuffer(1000);
        buffer.TryEnqueue(AMeasurement());
        buffer.TryEnqueue(AMeasurement());

        buffer.DrainAll();

        buffer.EnqueuedCount.ShouldBe(2, "the no-recursion gate reads this across a flush; draining must not move it");
    }

    [Fact]
    public void ApplyCapacity_ChangesTheCapAppliedToFutureEnqueues()
    {
        var buffer = new MeasurementBuffer(1);
        buffer.TryEnqueue(AMeasurement()).ShouldBeTrue();
        buffer.TryEnqueue(AMeasurement()).ShouldBeFalse();

        buffer.DrainAll();
        buffer.ApplyCapacity(2);

        buffer.TryEnqueue(AMeasurement()).ShouldBeTrue();
        buffer.TryEnqueue(AMeasurement()).ShouldBeTrue();
        buffer.TryEnqueue(AMeasurement()).ShouldBeFalse();
    }
}
