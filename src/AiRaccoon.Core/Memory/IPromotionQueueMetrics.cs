namespace AiRaccoon.Core.Memory;

/// <summary>
///     Observability port for the propose tier. Implemented in the server project
///     (Observability/) — Core and Infrastructure cannot reference it, so the port keeps
///     the queue service testable with a recording fake.
/// </summary>
public interface IPromotionQueueMetrics
{
    /// <summary>One eviction; reason is the rule that fired (capacity).</summary>
    void RecordEviction(string projectId, double victimScore, string reason);

    /// <summary>A row left the queue into the shared tier, after waiting waitSeconds.</summary>
    void RecordPromoted(string projectId, double waitSeconds);

    /// <summary>A row left the queue discarded by the agent, after waiting waitSeconds.</summary>
    void RecordDiscarded(string projectId, double waitSeconds);

    /// <summary>Publishes the queue's current persisted state — per-project depth and occupancy
    /// against capacity — for the observable depth and utilization instruments to read.</summary>
    void RecordSnapshot(PromotionQueueStats stats, int capacity);
}
