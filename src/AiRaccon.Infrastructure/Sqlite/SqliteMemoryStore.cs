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
        var hash = (string)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        await using var select = connection.CreateCommand();
        select.CommandText = MemorySql.SelectEntryByHash;
        select.Parameters.AddWithValue("@hash", hash);
        await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"memory_add_text returned hash '{hash}' but no dbmem_content row was found.");
        }

        return MemoryRowMapper.ToEntry(reader);
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

        await using var insert = connection.CreateCommand();
        insert.CommandText = MemorySql.InsertText;
        insert.Parameters.AddWithValue("@content", value);
        insert.Parameters.AddWithValue("@context", ContextNaming.SharedContext);
        await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

        return new MemoryEntry(hash, path, ContextNaming.SharedContext, value, createdAt);
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

        await using var count = connection.CreateCommand();
        count.CommandText = MemorySql.CountEntries;
        var entries = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

        await using var pending = connection.CreateCommand();
        pending.CommandText = MemorySql.PendingCount;
        var pendingCount = Convert.ToInt32(await pending.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);

        return new MemoryStats(entries, pendingCount);
    }
}
