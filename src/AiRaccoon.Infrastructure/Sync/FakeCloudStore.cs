using System.Collections.Concurrent;
using AiRaccoon.Core.Sync;

namespace AiRaccoon.Infrastructure.Sync;

/// <summary>In-memory ICloudStore with ETag tracking for sync merge tests.</summary>
public sealed class FakeCloudStore : ICloudStore
{
    private readonly ConcurrentDictionary<string, StoredObject> _store = new();

    public Task<CloudObject?> PullAsync(string objectKey, CancellationToken cancellationToken = default)
    {
        if (_store.TryGetValue(objectKey, out var stored))
        {
            return Task.FromResult<CloudObject?>(new CloudObject(stored.Data, stored.ETag));
        }

        return Task.FromResult<CloudObject?>(null);
    }

    public Task<string> PushAsync(string objectKey, byte[] data, string? etag,
        CancellationToken cancellationToken = default)
    {
        var current = _store.GetValueOrDefault(objectKey);

        if (current is not null && etag is not null && current.ETag != etag)
        {
            throw new SyncConflictException("Remote changed since last pull.");
        }

        var newETag = Guid.NewGuid().ToString("N");
        _store[objectKey] = new StoredObject(data, newETag);
        return Task.FromResult(newETag);
    }

    /// <summary>Directly set a stored object (for simulating remote changes in tests).</summary>
    public void Set(string objectKey, byte[] data) => _store[objectKey] = new StoredObject(data, Guid.NewGuid().ToString("N"));

    private sealed record StoredObject(byte[] Data, string ETag);
}
