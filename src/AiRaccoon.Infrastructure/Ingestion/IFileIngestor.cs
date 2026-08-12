using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

public interface IFileIngestor
{
    /// <summary>
    ///     Set <paramref name="embedInline" /> false when the caller holds a write transaction: embedding
    ///     runs the engine per chunk, and a lock held that long stalls another process's first bank open.
    /// </summary>
    Task<int> IngestFileAsync(SqliteConnection connection, string projectId, string path,
        string? context, CancellationToken cancellationToken, bool embedInline = true);

    Task<int> IngestDirectoryAsync(SqliteConnection connection, string projectId, string path,
        string? context, CancellationToken cancellationToken);
}
