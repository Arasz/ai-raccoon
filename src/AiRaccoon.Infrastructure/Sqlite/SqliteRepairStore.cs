using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using Dapper;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     The server side of <see cref="IRepairStore" /> (ADR-0075 amendment): a report opens its own
///     bank connection and scans read-only, exactly like <see cref="SqliteSettingsStore" /> opens one
///     per call; a request writes the repair_requests outbox row.
/// </summary>
public sealed class SqliteRepairStore(
    ISqliteConnectionFactory factory, IFileTypeMatcher fileTypeMatcher, ILocalTokenizer localTokenizer,
    IMemoryStore store, TimeProvider timeProvider) : IRepairStore
{
    public async Task<ReingestRepairReport> ReportReingestAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await new ReingestRepair(new ChunkPositionScanner(fileTypeMatcher, localTokenizer))
            .RunAsync(connection, store, apply: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ChunkIndexRepairReport> ReportChunkIndexAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await new ChunkIndexRepair(fileTypeMatcher, localTokenizer)
            .RunAsync(connection, apply: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task RequestRepairAsync(RepairKind kind, CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.RequestRepair,
                new { kind = kind.ToKey(), requestedAt = timeProvider.GetUtcNow().ToUnixTimeSeconds() },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
