using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>Ingests one file into the code corpus (`code_entries`), self-filtering by extension.</summary>
public interface ICodeIngestor
{
    /// <summary>
    ///     Pass <paramref name="scope" /> when the caller already resolved it once for the whole
    ///     walk (<c>FileIngestor.IngestDirectoryAsync</c>'s shape) — the per-file DB scope read is
    ///     skipped and the given list is trusted instead (S12).
    /// </summary>
    Task<int> IngestFileAsync(SqliteConnection connection, string projectId, string path,
        CancellationToken cancellationToken, IReadOnlyList<string>? scope = null);
}
