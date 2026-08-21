using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     One file's ingest outcome: rows written, and whether the watch digest may record a
///     fingerprint for it. <see cref="FingerprintEligible" /> is false only when the file's ONLY
///     matched corpus is code, the code chunker produced zero chunks (B1), AND the content is NOT
///     empty/whitespace-only (S3) — a stand-in chunker (e.g. <c>NoOpCodeChunker</c>) must never let
///     a file with real content settle into a fingerprinted, permanently hash-skipped state before
///     a real chunker ever ran on it, but a genuinely empty/whitespace-only file chunks to zero
///     rows FOREVER regardless of chunker quality, so it fingerprints like any other outcome —
///     a memory match (regardless of row count), a mixed match, or no corpus matching the file at
///     all — which all fingerprint exactly as before.
/// </summary>
public readonly record struct FileIngestResult(int RowsInserted, bool FingerprintEligible);

public interface IFileIngestor
{
    /// <summary>
    ///     Set <paramref name="embedInline" /> false when the caller holds a write transaction: embedding
    ///     runs the engine per chunk, and a lock held that long stalls another process's first bank open.
    /// </summary>
    Task<FileIngestResult> IngestFileAsync(SqliteConnection connection, string projectId, string path,
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
