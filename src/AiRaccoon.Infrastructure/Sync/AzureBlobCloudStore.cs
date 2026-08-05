using AiRaccoon.Infrastructure.Options;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Sync;

/// <summary>Azure Blob Storage object store for memory.db snapshots using the Azure SDK.</summary>
public sealed partial class AzureBlobCloudStore : ICloudStore
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

    /// <summary>Test seam: build the store around an already-constructed client (canned transport).</summary>
    internal AzureBlobCloudStore(BlobServiceClient blobs, string container, ILogger<AzureBlobCloudStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(blobs);
        ArgumentException.ThrowIfNullOrWhiteSpace(container);

        _blobs = blobs;
        _container = container;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureBlobCloudStore>.Instance;
    }

    public async Task<CloudObject?> PullAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var blob = _blobs.GetBlobContainerClient(_container).GetBlobClient(objectKey);
            var response = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);

            // Azure returns the ETag quoted; strip quotes (matches the S3 storage format).
            var etag = response.Value.Details.ETag.ToString().Trim('"');
            return new CloudObject(response.Value.Content.ToArray(), etag);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
        catch (RequestFailedException ex)
        {
            Log.PullFailed(_logger, ex.Message);
            throw new SyncNetworkException($"Azure pull failed: {ex.Message}", ex);
        }
    }

    public async Task<string> PushAsync(string objectKey, byte[] data, string? etag,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var blob = _blobs.GetBlobContainerClient(_container).GetBlobClient(objectKey);
            var options = new BlobUploadOptions();

            if (etag is not null)
            {
                // Azure stores ETag strings verbatim and writes IfMatch "G" straight into the header — pre-quote.
                options.Conditions = new BlobRequestConditions
                {
                    IfMatch = new ETag($"\"{etag}\"")
                };
            }

            var response = await blob.UploadAsync(BinaryData.FromBytes(data), options, cancellationToken)
                .ConfigureAwait(false);

            // Azure returns the ETag quoted; strip for storage compatibility.
            return response.Value.ETag.ToString().Trim('"');
        }
        catch (RequestFailedException ex) when (ex.Status == 412)
        {
            throw new SyncConflictException("Remote changed since last pull — If-Match precondition failed.");
        }
        catch (RequestFailedException ex)
        {
            Log.PushFailed(_logger, ex.Message);
            throw new SyncNetworkException($"Azure push failed: {ex.Message}", ex);
        }
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

    private static partial class Log
    {
        [LoggerMessage(EventId = 202, Level = LogLevel.Error, Message = "Azure pull failed: {reason}")]
        public static partial void PullFailed(ILogger logger, string reason);

        [LoggerMessage(EventId = 203, Level = LogLevel.Error, Message = "Azure push failed: {reason}")]
        public static partial void PushFailed(ILogger logger, string reason);
    }
}
