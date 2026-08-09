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

    /// <summary>Promotes the content behind hash into the flat shared context; the source project row may stay (see docs/work/features-agent-memory/spec-issue-1.md, FR-MEM-1.21).</summary>
    Task<MemoryEntry> ShareAsync(string projectId, string hash, CancellationToken cancellationToken = default);

    /// <summary>Committed project-scoped rows eligible for shared-extraction (scope='project', embedded; TTL rows only when opted in).</summary>
    Task<IReadOnlyList<ExtractionCandidateRow>> ExtractCandidatesAsync(string projectId, bool includeTtlRows,
        CancellationToken cancellationToken = default);

    /// <summary>Existing shared tier contents — values and materialized paths — as the extraction dedup index.</summary>
    Task<SharedIndex> GetSharedIndexAsync(CancellationToken cancellationToken = default);

    /// <summary>Every project id with committed project-scoped rows (the extraction sweep's universe).</summary>
    Task<IReadOnlyList<string>> GetProjectIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>The bank's file tree as returned by memory_list_files (see docs/work/features-agent-memory/spec-issue-1.md §4.1 memory_list).</summary>
    Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default);

    /// <summary>Indexes one file into the given context (see docs/work/features-agent-memory/spec-issue-1.md §4.1 memory_ingest_file).</summary>
    Task<int> IngestFileAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default);

    /// <summary>Indexes a directory tree into the given context (see docs/work/features-agent-memory/spec-issue-1.md §4.1 memory_ingest_directory).</summary>
    Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Sets the bank's embedding provider/model/endpoint, persists them in the settings table,
    ///     records the engine fingerprint, and re-embeds previously embedded rows (bank-global) when
    ///     the engine changes; the remote API key is a separate settings row (embedding.apiKey).
    /// </summary>
    Task<EmbeddingConfig> ConfigureEmbeddingAsync(string provider, string? model, string? baseUrl,
        CancellationToken cancellationToken = default);

    /// <summary>Embeds pending deferred rows in batches (see docs/work/features-agent-memory/spec-issue-1.md §4.1 memory_embed_pending).</summary>
    Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Indexes caller-provided file content under an explicit logical path and context (memory_add_content;
    ///     consolidation, share).
    /// </summary>
    Task<MemoryEntry> AddContentAsync(string projectId, string path, string content, string? context,
        string? sourceFile = null, string? section = null, CancellationToken cancellationToken = default);

    /// <summary>Lists the entries stored under one context (workspace status, sweep enumeration).</summary>
    Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the on-row rating/ttl metadata for one entry (degradation policy input).</summary>
    Task<EntryMetadata?> GetMetadataAsync(string projectId, string hash,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one settings row (access modes, forgetting knobs); null when the key is absent.</summary>
    Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Upserts one settings row.</summary>
    Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>Deletes every committed chunk whose source path is the given path or lies under it (mirror delete/rename/directory cascade).</summary>
    Task<int> DeleteSourcePathAsync(string projectId, string path, CancellationToken cancellationToken = default);

    /// <summary>Reads every settings row whose key starts with the prefix (config listing commands).</summary>
    Task<IReadOnlyDictionary<string, string>> GetSettingsByPrefixAsync(string prefix,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes one settings row (unset/reset commands); absent keys are a no-op.</summary>
    Task DeleteSettingAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Sets an entry's ttl_days override — a forgetting knob gated to full mode at the boundary.</summary>
    Task SetEntryTtlAsync(string projectId, string hash, double ttlDays,
        CancellationToken cancellationToken = default);
}
