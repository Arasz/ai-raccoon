using AiRaccoon.Infrastructure.Options;

namespace AiRaccoon.Infrastructure.Sync;

/// <summary>Outcome of one memory sync run.</summary>
public sealed record SyncResult(int Sent, int Received, int Reindexed);

/// <summary>Runs the sqlite-sync push/pull sequence over the bank's committed contexts (shared + project:&lt;id&gt;), serialized per spec §6.3.</summary>
public class SyncService(SyncOptions options, ICloudSyncConnectionFactory connections)
{
    private readonly ICloudSyncConnectionFactory _connections = connections;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SyncOptions _options = options;

    public virtual async Task<SyncResult> MemorySyncAsync(string projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);

        if (!_options.IsConfigured)
        {
            throw new SyncNotConfiguredException();
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await _connections.OpenAsync(cancellationToken).ConfigureAwait(false);

            var committedContexts = await connection.GetCommittedContextsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var context in committedContexts)
            {
                await connection.EnableSyncAsync(context, cancellationToken).ConfigureAwait(false);
            }

            await connection.NetworkInitAsync(_options.ManagedDatabaseId!, cancellationToken).ConfigureAwait(false);
            await connection.SetApiKeyAsync(_options.ApiKey!, cancellationToken).ConfigureAwait(false);

            var send = await connection.NetworkSyncAsync(cancellationToken).ConfigureAwait(false);
            var receive = await connection.NetworkSyncAsync(cancellationToken).ConfigureAwait(false);
            var reindexCount = await connection.ReindexAsync(cancellationToken).ConfigureAwait(false);

            return new SyncResult(send.Sent, receive.Received, reindexCount);
        }
        finally
        {
            _gate.Release();
        }
    }
}
