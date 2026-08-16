using AiRaccoon.Core.Memory;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Embedding;

public interface IEntryEmbedder
{
    /// <summary>Writes the engine settings and, when the engine fingerprint changed, re-embeds the whole bank.</summary>
    Task<EmbeddingConfig> ConfigureAsync(SqliteConnection connection, string provider, string? model,
        string? baseUrl, CancellationToken cancellationToken);

    /// <summary>
    ///     The outbox half of a model migration (ADR-0076): in one transaction, writes the engine
    ///     settings, a migration-started record and marks the bank's embedded rows pending — then
    ///     returns without re-embedding. Throws <see cref="ModelMigrationInProgressException" /> when
    ///     a previous migration is still open. A no-op settings write (still applied) when the engine
    ///     fingerprint is unchanged.
    /// </summary>
    Task<EmbeddingConfig> StartMigrationAsync(SqliteConnection connection, string provider, string? model,
        string? baseUrl, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    ///     The relay half (ADR-0076): under the migration lease, drains every bank-wide pending row
    ///     (not project-scoped, unlike <see cref="EmbedPendingAsync" />) and marks the open migration
    ///     finished. False when no migration is open or another relay already holds the lease.
    /// </summary>
    Task<bool> DrainMigrationAsync(SqliteConnection connection, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Embeds one row when an engine is configured; a bank with no engine is left pending.</summary>
    Task EmbedIfConfiguredAsync(SqliteConnection connection, long id, string value,
        CancellationToken cancellationToken);

    /// <summary>Embeds a project's pending rows in batches with the configured engine.</summary>
    Task<int> EmbedPendingAsync(SqliteConnection connection, string projectId, int? limit,
        CancellationToken cancellationToken);

    /// <summary>Embeds a query string, or null when the bank has no engine — search degrades rather than failing.</summary>
    Task<byte[]?> EmbedQueryAsync(SqliteConnection connection, string query,
        CancellationToken cancellationToken);

    Task<EmbeddingSettings> ReadSettingsAsync(SqliteConnection connection,
        CancellationToken cancellationToken);
}
