namespace AiRaccoon.Infrastructure.Sync;

/// <summary>The sqlite-sync surface the orchestrator drives; stubbed in unit tests.</summary>
public interface ICloudSyncConnection : IAsyncDisposable
{
    Task<IReadOnlyList<string>> GetCommittedContextsAsync(CancellationToken cancellationToken);

    Task EnableSyncAsync(string context, CancellationToken cancellationToken);

    Task NetworkInitAsync(string databaseId, CancellationToken cancellationToken);

    Task SetApiKeyAsync(string apiKey, CancellationToken cancellationToken);

    Task<CloudSyncCounts> NetworkSyncAsync(CancellationToken cancellationToken);

    Task<int> ReindexAsync(CancellationToken cancellationToken);
}

public interface ICloudSyncConnectionFactory
{
    Task<ICloudSyncConnection> OpenAsync(CancellationToken cancellationToken);
}
