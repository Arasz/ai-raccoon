using AiRaccon.Infrastructure.Sqlite;

namespace AiRaccon.Infrastructure.Sync;

/// <summary>Opens the bank connection with the cloudsync extension loaded; the injected factory must be built with loadCloudSync: true.</summary>
public sealed class CloudSyncConnectionFactory : ICloudSyncConnectionFactory
{
    private readonly SqliteConnectionFactory _factory;

    public CloudSyncConnectionFactory(SqliteConnectionFactory factory) => _factory = factory;

    public async Task<ICloudSyncConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await _factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return new CloudSyncConnection(connection);
    }
}
