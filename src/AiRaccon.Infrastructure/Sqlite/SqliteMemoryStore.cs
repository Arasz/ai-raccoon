using System.Globalization;
using AiRaccon.Core.Common;
using AiRaccon.Core.Memory;
using Microsoft.Data.Sqlite;

namespace AiRaccon.Infrastructure.Sqlite;

/// <summary>IMemoryStore over the sqlite-memory SQL surface; requires provisioned native extensions.</summary>
public sealed class SqliteMemoryStore : IMemoryStore
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteMemoryStore(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = ContextResolver.Resolve(request);
        await using var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        await using var insert = connection.CreateCommand();
        insert.CommandText = MemorySql.InsertText;
        insert.Parameters.AddWithValue("@content", request.Content);
        insert.Parameters.AddWithValue("@context", context);
        await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        // memory_add_text returns 1 (success), not the hash — read the row back by
        // (context, content). When the same content already exists in ANOTHER context the
        // global dedup skips the insert, so fall back to any row with this content (M5).
        var entry = await FindEntryAsync(connection, context, request.Content, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"memory_add_text stored no row for context '{context}'.");
        return entry;
    }

    public async Task<IReadOnlyList<MemorySearchResult>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        var batches = new List<IReadOnlyList<MemorySearchResult>>();
        foreach (var context in SearchContexts.For(query))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = MemorySql.SearchWithContext;
            command.Parameters.AddWithValue("@query", query.Query);
            command.Parameters.AddWithValue("@minScore", query.MinScore);
            command.Parameters.AddWithValue("@context", context);
            command.Parameters.AddWithValue("@limit", query.Limit);

            var results = new List<MemorySearchResult>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                results.Add(new MemorySearchResult(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetDouble(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }

            batches.Add(results);
        }

        return SearchResultMerger.Merge(batches, query.Limit);
    }

    public async Task<MemoryEntry> ShareAsync(string projectId, string hash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(hash);

        await using var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        await using var select = connection.CreateCommand();
        select.CommandText = MemorySql.SelectSourceByHashAndContext;
        select.Parameters.AddWithValue("@hash", hash);
        select.Parameters.AddWithValue("@context", ContextNaming.ProjectContext(projectId));
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"No entry with hash '{hash}' in context '{ContextNaming.ProjectContext(projectId)}'.");
        }

        var path = reader.GetString(0);
        var value = reader.GetString(1);
        var createdAt = reader.GetInt64(2);
        await reader.DisposeAsync().ConfigureAwait(false);

        // Promotion must create a REAL row in the shared context. sqlite-memory's content-hash
        // dedup is global, so a plain re-add is a silent no-op; with preserve_duplicate_paths=1
        // (set at bank open) a distinct logical path under shared/ yields its own path-scoped
        // hash and row (B1). Idempotent: re-sharing the same hash finds the existing row.
        var sharedPath = $"shared/{path}";
        var exists = await EntryExistsAsync(connection, sharedPath, ContextNaming.SharedContext, cancellationToken).ConfigureAwait(false);
        if (!exists)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = MemorySql.InsertContent;
            insert.Parameters.AddWithValue("@path", sharedPath);
            insert.Parameters.AddWithValue("@content", value);
            insert.Parameters.AddWithValue("@context", ContextNaming.SharedContext);
            await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        var shared = await GetEntryAsync(connection, sharedPath, ContextNaming.SharedContext, cancellationToken).ConfigureAwait(false);
        return shared ?? new MemoryEntry(hash, sharedPath, ContextNaming.SharedContext, value, createdAt);
    }

    public async Task<bool> DeleteAsync(string projectId, string hash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(hash);

        await using var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = MemorySql.Delete;
        command.Parameters.AddWithValue("@hash", hash);
        var deleted = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return deleted == 1;
    }

    public async Task<int> DeleteContextAsync(string projectId, string context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(context);

        await using var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = MemorySql.DeleteContext;
        command.Parameters.AddWithValue("@context", context);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<MemoryStats> GetStatsAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        // Entry count is scoped to this project's committed context: workspace scratch and other
        // projects' rows are excluded (feature scenario "without workspace shows zero drafts").
        await using var count = connection.CreateCommand();
        count.CommandText = MemorySql.CountProjectEntries;
        count.Parameters.AddWithValue("@project", ContextNaming.ProjectContext(projectId));
        var entries = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

        await using var pending = connection.CreateCommand();
        pending.CommandText = MemorySql.PendingCount;
        var pendingCount = Convert.ToInt32(await pending.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

        await using var contexts = connection.CreateCommand();
        contexts.CommandText = MemorySql.CommittedContexts;
        var contextList = new List<string>();
        await using var reader = await contexts.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            contextList.Add(reader.GetString(0));
        }

        return new MemoryStats(entries, pendingCount, contextList);
    }

    public async Task<string> ListFilesAsync(string projectId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = MemorySql.ListFiles;
        return (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<int> IngestFileAsync(string projectId, string path, string? context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(path);

        await using var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = MemorySql.IngestFile;
        command.Parameters.AddWithValue("@path", path);
        command.Parameters.AddWithValue("@context", (object?)context ?? DBNull.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<int> IngestDirectoryAsync(string projectId, string path, string? context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(path);

        await using var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = MemorySql.IngestDirectory;
        command.Parameters.AddWithValue("@path", path);
        command.Parameters.AddWithValue("@context", (object?)context ?? DBNull.Value);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<EmbeddingConfig> ConfigureEmbeddingAsync(
        string projectId, string provider, string model, string? apiKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(model);

        await using var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            await using var keyCommand = connection.CreateCommand();
            keyCommand.CommandText = MemorySql.SetApiKey;
            keyCommand.Parameters.AddWithValue("@apiKey", apiKey);
            await keyCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var modelCommand = connection.CreateCommand();
        modelCommand.CommandText = MemorySql.SetModel;
        modelCommand.Parameters.AddWithValue("@provider", provider);
        modelCommand.Parameters.AddWithValue("@model", model);
        await modelCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        // A configured model means embeddings run immediately; deferral is off (FR-MEM-1.12).
        await using var deferCommand = connection.CreateCommand();
        deferCommand.CommandText = MemorySql.SetDeferEmbeddings;
        deferCommand.Parameters.AddWithValue("@value", 0);
        await deferCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return new EmbeddingConfig(provider, model, provider == "local" ? "local" : "remote");
    }

    public async Task<EmbedPendingResult> EmbedPendingAsync(string projectId, int? limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(projectId);

        await using var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        // memory_embed_pending rejects a NULL/0 argument; use the 0-arg form when no limit
        // is given ("process all pending") (M2).
        await using var embed = connection.CreateCommand();
        embed.CommandText = limit is > 0 ? MemorySql.EmbedPending : MemorySql.EmbedPendingAll;
        if (limit is > 0)
        {
            embed.Parameters.AddWithValue("@limit", limit.Value);
        }

        var processed = Convert.ToInt32(await embed.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

        await using var pending = connection.CreateCommand();
        pending.CommandText = MemorySql.PendingCount;
        var pendingCount = Convert.ToInt32(await pending.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

        return new EmbedPendingResult(processed, pendingCount);
    }

    public async Task<IReadOnlyList<MemoryEntry>> ListContextAsync(string projectId, string context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNullOrWhiteSpace(projectId);
        ArgumentNullException.ThrowIfNullOrWhiteSpace(context);

        await using var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = MemorySql.SelectEntriesByContext;
        command.Parameters.AddWithValue("@context", context);

        var entries = new List<MemoryEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(MemoryRowMapper.ToEntry(reader));
        }

        return entries;
    }

    private static async Task<bool> EntryExistsAsync(
        SqliteConnection connection, string path, string context, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM dbmem_content WHERE path = @path AND context = @context LIMIT 1";
        command.Parameters.AddWithValue("@path", path);
        command.Parameters.AddWithValue("@context", context);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<MemoryEntry?> FindEntryAsync(
        SqliteConnection connection, string context, string content, CancellationToken cancellationToken)
    {
        // Prefer the row in the requested context; fall back to any row with this content
        // (global dedup may have skipped the insert because another context owns it).
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT hash, path, context, value, created_at
            FROM dbmem_content
            WHERE value = @content
            ORDER BY CASE WHEN context = @context THEN 0 ELSE 1 END, rowid DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("@content", content);
        command.Parameters.AddWithValue("@context", context);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MemoryRowMapper.ToEntry(reader) : null;
    }

    private static async Task<MemoryEntry?> GetEntryAsync(
        SqliteConnection connection, string path, string context, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT hash, path, context, value, created_at FROM dbmem_content WHERE path = @path AND context = @context LIMIT 1";
        command.Parameters.AddWithValue("@path", path);
        command.Parameters.AddWithValue("@context", context);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? MemoryRowMapper.ToEntry(reader) : null;
    }
}
