using AiRaccoon.Infrastructure.Options;
using Azure;
using Azure.Identity;
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
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Container);
        if (string.IsNullOrWhiteSpace(options.ConnectionString) && string.IsNullOrWhiteSpace(options.Account))
        {
            throw new ArgumentException("A connection string or an account name is required.", nameof(options));
        }

        _container = options.Container;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AzureBlobCloudStore>.Instance;
        _blobs = CreateClient(options);
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

    /// <summary>
    ///     Builds the blob client for the configured mode: connection string when present
    ///     (tie-break), else the account name with DefaultAzureCredential (--cli mode).
    /// </summary>
    internal static BlobServiceClient CreateClient(SyncOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            try
            {
                return new BlobServiceClient(options.ConnectionString);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                throw new SyncNotConfiguredException(ex);
            }
        }

        try
        {
            return new BlobServiceClient(
                new Uri($"https://{options.Account}.blob.core.windows.net"),
                new DefaultAzureCredential());
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new SyncNotConfiguredException(ex);
        }
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
        catch (CredentialUnavailableException ex)
        {
            Log.AuthFailed(_logger, ex.Message);
            throw new SyncAuthFailedException("Azure auth failed — run 'az login' (or set AZURE_TENANT_ID/AZURE_CLIENT_ID/AZURE_CLIENT_SECRET for headless use).", ex);
        }
        catch (AuthenticationFailedException ex)
        {
            Log.AuthFailed(_logger, ex.Message);
            throw new SyncAuthFailedException("Azure auth failed — run 'az login' (or set AZURE_TENANT_ID/AZURE_CLIENT_ID/AZURE_CLIENT_SECRET for headless use).", ex);
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            Log.AuthFailed(_logger, ex.Message);
            throw new SyncAuthFailedException("Azure auth failed — run 'az login' (or set AZURE_TENANT_ID/AZURE_CLIENT_ID/AZURE_CLIENT_SECRET for headless use).", ex);
        }
        catch (RequestFailedException ex)
        {
            Log.PullFailed(_logger, ex.Message);
            throw new SyncNetworkException($"Azure pull failed: {ex.Message}", ex);
        }
        catch (HttpRequestException ex)
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
        catch (CredentialUnavailableException ex)
        {
            Log.AuthFailed(_logger, ex.Message);
            throw new SyncAuthFailedException("Azure auth failed — run 'az login' (or set AZURE_TENANT_ID/AZURE_CLIENT_ID/AZURE_CLIENT_SECRET for headless use).", ex);
        }
        catch (AuthenticationFailedException ex)
        {
            Log.AuthFailed(_logger, ex.Message);
            throw new SyncAuthFailedException("Azure auth failed — run 'az login' (or set AZURE_TENANT_ID/AZURE_CLIENT_ID/AZURE_CLIENT_SECRET for headless use).", ex);
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            Log.AuthFailed(_logger, ex.Message);
            throw new SyncAuthFailedException("Azure auth failed — run 'az login' (or set AZURE_TENANT_ID/AZURE_CLIENT_ID/AZURE_CLIENT_SECRET for headless use).", ex);
        }
        catch (RequestFailedException ex)
        {
            Log.PushFailed(_logger, ex.Message);
            throw new SyncNetworkException($"Azure push failed: {ex.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            Log.PushFailed(_logger, ex.Message);
            throw new SyncNetworkException($"Azure push failed: {ex.Message}", ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 202, Level = LogLevel.Error, Message = "Azure pull failed: {reason}")]
        public static partial void PullFailed(ILogger logger, string reason);

        [LoggerMessage(EventId = 203, Level = LogLevel.Error, Message = "Azure push failed: {reason}")]
        public static partial void PushFailed(ILogger logger, string reason);

        [LoggerMessage(EventId = 204, Level = LogLevel.Error, Message = "Azure auth failed: {reason}")]
        public static partial void AuthFailed(ILogger logger, string reason);
    }
}
