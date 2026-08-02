using System.Globalization;
using AiRaccon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;

namespace AiRaccon.Infrastructure.Sync;

/// <summary>Runs the cloudsync SQL functions over the bank connection (spec §4.1).</summary>
internal sealed class CloudSyncConnection : ICloudSyncConnection
{
    private readonly SqliteConnection _connection;

    public CloudSyncConnection(SqliteConnection connection) => _connection = connection;

    public async Task<IReadOnlyList<string>> GetCommittedContextsAsync(CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
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
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT memory_enable_sync(@context)";
        command.Parameters.AddWithValue("@context", context);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task NetworkInitAsync(string databaseId, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT cloudsync_network_init(@databaseId)";
        command.Parameters.AddWithValue("@databaseId", databaseId);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetApiKeyAsync(string apiKey, CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT cloudsync_network_set_apikey(@apiKey)";
        command.Parameters.AddWithValue("@apiKey", apiKey);
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<CloudSyncCounts> NetworkSyncAsync(CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT cloudsync_network_sync()";
        var json = (string)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return CloudSyncCounts.Parse(json);
    }

    public async Task<int> ReindexAsync(CancellationToken cancellationToken)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = "SELECT memory_reindex()";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}
