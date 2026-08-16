using AiRaccoon.Core.Memory;
using Dapper;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>IModelMigrationStore's half of SqliteMemoryStore (ADR-0076), split out to its own file for the same reason ISettingsStore's four methods left the main file (WP8's size ratchet).</summary>
public sealed partial class SqliteMemoryStore
{
    /// <inheritdoc />
    public async Task<bool> HasOpenModelMigrationAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                MemorySql.HasOpenModelMigration, cancellationToken: cancellationToken))
            .ConfigureAwait(false) > 0;
    }

    /// <inheritdoc />
    public async Task<EmbeddingConfig> StartModelMigrationAsync(
        string provider, string? model, string? baseUrl, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (string.Equals(provider, "openai", StringComparison.OrdinalIgnoreCase))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(model);
        }

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await _embedder.StartMigrationAsync(connection, provider, model, baseUrl, timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
    }
}
