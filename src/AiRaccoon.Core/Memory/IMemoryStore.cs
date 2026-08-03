namespace AiRaccoon.Core.Memory;

/// <summary>Port over the sqlite-memory SQL surface; thin and SQL-shaped, implemented by the Infrastructure layer.</summary>
public interface IMemoryStore
{
    Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default);

    Task<int> DeleteContextAsync(string projectId, string context, CancellationToken cancellationToken = default);

    Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Promotes the content behind hash into the flat shared context; the source project row may stay (FR-MEM-1.21).</summary>
    Task<MemoryEntry> ShareAsync(string projectId, string hash, CancellationToken cancellationToken = default);

    /// <summary>The bank's file tree as returned by memory_list_files (spec §4.1 memory_list).</summary>
    Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Indexes one file into the given context (spec §4.1 memory_ingest_file).</summary>
    Task<int> IngestFileAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default);

    /// <summary>Indexes a directory tree into the given context (spec §4.1 memory_ingest_directory).</summary>
    Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Sets the bank's embedding provider/model (and API key when remote); persists in dbmem_settings (spec §4.1
    ///     memory_configure).
    /// </summary>
    Task<EmbeddingConfig> ConfigureEmbeddingAsync(string projectId, string provider, string model, string? apiKey,
        CancellationToken cancellationToken = default);

    /// <summary>Embeds pending deferred rows in batches (spec §4.1 memory_embed_pending).</summary>
    Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Indexes caller-provided file content under an explicit logical path and context (memory_add_content;
    ///     consolidation, share).
    /// </summary>
    Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the entries stored under one context (workspace status, sweep enumeration).</summary>
    Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default);
}
