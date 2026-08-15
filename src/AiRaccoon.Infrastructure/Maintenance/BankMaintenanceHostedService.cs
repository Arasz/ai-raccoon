using AiRaccoon.Core.Memory;
using AiRaccoon.Core.SearchQuality;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Observability;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Maintenance;

/// <summary>
///     Bank maintenance loop: WAL checkpoint (TRUNCATE) at startup, shutdown and on the
///     checkpoint cadence; VACUUM + ANALYZE on the vacuum cadence (ADR-0010); noise_entries
///     retention purge on every pass (ADR-0029's TTL, never built until this task).
/// </summary>
public sealed partial class BankMaintenanceHostedService(
    ISqliteConnectionFactory factory,
    TimeProvider timeProvider,
    IOperationTelemetry telemetry,
    ILogger<BankMaintenanceHostedService> logger,
    IMemoryStore store,
    INoiseEntryStore noiseEntryStore,
    IPromotionQueueStore promotionQueueStore,
    ISearchQualityService searchQuality)
    : BackgroundService
{
    /// <summary>Contended checkpoints defer quickly instead of blocking the maintenance connection.</summary>
    private const int CheckpointBusyTimeoutMs = 250;

    /// <summary>Span name and `operation` tag of one maintenance pass.</summary>
    internal const string OperationName = "bank.maintenance";

    private DateTimeOffset? _lastVacuumUtc;

    /// <summary>Completed after each maintenance pass (startup + ticks); test seam.</summary>
    internal TickSignal Ticks { get; } = new();

    /// <summary>Completed once the periodic timer is armed; test seam (startup passes run before it).</summary>
    internal TickSignal TimerArmed { get; } = new();

    /// <summary>Completed after each post-tick interval re-read; test seam.</summary>
    internal TickSignal IntervalReReads { get; } = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup pass: one checkpoint before any traffic bounds the WAL for short-lived
        // processes; a failure logs and must not abort startup.
        try
        {
            await RunOnceAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.RunFailed(logger, ex);
        }
        finally
        {
            Ticks.Increment();
        }

        using var timer = new PeriodicTimer(await ReadCheckpointIntervalSafeAsync(stoppingToken).ConfigureAwait(false),
            timeProvider);
        TimerArmed.Increment();
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.RunFailed(logger, ex);
            }
            finally
            {
                Ticks.Increment();
            }

            // Re-read the interval so config changes apply without a restart.
            timer.Period = await ReadCheckpointIntervalSafeAsync(stoppingToken).ConfigureAwait(false);
            IntervalReReads.Increment();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RunCheckpointAsync(connection, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await RestoreBusyTimeoutAsync(connection, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.ShutdownCheckpointFailed(logger, ex);
        }

        await base.StopAsync(cancellationToken);
    }

    /// <summary>One maintenance pass: WAL checkpoint, then vacuum+analyze if due. Test seam.</summary>
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var pass = telemetry.Begin(OperationName);
        try
        {
            await RunPassAsync(pass, cancellationToken).ConfigureAwait(false);
            pass.Succeeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // shutdown cut the pass short: abandoned, not failed
        }
        catch (Exception ex)
        {
            pass.Failed(ex);
            throw;
        }
    }

    private async Task RunPassAsync(IOperationScope pass, CancellationToken cancellationToken)
    {
        // Hourly-or-slower cadence (WP13 span-volume fix): spanning every pass costs nothing here,
        // so every pass is worth reading rather than distinguishing checkpoint-only from vacuumed.
        pass.NoteWork();
        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunCheckpointAsync(connection, cancellationToken).ConfigureAwait(false);
            await RetryPendingEmbedsAsync(cancellationToken).ConfigureAwait(false);
            await PurgeExpiredNoiseEntriesAsync(cancellationToken).ConfigureAwait(false);
            await PurgeExpiredRetentionAsync(connection, cancellationToken).ConfigureAwait(false);

            var now = timeProvider.GetUtcNow();
            if (_lastVacuumUtc is null)
            {
                // Seed on first run: short-lived processes never vacuum (the clock resets per process).
                _lastVacuumUtc = now;
                return;
            }

            var vacuumIntervalDays = await ReadVacuumIntervalDaysSafeAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (now - _lastVacuumUtc.Value < TimeSpan.FromDays(vacuumIntervalDays))
            {
                return;
            }

            try
            {
                await using (var vacuum = connection.CreateCommand())
                {
                    vacuum.CommandText = "VACUUM";
                    await vacuum.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) // SQLITE_BUSY / SQLITE_LOCKED
            {
                // A contended VACUUM defers like a contended checkpoint: Warning, clock
                // untouched so the next tick retries — never an Error.
                Log.VacuumDeferred(logger);
                return;
            }

            // VACUUM drops sqlite_stat1, so ANALYZE must run after it.
            await using (var analyze = connection.CreateCommand())
            {
                analyze.CommandText = "ANALYZE";
                await analyze.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            // The VACUUM rewrote the whole file through the WAL; truncate it now, not at the next tick.
            await RunCheckpointAsync(connection, cancellationToken).ConfigureAwait(false);

            _lastVacuumUtc = now;
            pass.Tag("vacuumed", "true");
            Log.Vacuum(logger);
        }
        finally
        {
            // The connection returns to the pool: restore the factory's busy timeout so a
            // future borrower never inherits the maintenance connection's short one.
            await RestoreBusyTimeoutAsync(connection, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Wal-checkpoint (TRUNCATE), reading the returned busy|log|checkpointed tuple:
    ///     busy&gt;0 means readers/writers pin the WAL — defer (Warning), never an error.
    /// </summary>
    private async Task RunCheckpointAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // A short busy timeout defers a contended checkpoint instead of blocking the
        // maintenance connection for seconds; the next tick retries.
        await using (var busy = connection.CreateCommand())
        {
            busy.CommandText = $"PRAGMA busy_timeout={CheckpointBusyTimeoutMs}";
            await busy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var busyFrames = reader.GetInt32(0);
        if (busyFrames > 0)
        {
            Log.CheckpointDeferred(logger, busyFrames);
        }
        else
        {
            Log.Checkpoint(logger);
        }
    }

    /// <summary>
    ///     Retries embedding for every project reporting a nonzero pending count (.NET-F1): a
    ///     watch-triggered embed failure leaves a row `embed_state='pending'` forever with nothing
    ///     else retrying it, so this pass is the only recovery besides another file change or a
    ///     manual `memory_embed_pending` call. One project's failure is logged and never blocks the
    ///     rest of the sweep or the checkpoint/vacuum pass.
    /// </summary>
    private async Task RetryPendingEmbedsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<string> projectIds;
        try
        {
            projectIds = await store.GetProjectIdsAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.PendingEmbedSweepFailed(logger, ex);
            return;
        }

        foreach (var projectId in projectIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stats = await store.GetStatsAsync(projectId, cancellationToken).ConfigureAwait(false);
                if (stats.PendingCount == 0)
                {
                    continue;
                }

                var result = await store.EmbedPendingAsync(projectId, null, cancellationToken).ConfigureAwait(false);
                Log.PendingEmbedsRetried(logger, projectId, result.Processed, result.Pending);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.PendingEmbedRetryFailed(logger, projectId, ex);
            }
        }
    }

    /// <summary>
    ///     Age-bounds the two tables that grew without one (ADR-0055). Same log-and-continue
    ///     discipline as the noise purge: retention is housekeeping, never a reason to fail a pass.
    /// </summary>
    private async Task PurgeExpiredRetentionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();

            var discardDays = await ReadIntervalAsync(connection,
                BankMaintenanceConfigKeys.PromotionDiscardRetentionDaysGlobal,
                BankMaintenanceConfigKeys.ParsePromotionDiscardRetentionDays, cancellationToken).ConfigureAwait(false);
            var discards = await promotionQueueStore
                .PurgeOldDiscardsAsync(now, discardDays, cancellationToken).ConfigureAwait(false);
            if (discards > 0)
            {
                Log.PromotionDiscardsPurged(logger, discards, discardDays);
            }

            var qualityDays = await ReadIntervalAsync(connection,
                BankMaintenanceConfigKeys.SearchQualityRetentionDaysGlobal,
                BankMaintenanceConfigKeys.ParseSearchQualityRetentionDays, cancellationToken).ConfigureAwait(false);
            var quality = await searchQuality
                .PurgeOlderThanAsync(now, qualityDays, cancellationToken).ConfigureAwait(false);
            if (quality > 0)
            {
                Log.SearchQualityPurged(logger, quality, qualityDays);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.RetentionPurgeFailed(logger, ex);
        }
    }

    /// <summary>
    ///     ADR-0029's retention TTL, never built until this task: noise_entries would otherwise
    ///     accumulate forever. A failure logs and never blocks the checkpoint/vacuum pass — same
    ///     silent-failure discipline as the pending-embed retry sweep above.
    /// </summary>
    private async Task PurgeExpiredNoiseEntriesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            var purged = await noiseEntryStore.PurgeExpiredAsync(now, cancellationToken).ConfigureAwait(false);
            if (purged > 0)
            {
                Log.NoiseEntriesPurged(logger, purged);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.NoiseEntryPurgeFailed(logger, ex);
        }
    }

    /// <summary>Re-applies the factory's busy timeout (5000 ms) before the connection returns to the pool.</summary>
    private static async Task RestoreBusyTimeoutAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var busy = connection.CreateCommand();
        busy.CommandText = "PRAGMA busy_timeout=5000";
        await busy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<TimeSpan> ReadCheckpointIntervalSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
            var minutes = await ReadIntervalAsync(connection,
                BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal,
                BankMaintenanceConfigKeys.ParseCheckpointIntervalMinutes, cancellationToken).ConfigureAwait(false);
            return TimeSpan.FromMinutes(minutes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.IntervalReadFailed(logger, ex);
            return TimeSpan.FromMinutes(BankMaintenanceConfigKeys.DefaultCheckpointIntervalMinutes);
        }
    }

    private async Task<int> ReadVacuumIntervalDaysSafeAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadIntervalAsync(connection, BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal,
                BankMaintenanceConfigKeys.ParseVacuumIntervalDays, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.IntervalReadFailed(logger, ex);
            return BankMaintenanceConfigKeys.DefaultVacuumIntervalDays;
        }
    }

    private static async Task<int> ReadIntervalAsync(SqliteConnection connection, string key,
        Func<string?, int> parse, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = @key LIMIT 1";
        command.Parameters.AddWithValue("@key", key);
        return parse(Convert.ToString(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)));
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 510, Level = LogLevel.Information, Message = "Bank WAL checkpoint complete")]
        public static partial void Checkpoint(ILogger logger);

        [LoggerMessage(EventId = 511, Level = LogLevel.Warning,
            Message = "Bank WAL checkpoint deferred: {Busy} frames busy (readers or a writer pin the WAL)")]
        public static partial void CheckpointDeferred(ILogger logger, int busy);

        [LoggerMessage(EventId = 512, Level = LogLevel.Information, Message = "Bank vacuum + analyze complete")]
        public static partial void Vacuum(ILogger logger);

        [LoggerMessage(EventId = 516, Level = LogLevel.Warning,
            Message = "Bank vacuum deferred: the bank is busy (retries next tick)")]
        public static partial void VacuumDeferred(ILogger logger);

        [LoggerMessage(EventId = 513, Level = LogLevel.Error, Message = "Bank maintenance run failed")]
        public static partial void RunFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 514, Level = LogLevel.Warning,
            Message = "Maintenance interval read failed; falling back to the default")]
        public static partial void IntervalReadFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 515, Level = LogLevel.Warning, Message = "Shutdown checkpoint failed")]
        public static partial void ShutdownCheckpointFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 517, Level = LogLevel.Warning,
            Message = "Pending-embed retry sweep could not list project ids; retried on the next tick")]
        public static partial void PendingEmbedSweepFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 518, Level = LogLevel.Information,
            Message = "Pending-embed retry for {ProjectId}: {Processed} processed, {Pending} still pending")]
        public static partial void PendingEmbedsRetried(ILogger logger, string projectId, int processed, int pending);

        [LoggerMessage(EventId = 519, Level = LogLevel.Warning,
            Message = "Pending-embed retry failed for {ProjectId}; row(s) stay pending, retried on the next tick")]
        public static partial void PendingEmbedRetryFailed(ILogger logger, string projectId, Exception exception);

        [LoggerMessage(EventId = 520, Level = LogLevel.Information, Message = "Noise entry retention purge removed {Count} expired row(s)")]
        public static partial void NoiseEntriesPurged(ILogger logger, int count);

        [LoggerMessage(EventId = 521, Level = LogLevel.Warning, Message = "Noise entry retention purge failed; retried on the next tick")]
        public static partial void NoiseEntryPurgeFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 522, Level = LogLevel.Information,
            Message = "Promotion-discard retention purge removed {Count} row(s) past {RetentionDays} day(s) whose entry is gone")]
        public static partial void PromotionDiscardsPurged(ILogger logger, int count, int retentionDays);

        [LoggerMessage(EventId = 523, Level = LogLevel.Information,
            Message = "Search-quality retention purge removed {Count} row(s) past {RetentionDays} day(s)")]
        public static partial void SearchQualityPurged(ILogger logger, int count, int retentionDays);

        [LoggerMessage(EventId = 524, Level = LogLevel.Warning,
            Message = "Retention purge failed; retried on the next tick")]
        public static partial void RetentionPurgeFailed(ILogger logger, Exception exception);
    }
}
