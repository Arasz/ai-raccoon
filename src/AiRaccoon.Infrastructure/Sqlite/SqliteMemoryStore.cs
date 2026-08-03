using AiRaccoon.Core.Common;
using AiRaccoon.Core.Memory;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>IMemoryStore over the sqlite-memory SQL surface; requires provisioned native extensions.</summary>
public sealed class SqliteMemoryStore(SqliteConnectionFactory factory) : IMemoryStore
{
    public async Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = ContextResolver.Resolve(request);
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        // memory_add_text returns 1 (success), not the hash — read the row back by
        // (context, content). When the same content already exists in ANOTHER context the
        // global dedup skips the insert, so fall back to any row with this content (M5).
        await connection.ExecuteAsync(Def(MemorySql.InsertText, new { content = request.Content, context },
                cancellationToken))
            .ConfigureAwait(false);

        var entry = await FindEntryAsync(connection, context, request.Content, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"memory_add_text stored no row for context '{context}'.");
        return entry;
    }

    public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var batches = new List<IReadOnlyList<MemorySearchResult>>();
        foreach (var context in SearchContexts.For(query))
        {
            // Dapper materializes into a settable DTO: the memory_search virtual table declares
            // blob-affinity columns, and Dapper's record-ctor matching would demand byte[] params.
            var results = (await connection.QueryAsync<SearchRow>(
                        Def(MemorySql.SearchWithContext,
                            new { query = query.Query, minScore = query.MinScore, context, limit = query.Limit },
                            cancellationToken))
                    .ConfigureAwait(false))
                .Select(row => new MemorySearchResult(row.Hash, row.Seq, row.Ranking, row.Path, row.Snippet))
                .ToList();
            batches.Add(results);
        }

        return SearchResultMerger.Merge(batches, query.Limit);
    }

    public async Task<MemoryEntry> ShareAsync(string projectId, string hash,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var source = await connection.QueryFirstOrDefaultAsync<SourceRow>(
                Def(MemorySql.SelectSourceByHashAndContext,
                    new { hash, context = ContextNaming.ProjectContext(projectId) }, cancellationToken))
            .ConfigureAwait(false);
        if (source is null)
        {
            throw new InvalidOperationException(
                $"No entry with hash '{hash}' in context '{ContextNaming.ProjectContext(projectId)}'.");
        }

        // Promotion must create a REAL row in the shared context. sqlite-memory's content-hash
        // dedup is global, so a plain re-add is a silent no-op; with preserve_duplicate_paths=1
        // (set at bank open) a distinct logical path under shared/ yields its own path-scoped
        // hash and row (B1). AddContentAsync is idempotent: re-sharing finds the existing row.
        return await AddContentAsync(projectId, $"shared/{source.Path}", source.Value, ContextNaming.SharedContext,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var deleted = await connection.ExecuteScalarAsync<long>(Def(MemorySql.Delete, new { hash }, cancellationToken))
            .ConfigureAwait(false);
        return deleted == 1;
    }

    public async Task<int> DeleteContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        return await connection
            .ExecuteScalarAsync<int>(Def(MemorySql.DeleteContext, new { context }, cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        // Entry count is scoped to this project's committed context: workspace scratch and other
        // projects' rows are excluded (feature scenario "without workspace shows zero drafts").
        var entries = await connection.ExecuteScalarAsync<int>(
            Def(MemorySql.CountProjectEntries, new { project = ContextNaming.ProjectContext(projectId) },
                cancellationToken)).ConfigureAwait(false);
        var pendingCount = await connection
            .ExecuteScalarAsync<int>(Def(MemorySql.PendingCount, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        var contextList =
            (await connection.QueryAsync<string>(Def(MemorySql.CommittedContexts, cancellationToken: cancellationToken))
                .ConfigureAwait(false)).ToList();

        return new MemoryStats(entries, pendingCount, contextList);
    }

    public async Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        return await connection
                   .ExecuteScalarAsync<string>(Def(MemorySql.ListFiles, cancellationToken: cancellationToken))
                   .ConfigureAwait(false)
               ?? "{}";
    }

    public async Task<int> IngestFileAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        return await connection
            .ExecuteScalarAsync<int>(Def(MemorySql.IngestFile, new { path, context }, cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<int> IngestDirectoryAsync(string projectId, string path, string? context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        return await connection
            .ExecuteScalarAsync<int>(Def(MemorySql.IngestDirectory, new { path, context }, cancellationToken))
            .ConfigureAwait(false);
    }

    public async Task<EmbeddingConfig> ConfigureEmbeddingAsync(
        string projectId, string provider, string model, string? apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            await connection.ExecuteScalarAsync<long>(Def(MemorySql.SetApiKey, new { apiKey }, cancellationToken))
                .ConfigureAwait(false);
        }

        await connection.ExecuteScalarAsync<long>(Def(MemorySql.SetModel, new { provider, model }, cancellationToken))
            .ConfigureAwait(false);

        // A configured model means embeddings run immediately; deferral is off (FR-MEM-1.12).
        await connection
            .ExecuteScalarAsync<long>(Def(MemorySql.SetDeferEmbeddings, new { value = 0 }, cancellationToken))
            .ConfigureAwait(false);

        return new EmbeddingConfig(provider, model, provider == "local" ? "local" : "remote");
    }

    public async Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        // memory_embed_pending rejects a NULL/0 argument; use the 0-arg form when no limit
        // is given ("process all pending") (M2).
        var processed = limit is > 0
            ? await connection.ExecuteScalarAsync<int>(Def(MemorySql.EmbedPending, new { limit }, cancellationToken))
                .ConfigureAwait(false)
            : await connection
                .ExecuteScalarAsync<int>(Def(MemorySql.EmbedPendingAll, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        var pendingCount = await connection
            .ExecuteScalarAsync<int>(Def(MemorySql.PendingCount, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return new EmbedPendingResult(processed, pendingCount);
    }

    public async Task<MemoryEntry> AddContentAsync(
        string projectId, string path, string content, string? context, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var resolvedContext = context ?? ContextNaming.ProjectContext(projectId);
        var exists = await connection.ExecuteScalarAsync<long?>(
            Def("SELECT 1 FROM dbmem_content WHERE path = @path AND context = @context LIMIT 1",
                new { path, context = resolvedContext }, cancellationToken)).ConfigureAwait(false) is not null;
        if (!exists)
        {
            await connection.ExecuteAsync(
                    Def(MemorySql.InsertContent, new { path, content, context = resolvedContext }, cancellationToken))
                .ConfigureAwait(false);
        }

        return await connection.QueryFirstOrDefaultAsync<MemoryEntry>(
                   Def(
                       "SELECT hash AS Hash, path AS Path, context AS Context, value AS Value, created_at AS CreatedAt FROM dbmem_content WHERE path = @path AND context = @context LIMIT 1",
                       new { path, context = resolvedContext }, cancellationToken)).ConfigureAwait(false)
               ?? throw new InvalidOperationException(
                   $"memory_add_content stored no row for '{path}' in '{resolvedContext}'.");
    }

    public async Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        return
        [
            .. await connection.QueryAsync<MemoryEntry>(
                Def(MemorySql.SelectEntriesByContext, new { context }, cancellationToken)).ConfigureAwait(false)
        ];
    }

    private static async Task<MemoryEntry?> FindEntryAsync(
        SqliteConnection connection, string context, string content, CancellationToken cancellationToken) =>
        // Prefer the row in the requested context; fall back to any row with this content
        // (global dedup may have skipped the insert because another context owns it).
        await connection.QueryFirstOrDefaultAsync<MemoryEntry>(
            Def("""
                SELECT hash AS Hash, path AS Path, context AS Context, value AS Value, created_at AS CreatedAt
                FROM dbmem_content
                WHERE value = @content
                ORDER BY CASE WHEN context = @context THEN 0 ELSE 1 END, rowid DESC
                LIMIT 1
                """,
                new { content, context }, cancellationToken)).ConfigureAwait(false);

    private static CommandDefinition Def(string sql, object? parameters = null,
        CancellationToken cancellationToken = default) =>
        new(sql, parameters, cancellationToken: cancellationToken);

    /// <summary>Dapper materialization target for memory_search rows (blob-affinity columns defeat record ctor matching).</summary>
    private sealed class SearchRow
    {
        public string Hash { get; set; } = "";

        public int Seq { get; set; }

        public double Ranking { get; set; }

        public string Path { get; set; } = "";

        public string Snippet { get; set; } = "";
    }

    private sealed record SourceRow(string Path, string Value);
}
