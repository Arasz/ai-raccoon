namespace AiRaccoon.Core.Memory;

/// <summary>One-shot orphan report for `ai-raccoon extract prune` (ADR-0023): rows PruneOrphansAsync found (and, with apply, removed) per project.</summary>
public sealed record PromotionQueueOrphanReport(int TotalOrphans, IReadOnlyDictionary<string, int> PerProject);

/// <summary>
///     Reaches `extract prune` entirely through the server (ADR-0075 amendment), mirroring
///     <see cref="IRepairStore" />: a read-only, server-computed report
///     (<see cref="ReportPruneOrphansAsync" />, scanned server-side, never touching the bank from
///     the CLI process) and the write, which the CLI can only *request*
///     (<see cref="RequestPruneOrphansAsync" />, an outbox row) — the maintenance loop's on-demand
///     job applies it. Split out of <see cref="AiRaccoon.Infrastructure.Sqlite.IPromotionQueueStore" />
///     for the same reason <see cref="ISettingsStore" /> was: the CLI needs only this shape, not
///     the whole promotion-queue surface.
/// </summary>
public interface IPromotionQueuePruneStore
{
    Task<PromotionQueueOrphanReport> ReportPruneOrphansAsync(CancellationToken cancellationToken = default);

    Task RequestPruneOrphansAsync(CancellationToken cancellationToken = default);
}
