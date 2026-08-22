using System.Threading.Channels;

namespace AiRaccoon.Core.EventPump;

/// <summary>
///     Bounded-channel pump: one instance per topic (docs/work/2026-08-22-post-delta-3-plan.md
///     WP11-B1, owner ruling G17). The channel is built at its topic's fixed <see cref="PumpTopic.Ceiling" />
///     with <see cref="BoundedChannelFullMode.Wait" />; capacity is enforced separately by an
///     <see cref="Interlocked" /> reservation ahead of the channel — <see cref="TryWrite" /> is
///     never called past that reservation — so <see cref="ApplyCapacity" /> can move the effective
///     cap at runtime without rebuilding the channel. This is exactly
///     <c>MeasurementBuffer</c>'s pre-extraction contract (Finding (c)): full → drop, counted,
///     never block, never grow.
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
