using System.Collections.Concurrent;
using AiRaccoon.Core.Metrics;

namespace AiRaccoon.Infrastructure.Metrics;

/// <inheritdoc cref="IMeasurementBuffer" />
public sealed class MeasurementBuffer(int capacity) : IMeasurementBuffer
{
    private readonly ConcurrentQueue<Measurement> _queue = new();
    private int _capacity = capacity;
    private long _queued;
    private long _enqueuedTotal;
    private long _dropped;

    public int Capacity => Volatile.Read(ref _capacity);

    public long EnqueuedCount => Interlocked.Read(ref _enqueuedTotal);

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public bool TryEnqueue(Measurement measurement)
    {
        // Reserve a slot before enqueuing so concurrent callers cannot both pass a stale
        // count-check and overflow the cap — the reservation itself is the cap enforcement.
        var reserved = Interlocked.Increment(ref _queued);
        if (reserved > Capacity)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return false;
        }

        _queue.Enqueue(measurement);
        Interlocked.Increment(ref _enqueuedTotal);
        return true;
    }

    public IReadOnlyList<Measurement> DrainAll()
    {
        var batch = new List<Measurement>();
        while (_queue.TryDequeue(out var measurement))
        {
            Interlocked.Decrement(ref _queued);
            batch.Add(measurement);
        }

        return batch;
    }

    public void ApplyCapacity(int capacity) => Volatile.Write(ref _capacity, capacity);
}
