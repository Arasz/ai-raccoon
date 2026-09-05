using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using Dapper;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     The server side of <see cref="IRepairStore" /> (ADR-0075 amendment): a report opens its own
///     bank connection and scans read-only, exactly like <see cref="SqliteSettingsStore" /> opens one
///     per call; a request writes the repair_requests outbox row. The three report methods open
///     through <see cref="ISqliteConnectionFactory.OpenBankSkippingEnsureAsync" /> +
///     <see cref="MemorySchema.EnsureCheapAsync" /> rather than <see cref="ISqliteConnectionFactory.OpenBankAsync" />
///     (the precedent is <see cref="SqliteProjectIdsMigrationGate" />): a report must never wait on
///     another connection's write lock, but <c>OpenBankAsync</c>'s full ladder runs an unconditional
///     write (<c>MigrateIngestScopeKeysAsync</c>) whenever a legacy bank still carries
///     <c>watch.scope.*</c> rows. A legacy bank's ingest-scope/watch migration is therefore no
///     longer applied by a report call specifically — every other bank-opening path still applies
///     it.
/// </summary>
public sealed class SqliteRepairStore(
    ISqliteConnectionFactory factory,
    IFileTypeMatcher fileTypeMatcher,
    IEmbeddingService embeddingService,
    IMemoryStore store,
    TimeProvider timeProvider) : IRepairStore
{
    public async Task<ReingestRepairReport> ReportReingestAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankSkippingEnsureAsync(cancellationToken).ConfigureAwait(false);
        await MemorySchema.EnsureCheapAsync(connection, cancellationToken).ConfigureAwait(false);
        return await new ReingestRepair(new ChunkPositionScanner(fileTypeMatcher, embeddingService))
            .RunAsync(connection, store, false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChunkIndexRepairReport> ReportChunkIndexAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankSkippingEnsureAsync(cancellationToken).ConfigureAwait(false);
        await MemorySchema.EnsureCheapAsync(connection, cancellationToken).ConfigureAwait(false);
        return await new ChunkIndexRepair(fileTypeMatcher, embeddingService)
            .RunAsync(connection, false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The project-ids diagnose half: the P1 census, SELECT-only (proven by
    ///     <c>Collect_RunsUnderQueryOnly_ProvingZeroBankWrites</c>), listing clusters, orphans and
    ///     zero-entry rows per id. Doctor stays read-only and never serves this.
    /// </summary>
    public async Task<ProjectIdCensusReport> ReportProjectIdsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankSkippingEnsureAsync(cancellationToken).ConfigureAwait(false);
        await MemorySchema.EnsureCheapAsync(connection, cancellationToken).ConfigureAwait(false);
        return await ProjectIdCensus.CollectAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task RequestRepairAsync(RepairKind kind, CancellationToken cancellationToken = default, string? projectIdsMapJson = null)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.RequestRepair,
                new { kind = kind.ToKey(), requestedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds(), mapJson = projectIdsMapJson },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
