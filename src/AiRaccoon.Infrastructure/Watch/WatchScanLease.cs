namespace AiRaccoon.Infrastructure.Watch;

/// <summary>Cross-process mutual exclusion for a (projectId, path) scan (D-2/D-3): only the
/// holder may scan; the holder renews on an interval or loses the lease to a live process.</summary>
public interface IWatchScanLease
{
    Task<bool> TryAcquireAsync(string projectId, string path, CancellationToken cancellationToken = default);

    Task<bool> TryRenewAsync(string projectId, string path, CancellationToken cancellationToken = default);

    Task ReleaseAsync(string projectId, string path, CancellationToken cancellationToken = default);
}

// Wave-2 placeholder: always grants. The SQLite-backed lease (watches.scan_owner /
// scan_lease_expires_at, D-2) lands with WP4.
/// <summary>Always-grant <see cref="IWatchScanLease"/> so DI resolves ahead of WP4.</summary>
public sealed class WatchScanLease : IWatchScanLease
{
    public Task<bool> TryAcquireAsync(string projectId, string path, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task<bool> TryRenewAsync(string projectId, string path, CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task ReleaseAsync(string projectId, string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
