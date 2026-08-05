using System.Globalization;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sync;

/// <summary>Runs the cloudsync SQL functions over the bank connection (see docs/work/features-agent-memory/spec-issue-1.md §4.1).</summary>
internal sealed class CloudSyncConnection(SqliteConnection connection) : ICloudSyncConnection
{
    public async Task<IReadOnlyList<string>> GetCommittedContextsAsync(CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = MemorySql.CommittedContexts;

        var contexts = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            contexts.Add(reader.GetString(0));
        }

        return contexts;
    }

    public async Task EnableSyncAsync(string context, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT memory_enable_sync(@context)";
        command.Parameters.AddWithValue("@context", context);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task NetworkInitAsync(string databaseId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT cloudsync_network_init(@databaseId)";
        command.Parameters.AddWithValue("@databaseId", databaseId);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT cloudsync_network_set_apikey(@apiKey)";
        command.Parameters.AddWithValue("@apiKey", apiKey);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CloudSyncCounts> NetworkSyncAsync(CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT cloudsync_network_sync()";
        var json = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return CloudSyncCounts.Parse(json);
    }

    public async Task<int> ReindexAsync(CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT memory_reindex()";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}
