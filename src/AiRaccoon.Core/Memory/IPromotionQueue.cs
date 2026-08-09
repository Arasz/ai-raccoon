namespace AiRaccoon.Core.Memory;

/// <summary>
///     The propose tier, as the tools and the extraction loop see it: persist candidates
///     (propose), promote from the queue (never a fresh re-extraction), review it, discard what
///     the agent rejects, and read what is waiting for the envelope meta; capacity/eviction stays internal.
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

    /// <summary>What is waiting for one project right now — the whole bank when projectId is null (the envelope meta).</summary>
    Task<PromotionMeta> GetMetaAsync(string? projectId, CancellationToken cancellationToken = default);

    /// <summary>Deletes this project's queued rows carrying a retired scorer_version (ADR-0018) —
    /// deletion, not re-scoring in place, since a stale row may no longer be a candidate at all; the
    /// normal propose path re-admits anything still eligible on merit. Returns the count removed.</summary>
    Task<int> ClearStaleAsync(string projectId, int currentScorerVersion,
        CancellationToken cancellationToken = default);
}
