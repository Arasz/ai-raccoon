using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     The relay half of a CLI-requested chunk-index repair (ADR-0075 amendment, mirrors
///     <see cref="ModelMigrationJob" />/ADR-0076): <c>repair chunk-index --apply</c> commits a
///     repair_requests row through the server instead of writing the bank itself; this job applies
///     it. On-demand only — <see cref="HasWorkAsync" /> reads the request row, never a clock, so this
///     never runs unless a human explicitly asked via <c>--apply</c> (the guard `repair` verbs need
///     is against clock-scheduled unattended runs, not against ever appearing on the maintenance job
///     list — see <c>ChunkIndexRepairDoesNotAutoStartTests</c>).
/// </summary>
public sealed class ChunkIndexRepairJob(IFileTypeMatcher fileTypeMatcher, IEmbeddingService embeddingService, TimeProvider timeProvider)
    : IMaintenanceJob
{
    public const string JobName = "repair-chunk-index";

    public string Name => JobName;

    public string DisplayName => "apply a CLI-requested chunk-index repair";

    /// <summary>Never due by the clock; <see cref="HasWorkAsync" /> is the only gate.</summary>
    public TimeSpan? Interval => null;

    public async ValueTask<bool> HasWorkAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(MemorySql.HasOpenRepairRequest,
                new { kind = RepairKinds.ChunkIndex }, cancellationToken: cancellationToken))
            .ConfigureAwait(false) > 0;

    /// <summary>Re-scans and applies, then marks the request finished. Pure UPDATE — never leaves anything newly pending for embedding.</summary>
    public async ValueTask<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await new ChunkIndexRepair(fileTypeMatcher, embeddingService)
            .RunAsync(connection, true, cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(MemorySql.FinishRepairRequest,
                new { kind = RepairKinds.ChunkIndex, finishedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return false;
    }
}
