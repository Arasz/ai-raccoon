namespace AiRaccoon.Infrastructure.Sync;

/// <summary>Single-file S3-compatible object store for memory.db snapshots with ETag/If-Match CAS.</summary>
public interface ICloudStore
{
    /// <summary>Downloads the remote snapshot and its ETag, or null if no object exists.</summary>
    Task<CloudObject?> PullAsync(string objectKey, CancellationToken cancellationToken = default);

    /// <summary>Uploads a snapshot with If-Match CAS; returns the new ETag on success.</summary>
    Task<string> PushAsync(string objectKey, byte[] data, string? etag, CancellationToken cancellationToken = default);
}

/// <summary>A snapshot object from cloud storage.</summary>
public sealed record CloudObject(byte[] Data, string? ETag);
