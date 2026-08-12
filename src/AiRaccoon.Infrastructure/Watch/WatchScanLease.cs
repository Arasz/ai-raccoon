using AiRaccoon.Infrastructure.Sqlite;
using Dapper;

namespace AiRaccoon.Infrastructure.Watch;

/// <summary>
///     Cross-process mutual exclusion for a (projectId, path) scan
///     (docs/plans/2026-08-07-watch-scan-runaway-fix.md D-2/D-3): only the holder may scan; the
///     holder renews on an interval or loses the lease to a live process.
/// </summary>
public interface IWatchScanLease
{
    Task<bool> TryAcquireAsync(string projectId, string path, CancellationToken cancellationToken = default);

    Task<bool> TryRenewAsync(string projectId, string path, CancellationToken cancellationToken = default);

    Task ReleaseAsync(string projectId, string path, CancellationToken cancellationToken = default);
}

/// <summary>
///     Lease held on the watch's own row (watches.scan_owner / scan_lease_expires_at). A crashed
///     owner never releases, so expiry alone frees the lease; see D-2/D-3 in
///     docs/plans/2026-08-07-watch-scan-runaway-fix.md.
/// </summary>
public sealed class SqliteWatchScanLease(SqliteConnectionFactory factory, TimeProvider timeProvider) : IWatchScanLease
{
    public static TimeSpan LeaseTtl { get; } = TimeSpan.FromSeconds(60);

    /// <summary>Renew cadence — a third of the TTL, so two missed renewals still keep the lease.</summary>
    public static TimeSpan HeartbeatInterval { get; } = TimeSpan.FromSeconds(20);

    /// <summary>
    ///     Per-process identity. The Guid matters: PIDs are recycled, and without it a fresh
    ///     process could inherit a dead one's lease and skip recovery entirely.
    /// </summary>
    internal string Owner { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public async Task<bool> TryAcquireAsync(string projectId, string path,
        CancellationToken cancellationToken = default)
    {
        var now = Now();
        return await ExecuteAsync(MemorySql.AcquireWatchScanLease,
            new { projectId, path, owner = Owner, now, expiresAt = now + (long)LeaseTtl.TotalSeconds },
            cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> TryRenewAsync(string projectId, string path,
        CancellationToken cancellationToken = default) =>
        await ExecuteAsync(MemorySql.RenewWatchScanLease,
            new { projectId, path, owner = Owner, expiresAt = Now() + (long)LeaseTtl.TotalSeconds },
            cancellationToken).ConfigureAwait(false) == 1;

    public async Task ReleaseAsync(string projectId, string path, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(MemorySql.ReleaseWatchScanLease, new { projectId, path, owner = Owner },
            cancellationToken).ConfigureAwait(false);

    private long Now() => timeProvider.GetUtcNow().ToUnixTimeSeconds();

    private async Task<int> ExecuteAsync(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        return await connection
            .ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }
}
