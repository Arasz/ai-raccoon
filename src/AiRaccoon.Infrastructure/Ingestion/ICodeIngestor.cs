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
    Task<CodeIngestResult> IngestFileAsync(SqliteConnection connection, string projectId, string path,
        CancellationToken cancellationToken, IReadOnlyList<string>? scope = null);
}

/// <summary>
///     <see cref="ContentWhitespaceOnly" /> lets the caller decide fingerprint eligibility (S3)
///     without re-reading the file the ingestor already read (review round 1, FileIngestor.cs:95).
/// </summary>
/// <param name="ChunkHashes">
///     Every code-corpus chunk hash this ingest wrote OR rediscovered unchanged — the file's
///     current chunk set (#436). Empty when the chunker produced zero chunks, whether because the
///     content is empty/whitespace-only or because a stand-in chunker cannot chunk yet; the caller
///     decides which of those two zero-chunk cases is trustworthy enough to prune by.
/// </param>
public readonly record struct CodeIngestResult(int Rows, bool ContentWhitespaceOnly,
    IReadOnlyList<string>? ChunkHashes = null)
{
}
