using AiRaccoon.Core;
using AiRaccoon.Core.Memory;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Promotion;

/// <summary>
///     The propose tier's orchestrator: persists candidates, shares from the queue (never a
///     fresh re-extraction), and evicts while over capacity via the injected IEvictionPolicy
///     (ADR-0007). A crash between upsert and eviction self-heals on the next propose loop.
/// </summary>
public sealed partial class PromotionQueueService(
    IPromotionQueueStore queue,
    IMemoryStore store,
    IEvictionPolicy eviction,
    IPromotionQueueMetrics metrics,
    ILogger<PromotionQueueService> logger,
    TimeProvider timeProvider) : IPromotionQueue
{
    public async Task<ProposeOutcome> ProposeAsync(string projectId, IReadOnlyList<QueueCandidate> candidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNull(candidates);

        // Residue sweep first (docs/adr/0026): rows that became shared or were rejected by an
        // earlier discard leave the queue before this pass re-ranks and re-inserts anything.
        var pruned = await queue.PruneRejectedAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (pruned > 0)
        {
            Log.Pruned(logger, projectId, pruned);
        }

        var upserted = await queue.UpsertAsync(projectId, candidates, cancellationToken).ConfigureAwait(false);

        var cap = await ReadCapAsync(cancellationToken).ConfigureAwait(false);
        var evicted = new List<EvictedRow>();
        var stats = await queue.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        while (PromotionCapacityPolicy.NeedsEviction(stats.TotalCount, cap))
        {
            var target = eviction.EvictionTarget(stats.PerProject);
            if (target is null)
            {
                break;
            }

            var victim = await queue.EvictVictimAsync(target, cancellationToken).ConfigureAwait(false);
            if (victim is null)
            {
                break;
            }

            evicted.Add(new EvictedRow(target, victim.Hash, victim.Score, EvictionReason));
            metrics.RecordEviction(target, victim.Score, EvictionReason);
            Log.Evicted(logger, target, victim.Hash, victim.Score, EvictionReason);
            stats = await queue.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        }

        metrics.RecordSnapshot(stats, cap);
        Log.Proposed(logger, projectId, upserted, evicted.Count);
        return new ProposeOutcome(upserted, evicted);
    }

    public async Task<PromoteOutcome> PromoteAsync(IReadOnlyList<string> projectIds, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectIds);
        ArgumentOutOfRangeException.ThrowIfZero(projectIds.Count);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        var sharedIndex = await store.GetSharedIndexAsync(cancellationToken).ConfigureAwait(false);
        // Mutable in-batch copies of the shared index in EXACTLY the formats the classifier uses
        // (whitespace-stripped values, full "shared/<sha256(value)>.md" strings), refreshed after
        // every created share so later rows in this batch classify against what THIS call just wrote.
        var sharedValues = new HashSet<string>(
            sharedIndex.Values.Select(v => string.Concat(v.Where(c => !char.IsWhiteSpace(c)))),
            StringComparer.Ordinal);
        var sharedPaths = new HashSet<string>(sharedIndex.Paths, StringComparer.Ordinal);
        var promoted = new List<string>();
        var failures = new List<PromoteFailure>();
        var skipped = 0;
        var absorbed = 0;
        foreach (var projectId in projectIds)
        {
            // Residue sweep (docs/adr/0026): a row rejected by an earlier discard must not be
            // promotable — the mode flip to promote happens after propose-only queues grew.
            var pruned = await queue.PruneRejectedAsync(projectId, cancellationToken).ConfigureAwait(false);
            if (pruned > 0)
            {
                Log.Pruned(logger, projectId, pruned);
            }

            var rows = (await queue.ListAsync(projectId, cancellationToken).ConfigureAwait(false))
                .Take(limit).ToList();
            foreach (var row in rows)
            {
                try
                {
                    // Claim before sharing: a concurrent discard may have removed this row since the
                    // ListAsync snapshot above. An empty result means this call lost the race.
                    var claimed = await queue.DiscardAsync(projectId, row.Hash, cancellationToken)
                        .ConfigureAwait(false);
                    if (claimed.Count == 0)
                    {
                        continue;
                    }

                    var valueKey = string.Concat(row.Value.Where(c => !char.IsWhiteSpace(c)));
                    var sharedPath = $"shared/{ContentHash.OfValue(row.Value)}.md";
                    if (sharedValues.Contains(valueKey))
                    {
                        // Value twin — checked FIRST: one copy of a value in the shared tier,
                        // even when a same-batch row carries it under another path.
                        skipped++;
                    }
                    else if (sharedPaths.Contains(sharedPath))
                    {
                        // Identical chunk value already shared under its value-addressed path
                        // (idempotent re-share; the exact-value twin of a whitespace-variant row).
                        absorbed++;
                    }
                    else
                    {
                        var shared = await store.ShareAsync(projectId, row.Hash, cancellationToken)
                            .ConfigureAwait(false);
                        if (shared.Created)
                        {
                            promoted.Add(row.Hash);
                            sharedValues.Add(valueKey);
                            sharedPaths.Add(sharedPath);
                            var wait = Math.Max(0, timeProvider.GetUtcNow().ToUnixTimeSeconds() - row.CreatedAt);
                            metrics.RecordPromoted(projectId, wait);
                        }
                        else
                        {
                            // Lost the insert race to a concurrent caller (affected == 0):
                            // the row exists, so this claim is absorbed, not a promotion.
                            absorbed++;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The row is already claimed off the queue by this point; re-queuing it would
                    // reopen the discard-before-share race and reset CreatedAt. Drop it and report
                    // why instead — the rest of this batch, and the trailing snapshot, still run.
                    if (ex is UnknownHashException)
                    {
                        failures.Add(new PromoteFailure(projectId, row.Hash, "stale-hash"));
                        Log.StaleHash(logger, projectId, row.Hash, ex);
                    }
                    else
                    {
                        failures.Add(new PromoteFailure(projectId, row.Hash, "share-failed"));
                        Log.ShareFailed(logger, projectId, row.Hash, ex);
                    }
                }
            }
        }

        var remaining = await queue.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        metrics.RecordSnapshot(remaining, await ReadCapAsync(cancellationToken).ConfigureAwait(false));
        Log.Promoted(logger, string.Join(",", projectIds), promoted.Count, absorbed, skipped);
        return new PromoteOutcome(promoted, skipped, remaining.PerProject, absorbed) { Failures = failures };
    }

    public async Task<int> DiscardAsync(string projectId, string? hash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var removed = await queue.DiscardAsync(projectId, hash, cancellationToken).ConfigureAwait(false);
        if (removed.Count > 0)
        {
            foreach (var row in removed)
            {
                var wait = Math.Max(0, timeProvider.GetUtcNow().ToUnixTimeSeconds() - row.CreatedAt);
                metrics.RecordDiscarded(projectId, wait);
            }

            // The agent's "no" is permanent: propose must never re-queue these hashes
            // (docs/adr/0026). The claim path of PromoteAsync shares this store method and
            // never calls RememberDiscardsAsync — promotions are not rejections.
            await queue.RememberDiscardsAsync(projectId, removed.Select(r => r.Hash).ToList(),
                    cancellationToken)
                .ConfigureAwait(false);

            var stats = await queue.GetStatsAsync(cancellationToken).ConfigureAwait(false);
            metrics.RecordSnapshot(stats, await ReadCapAsync(cancellationToken).ConfigureAwait(false));
            Log.Discarded(logger, projectId, removed.Count);
        }

        return removed.Count;
    }

    public async Task<IReadOnlyList<PromotionQueueRow>> ListAsync(string? projectId, int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        return (await queue.ListAsync(projectId, cancellationToken).ConfigureAwait(false))
            .Take(limit).ToList();
    }

    public async Task<PromotionMeta> GetMetaAsync(string? projectId,
        CancellationToken cancellationToken = default)
    {
        var stats = await queue.GetWaitStatsAsync(projectId, cancellationToken).ConfigureAwait(false);
        PromotionCapacityInfo? capacity = null;
        if (projectId is not null && stats.WaitingCount > 0)
        {
            // OccupyingProjects, not the full roster: that's what UniformCountEvictionPolicy competes over.
            var cap = await ReadCapAsync(cancellationToken).ConfigureAwait(false);
            capacity = PromotionCapacityPolicy.CapacityFor(cap, stats.OccupyingProjects, stats.WaitingCount);
        }

        return new PromotionMeta(stats.WaitingCount, stats.AvgWaitSeconds)
        {
            Capacity = capacity,
            OldestWaitSeconds = stats.OldestWaitSeconds
        };
    }

    public async Task<int> ClearStaleAsync(string projectId, int currentScorerVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var cleared = await queue.ClearStaleAsync(projectId, currentScorerVersion, cancellationToken)
            .ConfigureAwait(false);
        if (cleared > 0)
        {
            Log.StaleCleared(logger, projectId, cleared);
        }

        return cleared;
    }

    private const string EvictionReason = "capacity";

    private async Task<int> ReadCapAsync(CancellationToken cancellationToken)
    {
        try
        {
            return ExtractionConfigKeys.ParseQueueCapacity(
                await store.GetSettingAsync(ExtractionConfigKeys.QueueCapacityGlobal, cancellationToken)
                    .ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            Log.CapReadFailed(logger, ex);
            return ExtractionConfigKeys.DefaultQueueCapacity;
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 700, Level = LogLevel.Debug,
            Message = "Propose for {ProjectId}: {Upserted} upserted, {Evicted} evicted")]
        public static partial void Proposed(ILogger logger, string projectId, int upserted, int evicted);

        [LoggerMessage(EventId = 701, Level = LogLevel.Warning,
            Message = "Propose-tier eviction from {ProjectId}: {VictimHash} (score {Score}, {Reason})")]
        public static partial void Evicted(ILogger logger, string projectId, string victimHash, double score,
            string reason);

        [LoggerMessage(EventId = 702, Level = LogLevel.Information,
            Message = "Promoted from the queue for {ProjectIds}: {Promoted} shared, {Absorbed} absorbed (already shared), {Skipped} duplicate-skipped")]
        public static partial void Promoted(ILogger logger, string projectIds, int promoted, int absorbed,
            int skipped);

        [LoggerMessage(EventId = 703, Level = LogLevel.Information,
            Message = "Discarded {Count} queued row(s) for {ProjectId}")]
        public static partial void Discarded(ILogger logger, string projectId, int count);

        [LoggerMessage(EventId = 704, Level = LogLevel.Warning,
            Message = "Queue-capacity read failed; falling back to the default")]
        public static partial void CapReadFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 705, Level = LogLevel.Debug,
            Message = "Promote candidate stale for {ProjectId}: {Hash} (claimed but no longer resolves)")]
        public static partial void StaleHash(ILogger logger, string projectId, string hash, Exception exception);

        [LoggerMessage(EventId = 706, Level = LogLevel.Warning,
            Message = "Promote candidate failed to share for {ProjectId}: {Hash}")]
        public static partial void ShareFailed(ILogger logger, string projectId, string hash, Exception exception);

        [LoggerMessage(EventId = 707, Level = LogLevel.Information,
            Message = "Cleared {Count} stale-scored queue row(s) for {ProjectId}")]
        public static partial void StaleCleared(ILogger logger, string projectId, int count);

        [LoggerMessage(EventId = 708, Level = LogLevel.Debug,
            Message = "Pruned {Count} rejected queue row(s) for {ProjectId} (already shared or discarded)")]
        public static partial void Pruned(ILogger logger, string projectId, int count);
    }
}
