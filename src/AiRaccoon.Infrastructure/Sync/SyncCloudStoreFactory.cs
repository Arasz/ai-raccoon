using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Sync;

/// <summary>
///     Resolves the cloud store per memory_sync call from the current settings rows:
///     sync add/remove write the rows, and the store is rebuilt on every call — never
///     constructed once at startup.
/// </summary>
public sealed class SyncCloudStoreFactory(IMemoryStore store, ILoggerFactory loggerFactory) : ISyncCloudStoreFactory
{
    public async Task<SyncOptions> ReadOptionsAsync(CancellationToken cancellationToken = default)
    {
        var provider = await store.GetSettingAsync(SyncSettingsKeys.Provider, cancellationToken);
        var endpoint = await store.GetSettingAsync(SyncSettingsKeys.Endpoint, cancellationToken);
        var bucket = await store.GetSettingAsync(SyncSettingsKeys.Bucket, cancellationToken);
        var accessKey = await store.GetSettingAsync(SyncSettingsKeys.AccessKey, cancellationToken);
        var secretKey = await store.GetSettingAsync(SyncSettingsKeys.SecretKey, cancellationToken);
        var region = await store.GetSettingAsync(SyncSettingsKeys.Region, cancellationToken);
        var objectKey = await store.GetSettingAsync(SyncSettingsKeys.ObjectKey, cancellationToken);
        var connectionString = await store.GetSettingAsync(SyncSettingsKeys.ConnectionString, cancellationToken);
        var container = await store.GetSettingAsync(SyncSettingsKeys.Container, cancellationToken);
        var account = await store.GetSettingAsync(SyncSettingsKeys.AzureAccount, cancellationToken);
        var s3Chain = bool.TryParse(
            await store.GetSettingAsync(SyncSettingsKeys.S3Chain, cancellationToken),
            out _);
        return new SyncOptions
        {
            Provider = SyncProviderParser.Parse(provider),
            Endpoint = endpoint,
            Bucket = bucket,
            AccessKey = accessKey,
            SecretKey = secretKey,
            Region = region,
            ObjectKey = objectKey,
            ConnectionString = connectionString,
            Container = container,
            Account = account,
            S3Chain = s3Chain
        };
    }

    public async Task<ICloudStore> CreateAsync(CancellationToken cancellationToken = default)
    {
        var options = await ReadOptionsAsync(cancellationToken);
        if (!options.IsConfigured)
        {
            // Provider row present without its credential rows must not crash the ctor
            // (silently-dead trap: provider says azure, only s3 rows exist).
            return new NullCloudStore();
        }

        return options.Provider switch
        {
            SyncProvider.Azure => new AzureBlobCloudStore(options, loggerFactory.CreateLogger<AzureBlobCloudStore>()),
            _ => new S3CloudStore(options, loggerFactory.CreateLogger<S3CloudStore>())
        };
    }
}
