using AiRaccoon.Infrastructure.Options;

namespace AiRaccoon.Infrastructure.Sync;

public interface ISyncCloudStoreFactory
{
    Task<SyncOptions> ReadOptionsAsync(CancellationToken cancellationToken = default);
    Task<ICloudStore> CreateAsync(CancellationToken cancellationToken = default);
}
