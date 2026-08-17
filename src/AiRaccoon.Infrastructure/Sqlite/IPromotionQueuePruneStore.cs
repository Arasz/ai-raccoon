namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Reaches `extract prune` entirely through the server (ADR-0075 amendment), mirroring
///     <see cref="AiRaccoon.Core.Memory.IRepairStore" />: a read-only, server-computed report
///     (<see cref="ReportPruneOrphansAsync" />, scanned server-side, never touching the bank from
///     the CLI process) and the write, which the CLI can only *request*
///     (<see cref="RequestPruneOrphansAsync" />, an outbox row) — the maintenance loop's on-demand
///     job applies it. Split out of <see cref="IPromotionQueueStore" /> for the same reason
///     <see cref="AiRaccoon.Core.Memory.ISettingsStore" /> was: the CLI needs only this shape, not
///     the whole promotion-queue surface.
/// </summary>
public interface IPromotionQueuePruneStore
{
    Task<PromotionQueueOrphanReport> ReportPruneOrphansAsync(CancellationToken cancellationToken = default);

    Task RequestPruneOrphansAsync(CancellationToken cancellationToken = default);
}
