using AiRaccoon.Infrastructure.Options;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Sync;

/// <summary>S3-compatible object store for memory.db snapshots using AWS SDK.</summary>
public sealed partial class S3CloudStore : ICloudStore
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly ILogger<S3CloudStore> _logger;

    public S3CloudStore(SyncOptions options, ILogger<S3CloudStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Bucket);
        if ((string.IsNullOrWhiteSpace(options.AccessKey) || string.IsNullOrWhiteSpace(options.SecretKey))
            && !options.S3Chain)
        {
            throw new ArgumentException("Persisted keys or the AWS credential chain are required.", nameof(options));
        }

        _bucket = options.Bucket;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<S3CloudStore>.Instance;
        _s3 = CreateClient(options);
    }

    /// <summary>Test seam: build the store around an already-constructed client (stubbed transport).</summary>
    internal S3CloudStore(IAmazonS3 s3, string bucket, ILogger<S3CloudStore>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(s3);
        ArgumentException.ThrowIfNullOrWhiteSpace(bucket);

        _s3 = s3;
        _bucket = bucket;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<S3CloudStore>.Instance;
    }

    /// <summary>
    ///     Builds the S3 client for the configured mode: persisted keys when present
    ///     (tie-break), else the AWS default credential chain (--cli mode; resolved lazily).
    /// </summary>
    internal static IAmazonS3 CreateClient(SyncOptions options)
    {
        var config = new AmazonS3Config
        {
            ServiceURL = options.Endpoint,
            ForcePathStyle = true
        };

        if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        if (!string.IsNullOrWhiteSpace(options.AccessKey) && !string.IsNullOrWhiteSpace(options.SecretKey))
        {
            return new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config);
        }

        return new AmazonS3Client(config);
    }

    public async Task<CloudObject?> PullAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _s3.GetObjectAsync(_bucket, objectKey, cancellationToken).ConfigureAwait(false);
            await using var stream = response.ResponseStream;
            using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            // ETag from S3 comes quoted; strip quotes.
            var etag = response.ETag?.Trim('"');
            return new CloudObject(ms.ToArray(), etag);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (AmazonClientException ex)
        {
            Log.AuthFailed(_logger, ex.Message);
            throw new SyncAuthFailedException(
                "AWS auth failed — run 'aws configure' or 'aws sso login', or verify the keys with 'ai-raccoon sync show'.", ex);
        }
        catch (AmazonServiceException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            Log.AuthFailed(_logger, ex.Message);
            throw new SyncAuthFailedException(
                "AWS auth failed — run 'aws configure' or 'aws sso login', or verify the keys with 'ai-raccoon sync show'.", ex);
        }
        catch (AmazonS3Exception ex)
        {
            Log.PullFailed(_logger, ex.Message);
            throw new SyncNetworkException($"S3 pull failed: {ex.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            Log.PullFailed(_logger, ex.Message);
            throw new SyncNetworkException($"S3 pull failed: {ex.Message}", ex);
        }
    }

    public async Task<string> PushAsync(string objectKey, byte[] data, string? etag,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new PutObjectRequest
            {
                BucketName = _bucket,
                Key = objectKey,
                InputStream = new MemoryStream(data)
            };

            if (etag is not null)
            {
                // Precondition: fail if the remote has changed.
                request.Headers["If-Match"] = $"\"{etag}\"";
            }

            var response = await _s3.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);

            // S3 returns ETag quoted; strip for storage compatibility.
            return response.ETag?.Trim('"') ?? "";
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.PreconditionFailed)
        {
            throw new SyncConflictException("Remote changed since last pull — If-Match precondition failed.");
        }
        catch (AmazonClientException ex)
        {
            Log.AuthFailed(_logger, ex.Message);
            throw new SyncAuthFailedException(
                "AWS auth failed — run 'aws configure' or 'aws sso login', or verify the keys with 'ai-raccoon sync show'.", ex);
        }
        catch (AmazonServiceException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            Log.AuthFailed(_logger, ex.Message);
            throw new SyncAuthFailedException(
                "AWS auth failed — run 'aws configure' or 'aws sso login', or verify the keys with 'ai-raccoon sync show'.", ex);
        }
        catch (AmazonS3Exception ex)
        {
            Log.PushFailed(_logger, ex.Message);
            throw new SyncNetworkException($"S3 push failed: {ex.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            Log.PushFailed(_logger, ex.Message);
            throw new SyncNetworkException($"S3 push failed: {ex.Message}", ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 200, Level = LogLevel.Error, Message = "S3 pull failed: {reason}")]
        public static partial void PullFailed(ILogger logger, string reason);

        [LoggerMessage(EventId = 201, Level = LogLevel.Error, Message = "S3 push failed: {reason}")]
        public static partial void PushFailed(ILogger logger, string reason);

        [LoggerMessage(EventId = 202, Level = LogLevel.Error, Message = "S3 auth failed: {reason}")]
        public static partial void AuthFailed(ILogger logger, string reason);
    }
}
