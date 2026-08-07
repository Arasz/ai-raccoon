using AiRaccoon.Core;
using AiRaccoon.Core.Memory;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Promotion;

/// <summary>
///     The propose tier's orchestrator: persists candidates (propose), shares from the queue
///     (promote — never a fresh re-extraction), evicts while the queue is over capacity, and
///     reports what is waiting. The eviction rule is the injected IEvictionPolicy; the store
///     executes the victim-row query; a crash between upsert and eviction may leave the queue
///     one over cap — the next propose loop self-heals (documented in the plan).
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

        var upserted = await queue.UpsertAsync(projectId, candidates, cancellationToken).ConfigureAwait(false);
        metrics.RecordQueued(projectId, upserted);

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
            metrics.RecordQueued(target, -1);
            Log.Evicted(logger, target, victim.Hash, victim.Score, EvictionReason);
            stats = await queue.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        }

        metrics.RecordUtilization(stats.TotalCount / (double)cap);
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
        var promoted = new List<string>();
        var skipped = 0;
        foreach (var projectId in projectIds)
        {
            var rows = (await queue.ListAsync(projectId, cancellationToken).ConfigureAwait(false))
                .Take(limit).ToList();
            var drained = 0;
            foreach (var row in rows)
            {
                // Claim before sharing: a concurrent discard may have removed this row since the
                // ListAsync snapshot above. An empty result means this call lost the race.
                var claimed = await queue.DiscardAsync(projectId, row.Hash, cancellationToken).ConfigureAwait(false);
                if (claimed.Count == 0)
                {
                    continue;
                }

                drained++;
                if (IsDuplicate(row, sharedIndex))
                {
                    skipped++;
                }
                else
                {
                    await store.ShareAsync(projectId, row.Hash, cancellationToken).ConfigureAwait(false);
                    promoted.Add(row.Hash);
                    var wait = Math.Max(0, timeProvider.GetUtcNow().ToUnixTimeSeconds() - row.CreatedAt);
                    metrics.RecordPromoted(projectId, wait);
                }
            }

            metrics.RecordQueued(projectId, -drained);
        }

        var remaining = await queue.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        metrics.RecordUtilization(remaining.TotalCount / (double)Math.Max(1, await ReadCapAsync(cancellationToken).ConfigureAwait(false)));
        Log.Promoted(logger, string.Join(",", projectIds), promoted.Count, skipped);
        return new PromoteOutcome(promoted, skipped, remaining.PerProject);
    }

    public async Task<int> DiscardAsync(string projectId, string? hash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        var removed = await queue.DiscardAsync(projectId, hash, cancellationToken).ConfigureAwait(false);
        if (removed.Count > 0)
        {
            metrics.RecordQueued(projectId, -removed.Count);
            foreach (var row in removed)
            {
                var wait = Math.Max(0, timeProvider.GetUtcNow().ToUnixTimeSeconds() - row.CreatedAt);
                metrics.RecordDiscarded(projectId, wait);
            }

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

    public async Task<ResponseMeta> GetMetaAsync(CancellationToken cancellationToken = default)
    {
        var stats = await queue.GetStatsAsync(cancellationToken).ConfigureAwait(false);
        return new ResponseMeta(stats.TotalCount, stats.AvgWaitSeconds,
            stats.PerProject.Count > 0 ? stats.PerProject : null);
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

    private static bool IsDuplicate(PromotionQueueRow row, SharedIndex sharedIndex)
    {
        var value = string.Concat(row.Value.Where(c => !char.IsWhiteSpace(c)));
        var sharedValues = sharedIndex.Values
            .Select(v => string.Concat(v.Where(c => !char.IsWhiteSpace(c))));
        return sharedValues.Contains(value) || sharedIndex.Paths.Contains($"shared/{row.Path}");
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
            Message = "Promoted from the queue for {ProjectIds}: {Promoted} shared, {Skipped} duplicate-skipped")]
        public static partial void Promoted(ILogger logger, string projectIds, int promoted, int skipped);

        [LoggerMessage(EventId = 703, Level = LogLevel.Information,
            Message = "Discarded {Count} queued row(s) for {ProjectId}")]
        public static partial void Discarded(ILogger logger, string projectId, int count);

        [LoggerMessage(EventId = 704, Level = LogLevel.Warning,
            Message = "Queue-capacity read failed; falling back to the default")]
        public static partial void CapReadFailed(ILogger logger, Exception exception);
    }
}
