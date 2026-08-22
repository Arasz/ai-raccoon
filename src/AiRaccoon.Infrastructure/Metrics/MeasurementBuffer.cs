using AiRaccoon.Core.EventPump;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;

namespace AiRaccoon.Infrastructure.Metrics;

/// <summary>
///     Thin adapter over the metrics topic's <see cref="EventPump{T}" /> (docs/work/2026-08-22-post-delta-3-plan.md
///     WP11-B1): every measurement is distinct data, so this topic never coalesces. The channel's
///     fixed ceiling is <see cref="MetricsConfigKeys.MaxBufferCapacity" />; the effective cap an
///     operator sets stays a runtime-mutable reservation ahead of it (<see cref="ApplyCapacity" />).
/// </summary>
/// <inheritdoc cref="IMeasurementBuffer" />
public sealed class MeasurementBuffer(int capacity) : IMeasurementBuffer
{
    private readonly IEventPump<Measurement> _pump =
        new EventPump<Measurement>(new PumpTopic(MetricsConfigKeys.MaxBufferCapacity, capacity, Coalesce: false));
    private int _capacity = capacity;

    public int Capacity => Volatile.Read(ref _capacity);

    public long EnqueuedCount => _pump.EnqueuedCount;

    public long DroppedCount => _pump.DroppedCount;

    public bool TryEnqueue(Measurement measurement) => _pump.TryEnqueue(measurement);

    public IReadOnlyList<Measurement> DrainAll() => _pump.DrainUpTo(int.MaxValue);

    public void ApplyCapacity(int capacity)
    {
        Volatile.Write(ref _capacity, capacity);
        _pump.ApplyCapacity(capacity);
    }
}
