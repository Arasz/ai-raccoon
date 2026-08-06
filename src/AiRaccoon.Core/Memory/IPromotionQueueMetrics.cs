namespace AiRaccoon.Core.Memory;

/// <summary>
///     Observability port for the propose tier. Implemented in the server project
///     (Observability/) — Core and Infrastructure cannot reference it, so the port keeps
///     the queue service testable with a recording fake.
/// </summary>
public interface IPromotionQueueMetrics
{
    /// <summary>Queue-size delta for a project: +upserts, −drained/promoted/discarded.</summary>
    void RecordQueued(string projectId, int delta);

    /// <summary>One eviction; reason is the rule that fired (capacity).</summary>
    void RecordEviction(string projectId, double victimScore, string reason);

    /// <summary>A row left the queue into the shared tier, after waiting waitSeconds.</summary>
    void RecordPromoted(string projectId, double waitSeconds);

    /// <summary>A row left the queue discarded by the agent, after waiting waitSeconds.</summary>
    void RecordDiscarded(string projectId, double waitSeconds);

    /// <summary>Queue occupancy against the cap, 0..1+ (over-cap right after a mutation is possible).</summary>
    void RecordUtilization(double ratio);
}
