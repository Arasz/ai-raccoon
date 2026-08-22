namespace AiRaccoon.Core.EventPump;

/// <summary>
///     One bounded-channel topic (docs/work/2026-08-22-post-delta-3-plan.md WP11-B1, owner ruling
///     G17). <see cref="TryEnqueue" /> never blocks and never throws; a full or (on a coalescing
///     topic) duplicate item is dropped/coalesced and counted, never silently discarded.
/// </summary>
public interface IEventPump<T>
{
    /// <summary>Total items ever accepted. Untouched by draining.</summary>
    long EnqueuedCount { get; }

    /// <summary>Items dropped because the pump was at its effective capacity.</summary>
    long DroppedCount { get; }

    /// <summary>Items not queued because an identical item was already queued (coalescing topics only).</summary>
    long CoalescedCount { get; }

    /// <summary>False when at capacity (dropped) or already queued on a coalescing topic (coalesced). Never blocks, never throws.</summary>
    bool TryEnqueue(T item);

    /// <summary>Removes and returns up to <paramref name="budget" /> items, FIFO; fewer when the pump is empty. A count, never a duration.</summary>
    IReadOnlyList<T> DrainUpTo(int budget);

    /// <summary>Completes once an item is available to read.</summary>
    Task WaitForItemAsync(CancellationToken cancellationToken);

    /// <summary>Changes the cap applied to future enqueues; already-queued items are unaffected.</summary>
    void ApplyCapacity(int capacity);
}
