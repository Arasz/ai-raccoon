using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     The relay half of a CLI-requested reingest repair (ADR-0075 amendment, mirrors
///     <see cref="ModelMigrationJob" />/ADR-0076): <c>repair reingest --apply</c> commits a
///     repair_requests row through the server instead of writing the bank itself; this job applies
///     it. On-demand only — <see cref="HasWorkAsync" /> reads the request row, never a clock, so this
///     never runs unless a human explicitly asked via <c>--apply</c> (the guard `repair` verbs need
///     is against clock-scheduled unattended runs, not against ever appearing on the maintenance job
///     list — see <c>ReingestRepairDoesNotAutoStartTests</c>).
/// </summary>
public sealed class ReingestRepairJob(
    IFileTypeMatcher fileTypeMatcher,
    IEmbeddingService embeddingService,
    IMemoryStore store,
    TimeProvider timeProvider)
    : IMaintenanceJob
{
    public const string JobName = "repair-reingest";

    public string Name => JobName;

    public string DisplayName => "apply a CLI-requested reingest repair";

    /// <summary>Never due by the clock; <see cref="HasWorkAsync" /> is the only gate.</summary>
    public TimeSpan? Interval => null;

    public async ValueTask<bool> HasWorkAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(MemorySql.HasOpenRepairRequest,
                new { kind = RepairKinds.Reingest }, cancellationToken: cancellationToken))
            .ConfigureAwait(false) > 0;

    /// <summary>Re-scans and applies, then marks the request finished — leaves rows pending for PendingEmbedJob, ordered after this in the job list.</summary>
    public async ValueTask<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var report = await new ReingestRepair(new ChunkPositionScanner(fileTypeMatcher, embeddingService))
            .RunAsync(connection, store, true, cancellationToken).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(MemorySql.FinishRepairRequest,
                new { kind = RepairKinds.Reingest, finishedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return report.ChunksToEmbed > 0;
    }
}
