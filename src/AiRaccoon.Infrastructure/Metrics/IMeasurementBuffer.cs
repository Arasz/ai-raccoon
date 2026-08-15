using AiRaccoon.Core.Metrics;

namespace AiRaccoon.Infrastructure.Metrics;

/// <summary>
///     The capped in-memory buffer <see cref="MetricsRecorder" /> enqueues into and
///     <see cref="MetricsFlusher" /> drains (docs/plans/2026-08-15-performance-metrics-implementation.md,
///     WP3). Beyond capacity an enqueue drops and counts the drop, rather than blocking or growing.
/// </summary>
public interface IMeasurementBuffer
{
    int Capacity { get; }

    /// <summary>Total measurements ever accepted. Untouched by draining — the no-recursion gate's seam.</summary>
    long EnqueuedCount { get; }

    long DroppedCount { get; }

    /// <summary>False when the buffer was at capacity; the measurement is dropped, not queued.</summary>
    bool TryEnqueue(Measurement measurement);

    /// <summary>Removes and returns everything currently queued.</summary>
    IReadOnlyList<Measurement> DrainAll();

    /// <summary>Changes the cap applied to future enqueues; already-queued items are unaffected.</summary>
    void ApplyCapacity(int capacity);
}
