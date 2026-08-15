using System.Linq;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     The proposal tier's persistence port: waiting-for-promotion candidates, upserted by
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

    /// <summary>
    ///     Persists the agent's "no" for (project_id, hash) so propose never re-queues it
    ///     (docs/adr/0026-persistent-discards-and-shared-exclusion.md). Idempotent; the tool-path
    ///     discard only — promotions, evictions and scorer clears never call this.
    /// </summary>
    Task RememberDiscardsAsync(string projectId, IReadOnlyList<string> hashes,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes queued rows that are already shared (exact value twin) or that a prior
    ///     discard rejected — the residue sweep of docs/adr/0026. Returns the count removed.
    /// </summary>
    Task<int> PruneRejectedAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Removes discards older than the retention window whose entry no longer exists. Both
    ///     conditions are required — see ADR-0055. Returns the count removed.
    /// </summary>
    Task<int> PurgeOldDiscardsAsync(long nowUnixSeconds, int retentionDays,
        CancellationToken cancellationToken = default);

    Task<PromotionQueueOrphanReport> PruneOrphansAsync(bool apply,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Claims one row for promotion by marking it claimed rather than deleting it (WP5b/A-F11):
    ///     a ShareAsync failure after this leaves the row reclaimable instead of destroyed. Null
    ///     when the row is already claimed, discarded, or never existed. Abstract on purpose: the
    ///     old default forwarded to the delete-based <see cref="DiscardAsync" />, so an implementor
    ///     that forgot to override destroyed the candidate instead of claiming it (ADR-0054).
    /// </summary>
    Task<PromotionQueueRow?> ClaimAsync(string projectId, string hash,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Releases claims older than <paramref name="staleAfter" /> back to the queue (WP5b/A-F11):
    ///     the caller that claimed a row and then died or hung mid-ShareAsync never gets to unclaim
    ///     it itself. Returns the count released. Default is a no-op (0) for stores with no claim
    ///     concept of their own.
    /// </summary>
    Task<int> ReclaimStaleClaimsAsync(TimeSpan staleAfter, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}
