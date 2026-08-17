using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     The relay half of a CLI-requested promotion-queue prune (ADR-0075 amendment, mirrors
///     <see cref="ReingestRepairJob" />/<see cref="ChunkIndexRepairJob" />): <c>extract prune --apply</c>
///     commits a promotion_queue_prune_requests row through the server instead of deleting the
///     orphaned rows itself; this job applies it. On-demand only — <see cref="HasWorkAsync" /> reads
///     the request row, never a clock, so this never runs unless a human explicitly asked via
///     <c>--apply</c>.
/// </summary>
public sealed class PromotionQueuePruneJob(TimeProvider timeProvider) : IMaintenanceJob
{
    public const string JobName = "promotion-queue-prune";

    public string Name => JobName;

    public string DisplayName => "apply a CLI-requested promotion-queue prune";

    /// <summary>Never due by the clock; <see cref="HasWorkAsync" /> is the only gate.</summary>
    public TimeSpan? Interval => null;

    public async Task<bool> HasWorkAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                MemorySql.HasOpenPromotionQueuePruneRequest, cancellationToken: cancellationToken))
            .ConfigureAwait(false) > 0;

    /// <summary>Deletes the orphaned rows, then marks the request finished. Pure DELETE — never leaves anything pending for embedding.</summary>
    public async Task<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(PromotionQueueSql.DeleteOrphans,
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(MemorySql.FinishPromotionQueuePruneRequest,
                new { finishedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return false;
    }
}
