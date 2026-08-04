using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Sync;

/// <summary>
///     Resolves the cloud store per memory_sync call from the CURRENT settings rows
///     (single-channel ruling F13): sync add/remove write the rows, and the store is
///     rebuilt on every call — never constructed once at startup.
/// </summary>
public sealed class SyncCloudStoreFactory(IMemoryStore store, ILoggerFactory loggerFactory)
{
    public async Task<SyncOptions> ReadOptionsAsync(CancellationToken cancellationToken = default)
    {
        var endpoint = await store.GetSettingAsync(SyncSettingsKeys.Endpoint, cancellationToken).ConfigureAwait(false);
        var bucket = await store.GetSettingAsync(SyncSettingsKeys.Bucket, cancellationToken).ConfigureAwait(false);
        var accessKey = await store.GetSettingAsync(SyncSettingsKeys.AccessKey, cancellationToken).ConfigureAwait(false);
        var secretKey = await store.GetSettingAsync(SyncSettingsKeys.SecretKey, cancellationToken).ConfigureAwait(false);
        var region = await store.GetSettingAsync(SyncSettingsKeys.Region, cancellationToken).ConfigureAwait(false);
        var objectKey = await store.GetSettingAsync(SyncSettingsKeys.ObjectKey, cancellationToken).ConfigureAwait(false);
        return new SyncOptions
        {
            Endpoint = endpoint,
            Bucket = bucket,
            AccessKey = accessKey,
            SecretKey = secretKey,
            Region = region,
            ObjectKey = objectKey
        };
    }

    public async Task<ICloudStore> CreateAsync(CancellationToken cancellationToken = default)
    {
        var options = await ReadOptionsAsync(cancellationToken).ConfigureAwait(false);
        return options.IsConfigured
            ? new S3CloudStore(options, loggerFactory.CreateLogger<S3CloudStore>())
            : new NullCloudStore();
    }
}
