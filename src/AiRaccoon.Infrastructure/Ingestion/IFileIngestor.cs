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

    /// <summary>
    ///     Splits free-form content to the budget of the engine that will embed it, using the same
    ///     chunker and the same budget resolution as file ingest (docs/adr/0064). `memory_write` had
    ///     no chunking of its own, so a long body stored whole and embedded only its first window.
    ///     Returns one chunk for content that already fits.
    /// </summary>
    Task<IReadOnlyList<string>> ChunkToBudgetAsync(SqliteConnection connection, string content,
        CancellationToken cancellationToken);
}
