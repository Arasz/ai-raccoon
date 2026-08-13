using AiRaccoon.Core.Memory;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Embedding;

public interface IEntryEmbedder : IContentEmbedder
{
    /// <summary>Writes the engine settings and, when the engine fingerprint changed, re-embeds the whole bank.</summary>
    Task<EmbeddingConfig> ConfigureAsync(SqliteConnection connection, string provider, string? model,
        string? baseUrl, CancellationToken cancellationToken);

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
