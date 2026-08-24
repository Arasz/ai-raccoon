using AiRaccoon.Core.Ingestion;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Ingestion;

/// <summary>
///     One file's ingest outcome. <see cref="FingerprintEligible" /> is false only when the file's
///     sole corpus is code, the chunker produced zero chunks and the content is not blank — a
///     stand-in chunker must never fingerprint real content into a permanently hash-skipped state.
/// </summary>
/// <param name="ChunkHashes">
///     Every memory-corpus chunk hash this ingest wrote OR rediscovered unchanged — the file's
///     current chunk set. A caller replacing by path deletes the complement: anything still stored
///     for the path that this ingest did not account for is a leftover of a previous chunking.
///     Empty when the path produced no memory chunks at all (ignored, hidden, or not a memory file).
/// </param>
/// <param name="CodeChunkHashes">
///     Every code-corpus chunk hash this ingest wrote OR rediscovered unchanged (#436), same
///     contract as <see cref="ChunkHashes" /> but for `code_entries`. <b>Null</b> — not an empty
///     list — when the code chunker produced zero chunks for content that is NOT empty/whitespace-only:
///     a stand-in chunker (e.g. `NoOpCodeChunker`) reporting zero chunks is not trustworthy
///     information about the file's real chunk set, so a caller pruning by this must treat null as
///     "leave code_entries alone" rather than "the current set is empty, delete everything".
/// </param>
public readonly record struct FileIngestResult(
    int RowsInserted,
    bool FingerprintEligible,
    IReadOnlyList<string>? ChunkHashes = null,
    IReadOnlyList<string>? CodeChunkHashes = null)
{
    /// <summary>Which corpus this ingest actually wrote new rows to — <see cref="CorpusKind.Neither" />
    /// when nothing was inserted (ignored/hidden/unrouted, or a rediscovered-unchanged file). One
    /// ingest call only ever touches one corpus — routing is by file type, never both at once.</summary>
    public CorpusKind WrittenCorpus => RowsInserted == 0 ? CorpusKind.Neither : CodeChunkHashes is not null ? CorpusKind.Code : CorpusKind.Memory;
}

/// <summary>
///     One walked file's current chunk set(s), so the caller can prune the rest.
///     <paramref name="CodeChunkHashes" /> follows <see cref="FileIngestResult.CodeChunkHashes" />'s
///     null-vs-empty contract (#485): null means the code chunker's zero chunks are not trustworthy
///     enough to prune by.
/// </summary>
public readonly record struct WalkedFile(
    string Path,
    IReadOnlyList<string> ChunkHashes,
    IReadOnlyList<string>? CodeChunkHashes);

/// <summary>A directory walk's outcome: files indexed, plus each walked file's current chunk set.</summary>
public readonly record struct DirectoryIngestResult(int Indexed, IReadOnlyList<WalkedFile> Files);

public interface IFileIngestor
{
    /// <summary>
    ///     Chunks and inserts <paramref name="path" />, leaving every row `embed_state = 'pending'` —
    ///     the caller enqueues the corpus's embed-drain signal once it is safe to (after any
    ///     wrapping transaction commits).
    /// </summary>
    Task<FileIngestResult> IngestFileAsync(SqliteConnection connection, string projectId, string path,
        string? context, CancellationToken cancellationToken);

    Task<DirectoryIngestResult> IngestDirectoryAsync(SqliteConnection connection, string projectId, string path,
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
