namespace AiRaccoon.Core.Memory;

/// <summary>
///     The propose tier's persistence port: waiting-for-promotion candidates, upserted by
///     (project, hash), listed for review, drained by discard, and evicted per project when over
///     capacity — IEvictionPolicy picks the victim project; this port runs the victim-row query.
/// </summary>
public interface IPromotionQueueStore
{
    /// <summary>Upsert by (project_id, hash), in one transaction: inserts keep the first created_at; re-propose refreshes score/value/reasons/updated_at. Returns the count of genuinely new rows — a re-propose of an existing hash does not count.</summary>
    Task<int> UpsertAsync(string projectId, IReadOnlyList<QueueCandidate> rows,
        CancellationToken cancellationToken = default);

    /// <summary>Queued rows, score DESC then created_at ASC (stable review order); all projects when projectId is null.</summary>
    Task<IReadOnlyList<PromotionQueueRow>> ListAsync(string? projectId,
        CancellationToken cancellationToken = default);

    /// <summary>Removes and returns one row (hash given) or the whole project's queue (hash null) — a DELETE...RETURNING claim, so a row is only ever removed by exactly one caller.</summary>
    Task<IReadOnlyList<PromotionQueueRow>> DiscardAsync(string projectId, string? hash,
        CancellationToken cancellationToken = default);

    /// <summary>Total count, per-project counts, and average wait age (now − created_at) over the queue.</summary>
    Task<PromotionQueueStats> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>Waiting count and wait ages for one project — the whole bank when projectId is null — plus the occupying-project count.</summary>
    Task<PromotionWaitStats> GetWaitStatsAsync(string? projectId,
        CancellationToken cancellationToken = default);

    /// <summary>The victim row of one project — lowest score, oldest created_at — removed; null when the project's queue is empty.</summary>
    Task<PromotionQueueRow?> EvictVictimAsync(string projectId,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes this project's queued rows whose scorer_version is not currentScorerVersion (ADR-0018). Returns the count removed.</summary>
    Task<int> ClearStaleAsync(string projectId, int currentScorerVersion,
        CancellationToken cancellationToken = default);
}
