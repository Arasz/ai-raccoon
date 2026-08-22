using System.Threading.Channels;

namespace AiRaccoon.Core.EventPump;

/// <summary>
///     Bounded-channel pump: one instance per topic (ADR-0091). Built at
///     <see cref="PumpTopic.Ceiling" /> with <see cref="BoundedChannelFullMode.Wait" />; an
///     <see cref="Interlocked" /> reservation ahead of the channel enforces the (possibly lower)
///     effective capacity, so <see cref="ApplyCapacity" /> can move it at runtime without
///     rebuilding the channel — full drops and counts (mirrors <c>MeasurementBuffer</c>'s
///     contract), never blocks, never grows.
/// </summary>
public sealed class EventPump<T> : IEventPump<T>
{
    private readonly Channel<T> _channel;
    private readonly bool _coalesce;
    private readonly HashSet<T> _inFlight;
    private readonly Lock _coalesceGate = new();
    private int _capacity;
    private long _queued;
    private long _enqueuedTotal;
    private long _dropped;
    private long _coalesced;

    public EventPump(PumpTopic topic)
    {
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(topic.Ceiling)
        {
            FullMode = BoundedChannelFullMode.Wait
        });
        _capacity = topic.Capacity;
        _coalesce = topic.Coalesce;
        _inFlight = topic.Coalesce ? new HashSet<T>() : [];
    }

    public long EnqueuedCount => Interlocked.Read(ref _enqueuedTotal);

    public long DroppedCount => Interlocked.Read(ref _dropped);

    public long CoalescedCount => Interlocked.Read(ref _coalesced);

    public bool TryEnqueue(T item)
    {
        if (_coalesce && !TryClaimCoalesceKey(item))
        {
            Interlocked.Increment(ref _coalesced);
            return false;
        }

        // Reserve a slot before writing so concurrent callers cannot both pass a stale
        // count-check and overflow the cap — the reservation itself is the cap enforcement
        // (MeasurementBuffer.cs's precedent, kept byte-for-byte).
        var reserved = Interlocked.Increment(ref _queued);
        if (reserved > Volatile.Read(ref _capacity) || !_channel.Writer.TryWrite(item))
        {
            Interlocked.Decrement(ref _queued);
            ReleaseCoalesceKey(item);
            Interlocked.Increment(ref _dropped);
            return false;
        }

        Interlocked.Increment(ref _enqueuedTotal);
        return true;
    }

    public IReadOnlyList<T> DrainUpTo(int budget)
    {
        var batch = new List<T>(Math.Min(budget, 16));
        while (batch.Count < budget && _channel.Reader.TryRead(out var item))
        {
            Interlocked.Decrement(ref _queued);
            ReleaseCoalesceKey(item);
            batch.Add(item);
        }

        return batch;
    }

    public async Task WaitForItemAsync(CancellationToken cancellationToken) =>
        await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);

    public void ApplyCapacity(int capacity) => Volatile.Write(ref _capacity, capacity);

    private bool TryClaimCoalesceKey(T item)
    {
        lock (_coalesceGate)
        {
            return _inFlight.Add(item);
        }
    }

    private void ReleaseCoalesceKey(T item)
    {
        if (!_coalesce)
        {
            return;
        }

        lock (_coalesceGate)
        {
            _inFlight.Remove(item);
        }
    }
}
