using AiRaccoon.Infrastructure.Options;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Sync;

/// <summary>Azure Blob Storage object store for memory.db snapshots using the Azure SDK.</summary>
public sealed partial class AzureBlobCloudStore
{
    private readonly BlobServiceClient _blobs;
    private readonly string _container;
    private readonly ILogger<AzureBlobCloudStore> _logger;

    public AzureBlobCloudStore(SyncOptions options, ILogger<AzureBlobCloudStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Container);

        _container = options.Container;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureBlobCloudStore>.Instance;
        _blobs = CreateClient(options.ConnectionString);
    }

    /// <summary>Wraps the SDK ctor so a malformed connection string surfaces as a typed sync error.</summary>
    private static BlobServiceClient CreateClient(string connectionString)
    {
        try
        {
            return new BlobServiceClient(connectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new SyncNotConfiguredException(ex);
        }
    }
}
