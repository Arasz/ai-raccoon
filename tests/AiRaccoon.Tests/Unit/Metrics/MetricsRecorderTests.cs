using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Metrics;

/// <summary>
///     IMeasurementRecorder's own contract: Record must never throw and never touches the store —
///     it only enqueues (docs/plans/2026-08-15-performance-metrics-implementation.md, WP3 AC1, the
///     best-effort gate).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MetricsRecorderTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 15, 0, 0, 0, TimeSpan.Zero);

    private static Measurement AMeasurement() =>
        new("test.metric", MeasurementKind.Counter, 1, "count", FixedNow);

    [Fact]
    public void Record_ValidMeasurement_EnqueuesIntoTheBuffer()
    {
        var buffer = new MeasurementBuffer(10);
        var recorder = new MetricsRecorder(buffer, NullLogger<MetricsRecorder>.Instance);

        recorder.Record(AMeasurement());

        buffer.EnqueuedCount.ShouldBe(1);
    }

    [Fact]
    public void Record_BufferThrowsOnEveryCall_DoesNotThrow()
    {
        var poison = new PoisonBuffer();
        var recorder = new MetricsRecorder(poison, NullLogger<MetricsRecorder>.Instance);

        Should.NotThrow(() => recorder.Record(AMeasurement()));
        Should.NotThrow(() => recorder.Record(AMeasurement()));
    }

    /// <summary>Always throws on enqueue — simulates "a metrics writer that throws on every write" (spec scenario 18).</summary>
    private sealed class PoisonBuffer : IMeasurementBuffer
    {
        public int Capacity => 10;
        public long EnqueuedCount => 0;
        public long DroppedCount => 0;

        public bool TryEnqueue(Measurement measurement) => throw new InvalidOperationException("writer boom");

        public IReadOnlyList<Measurement> DrainAll() => [];

        public void ApplyCapacity(int capacity)
        {
        }
    }
}
