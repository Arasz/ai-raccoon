using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Cross-process mutual exclusion over the single open <c>model_migration</c> row (ADR-0076),
///     mirroring <see cref="AiRaccoon.Infrastructure.Watch.IWatchScanLease" />: only the holder may
///     drain it, and a crashed holder never releases — expiry alone frees the lease for the next relay pass.
/// </summary>
public interface IModelMigrationLease
{
    Task<bool> TryAcquireAsync(SqliteConnection connection, CancellationToken cancellationToken = default);

    Task<bool> TryRenewAsync(SqliteConnection connection, CancellationToken cancellationToken = default);

    Task ReleaseAsync(SqliteConnection connection, CancellationToken cancellationToken = default);
}

/// <summary>Lease held on model_migration's own row (lease_owner / lease_expires_at).</summary>
public sealed class SqliteModelMigrationLease(TimeProvider timeProvider) : IModelMigrationLease
{
    /// <summary>
    ///     Renewed after every batch (<see cref="EntryEmbedder.DrainMigrationAsync" />, 32 rows), so
    ///     this only has to outlast one batch's real embedding latency, not the whole drain — a first
    ///     draft set this to 10 minutes on the (wrong) belief there was no mid-drain renewal, and a
    ///     real kill-mid-drain test caught it: every bank operation is refused while the migration is
    ///     open (ToolGate), so a stale lease from a dead holder is a full-bank outage for as long as
    ///     it survives, not just wasted inference. 60 seconds matches
    ///     <see cref="AiRaccoon.Infrastructure.Watch.SqliteWatchScanLease.LeaseTtl" />'s own value and
    ///     is generous for one batch while capping that outage at a minute instead of ten.
    /// </summary>
    public static TimeSpan LeaseTtl { get; } = TimeSpan.FromSeconds(60);

    /// <summary>Per-process identity, not the PID alone: PIDs recycle, and without this a fresh process could inherit a dead one's lease (ADR-0037).</summary>
    internal string Owner { get; } = $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public async Task<bool> TryAcquireAsync(SqliteConnection connection, CancellationToken cancellationToken = default)
    {
        var now = Now();
        return await ExecuteAsync(connection, MemorySql.AcquireModelMigrationLease,
            new { owner = Owner, now, expiresAt = now + (long)LeaseTtl.TotalSeconds }, cancellationToken)
            .ConfigureAwait(false) == 1;
    }

    public async Task<bool> TryRenewAsync(SqliteConnection connection, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(connection, MemorySql.RenewModelMigrationLease,
            new { owner = Owner, expiresAt = Now() + (long)LeaseTtl.TotalSeconds }, cancellationToken)
            .ConfigureAwait(false) == 1;

    public async Task ReleaseAsync(SqliteConnection connection, CancellationToken cancellationToken = default) =>
        await ExecuteAsync(connection, MemorySql.ReleaseModelMigrationLease, new { owner = Owner }, cancellationToken)
            .ConfigureAwait(false);

    private long Now() => timeProvider.GetUtcNow().ToUnixTimeSeconds();

    private static async Task<int> ExecuteAsync(SqliteConnection connection, string sql, object parameters,
        CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
}
