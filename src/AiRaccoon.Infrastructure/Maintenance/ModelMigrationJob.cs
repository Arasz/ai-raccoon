using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     The relay half of a model migration (ADR-0076): finishes whatever <c>model embedding set</c>'s outbox
///     transaction left owing. On-demand — <see cref="HasWorkAsync" /> answers from the
///     <c>model_migration</c> row itself, not a clock — so it is picked up by the maintenance loop's
///     startup pass (the crash-recovery guarantee) and every later poll, never by a cadence that
///     could leave it waiting.
/// </summary>
public sealed class ModelMigrationJob(IEntryEmbedder embedder) : IMaintenanceJob
{
    public const string JobName = "model-migration";

    public string Name => JobName;

    public string DisplayName => "finish an interrupted embedding-model migration";

    /// <summary>Never due by the clock; <see cref="HasWorkAsync" /> is the only gate.</summary>
    public TimeSpan? Interval => null;

    public async ValueTask<bool> HasWorkAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                MemorySql.HasOpenModelMigration, cancellationToken: cancellationToken))
            .ConfigureAwait(false) > 0;

    /// <summary>
    ///     Drains the open migration under the migration lease. False whenever there was nothing
    ///     left for this pass to do — no open migration, or another relay already holds the lease —
    ///     never a reason to fail the pass: an unfinished migration is retried on the next poll.
    /// </summary>
    public async ValueTask<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await embedder.DrainMigrationAsync(connection, cancellationToken).ConfigureAwait(false);

        // Draining re-embeds everything itself; nothing is left pending for the general
        // memory_embed_pending sweep to pick up, so this never asks the pass to sweep again.
        return false;
    }
}
