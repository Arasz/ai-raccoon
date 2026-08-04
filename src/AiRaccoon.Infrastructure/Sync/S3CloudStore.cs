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

        _bucket = options.Bucket!;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<S3CloudStore>.Instance;

        var config = new AmazonS3Config
        {
            ServiceURL = options.Endpoint,
            ForcePathStyle = true
        };

        if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        var credentials = new BasicAWSCredentials(options.AccessKey, options.SecretKey);
        _s3 = new AmazonS3Client(credentials, config);
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
        catch (AmazonS3Exception ex)
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
        catch (AmazonS3Exception ex)
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
    }
}
