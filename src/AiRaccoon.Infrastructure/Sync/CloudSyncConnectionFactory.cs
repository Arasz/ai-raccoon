using AiRaccoon.Infrastructure.Sqlite;

namespace AiRaccoon.Infrastructure.Sync;

/// <summary>
///     Opens the bank connection with the cloudsync extension loaded; the injected factory must be built with
///     loadCloudSync: true.
/// </summary>
public sealed class CloudSyncConnectionFactory(SqliteConnectionFactory factory) : ICloudSyncConnectionFactory
{
    public async Task<ICloudSyncConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return new CloudSyncConnection(connection);
    }
}
