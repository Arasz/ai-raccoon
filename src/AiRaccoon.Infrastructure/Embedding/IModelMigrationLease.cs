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
    ///     Generous on purpose: a full-bank re-embed is the one operation this codebase expects to
    ///     run long, and there is no mid-drain renewal (ask if a simpler shape would do — there is:
    ///     a second pass that out-waits this TTL just re-acquires and continues from wherever pending
    ///     rows remain; re-embedding an already-embedded row wastes inference, it does not corrupt).
    /// </summary>
    public static TimeSpan LeaseTtl { get; } = TimeSpan.FromMinutes(10);

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
