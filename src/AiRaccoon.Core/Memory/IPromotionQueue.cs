namespace AiRaccoon.Core.Memory;

/// <summary>
///     The propose tier, as the tools and the extraction loop see it: persist candidates
///     (propose), promote from the queue (never a fresh re-extraction), review the queue,
///     discard what the agent rejects, and read what is waiting for the envelope meta.
///     Capacity/eviction is internal to the implementation.
/// </summary>
public interface IPromotionQueue
{
    /// <summary>Persists candidates for a project, evicting while the queue is over capacity. Returns what was upserted and evicted.</summary>
    Task<ProposeOutcome> ProposeAsync(string projectId, IReadOnlyList<QueueCandidate> candidates,
        CancellationToken cancellationToken = default);

    /// <summary>Shares the top-ranked queued rows of the given projects and drains them; already-shared rows are skipped and drained too.</summary>
    Task<PromoteOutcome> PromoteAsync(IReadOnlyList<string> projectIds, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Removes one queued row (hash given) or a whole project's queue (hash null); returns the removed count.</summary>
    Task<int> DiscardAsync(string projectId, string? hash,
        CancellationToken cancellationToken = default);

    /// <summary>Queued rows for review, score DESC then created_at ASC, capped at limit.</summary>
    Task<IReadOnlyList<PromotionQueueRow>> ListAsync(string? projectId, int limit,
        CancellationToken cancellationToken = default);

    /// <summary>What is waiting right now — count, average wait age, per-project breakdown (the envelope meta).</summary>
    Task<PromotionMeta> GetMetaAsync(CancellationToken cancellationToken = default);
}
