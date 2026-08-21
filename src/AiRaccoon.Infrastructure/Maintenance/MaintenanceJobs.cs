using AiRaccoon.Core.Chunking;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
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

    /// <summary>
    ///     How often the metrics reaper checks for rows past the retention window. Independent of
    ///     <see cref="VacuumInterval" /> even though it starts at the same value — the two jobs
    ///     reap different tables for different reasons and each may move on its own.
    /// </summary>
    public static readonly TimeSpan MetricsRetentionInterval = TimeSpan.FromHours(2);
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
public sealed class ChunkBackfillJob(IMarkdownChunker chunker, TimeProvider timeProvider, IEmbeddingService embeddingService) : IMaintenanceJob
{
    public const string JobName = "chunk-backfill";

    public string Name => JobName;

    public string DisplayName => "re-chunk entries larger than the embedding window";

    public TimeSpan? Interval => null;

    public async Task<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var report = await new ChunkBackfill(chunker, timeProvider, embeddingService)
            .RunAsync(connection, dryRun: false, cancellationToken).ConfigureAwait(false);
        // Only a backfill that actually replaced rows leaves anything to embed.
        return report.RowsReplaced > 0;
    }
}

/// <summary>
///     Deletes `metrics` rows past the retention window
///     (docs/plans/2026-08-15-performance-metrics-implementation.md, WP4). Retention is best-effort:
///     the window bounds growth, it does not guarantee a ceiling — holding more than it is within
///     contract. Re-cast from the plan's original inline-purge shape onto ADR-0070's job list, the
///     same generalisation <see cref="VacuumJob" /> underwent.
/// </summary>
public sealed class MetricsRetentionJob(TimeProvider timeProvider) : IMaintenanceJob
{
    public const string JobName = "metrics-retention";

    public string Name => JobName;

    public string DisplayName => "purge metrics rows past the retention window";

    public TimeSpan? Interval => MaintenanceJobDefaults.MetricsRetentionInterval;

    public async Task<bool> RunAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var days = await ReadRetentionDaysAsync(connection, cancellationToken).ConfigureAwait(false);
        var cutoff = timeProvider.GetUtcNow().AddDays(-days).ToUnixTimeSeconds();
        await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM metrics WHERE recorded_at < @cutoff",
                new { cutoff }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        // A reaper only deletes; it never leaves a row needing an embedding.
        return false;
    }

    private static async Task<int> ReadRetentionDaysAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var raw = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT value FROM settings WHERE key = @key",
                new { key = MetricsConfigKeys.RetentionDaysGlobal }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        return MetricsConfigKeys.ParseRetentionDays(raw);
    }
}
