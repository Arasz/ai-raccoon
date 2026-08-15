using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Ingestion;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>Cadences the jobs run at. Settings, because every one of these numbers is expected to move.</summary>
public static class MaintenanceJobDefaults
{
    /// <summary>
    ///     Was 7 days on a per-process clock, which no short-lived process ever reached. Two hours
    ///     on a bank-persisted clock is a cadence a real process actually hits, and VACUUM measured
    ///     0.6 s on a 183 MB bank — the cost that made the old caution unnecessary.
    /// </summary>
    public static readonly TimeSpan VacuumInterval = TimeSpan.FromHours(2);
}

/// <summary>
///     VACUUM + ANALYZE. Converted from the hosted service's own timer (ADR-0070). An explicitly
///     configured <c>maintenance.vacuum-interval-days</c> still wins — the CLI verb that sets it did
///     not stop meaning anything — and only the DEFAULT moved from 7 days to 2 hours.
/// </summary>
public sealed class VacuumJob(Func<SqliteConnection, CancellationToken, Task<TimeSpan?>>? configuredInterval = null)
    : IMaintenanceJob
{
    public const string JobName = "vacuum";

    public string Name => JobName;

    public string DisplayName => "compact the bank";

    public TimeSpan? Interval => _resolved ?? MaintenanceJobDefaults.VacuumInterval;

    private TimeSpan? _resolved;

    public async Task<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        // The trailing semicolons are load-bearing: Dapper infers CommandType.StoredProcedure for a
        // SQL string with no whitespace, so a bare "VACUUM" throws "The CommandType 'StoredProcedure'
        // is not supported" instead of running. Found by a test asserting the job actually ran.
        await connection.ExecuteAsync(new CommandDefinition("VACUUM;", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        // VACUUM drops sqlite_stat1, so ANALYZE has to follow it rather than precede it.
        await connection.ExecuteAsync(new CommandDefinition("ANALYZE;", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return false;
    }

    /// <summary>Reads the configured override, if any. Called by the runner before it asks for <see cref="Interval" />.</summary>
    public async Task RefreshIntervalAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        if (configuredInterval is not null)
        {
            _resolved = await configuredInterval(connection, cancellationToken).ConfigureAwait(false);
            return;
        }

        var raw = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT value FROM settings WHERE key = @key",
                new { key = BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal },
                cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        _resolved = raw is null ? null : TimeSpan.FromDays(BankMaintenanceConfigKeys.ParseVacuumIntervalDays(raw));
    }
}

/// <summary>
///     Reclaims the pages ladder step v9 freed, once, without waiting for the vacuum cadence.
///     Separate from <see cref="VacuumJob" /> because it is a one-off consequence of a migration:
///     v9 rebuilds both vec0 tables and frees 42 MB on a real bank, and the file does not shrink
///     until something vacuums it.
/// </summary>
public sealed class Vec0ReclaimJob : IMaintenanceJob
{
    public const string JobName = "vec0-reclaim";

    public string Name => JobName;

    public string DisplayName => "reclaim vector-table space freed by the v9 migration";

    /// <summary>Once ever. The shape it reclaims is created exactly once, by the v9 migration.</summary>
    public TimeSpan? Interval => null;

    public async Task<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await connection.ExecuteAsync(new CommandDefinition("VACUUM;", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return false;
    }
}

/// <summary>
///     Splits rows holding more text than the embedding window into in-budget pieces (WP3 step 4).
///     Once ever: it heals a defect the write paths no longer create, so a second pass would find
///     nothing and cost a full-table tokenize to discover that.
/// </summary>
public sealed class ChunkBackfillJob(IMarkdownChunker chunker, TimeProvider timeProvider) : IMaintenanceJob
{
    public const string JobName = "chunk-backfill";

    public string Name => JobName;

    public string DisplayName => "re-chunk entries larger than the embedding window";

    public TimeSpan? Interval => null;

    public async Task<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var report = await new ChunkBackfill(chunker, timeProvider)
            .RunAsync(connection, dryRun: false, cancellationToken).ConfigureAwait(false);
        // Only a backfill that actually replaced rows leaves anything to embed.
        return report.RowsReplaced > 0;
    }
}
