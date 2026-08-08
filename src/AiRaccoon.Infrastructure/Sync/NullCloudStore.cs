using AiRaccoon.Core.Sync;

namespace AiRaccoon.Infrastructure.Sync;

/// <summary>No-op cloud store that rejects all operations — used when sync is not configured.</summary>
public sealed class NullCloudStore : ICloudStore
{
    public Task<CloudObject?> PullAsync(string objectKey, CancellationToken cancellationToken = default) => Task.FromResult<CloudObject?>(null);

    public Task<string> PushAsync(string objectKey, byte[] data, string? etag,
        CancellationToken cancellationToken = default) =>
        throw new SyncNotConfiguredException();
}
