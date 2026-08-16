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

    // Same shape the store uses for its noise ports: the primary constructor above stays the
    // narrow one the tests build, and production DI resolves the wider constructor below. A service
    // built without jobs simply runs none, which is what every pre-ADR-0070 test wants.
    private readonly MaintenanceJobRunner? _jobRunner;
    private readonly IReadOnlyList<IMaintenanceJob> _jobs = [];

    /// <inheritdoc cref="BankMaintenanceHostedService" />
    public BankMaintenanceHostedService(
        ISqliteConnectionFactory factory,
        TimeProvider timeProvider,
        IOperationTelemetry telemetry,
        ILogger<BankMaintenanceHostedService> logger,
        IMemoryStore store,
        INoiseEntryStore noiseEntryStore,
        IPromotionQueueStore promotionQueueStore,
        ISearchQualityService searchQuality,
        MaintenanceJobRunner jobRunner,
        IReadOnlyList<IMaintenanceJob> jobs)
        : this(factory, timeProvider, telemetry, logger, store, noiseEntryStore, promotionQueueStore, searchQuality)
    {
        _jobRunner = jobRunner;
        _jobs = jobs;
    }

    /// <summary>Completed after each maintenance pass (startup + ticks); test seam.</summary>
    internal TickSignal Ticks { get; } = new();

    /// <summary>Completed once the periodic timer is armed; test seam (startup passes run before it).</summary>
    internal TickSignal TimerArmed { get; } = new();

    /// <summary>Completed after each post-tick interval re-read; test seam.</summary>
    internal TickSignal IntervalReReads { get; } = new();

    /// <summary>Completed after each on-demand poll; test seam (ADR-0076).</summary>
    internal TickSignal OnDemandPolls { get; } = new();

    /// <summary>
    ///     How often the on-demand poll checks whether any job has outstanding work
    ///     (<see cref="IMaintenanceJob.HasWorkAsync" />, ADR-0076) — independent of, and much
    ///     shorter than, the heavy checkpoint/vacuum/purge pass's own cadence
    ///     (<see cref="BankMaintenanceConfigKeys.CheckpointIntervalMinutesGlobal" />, default 60
    ///     minutes). Without this split, an on-demand job's real latency was bounded by that hourly
    ///     cadence — a model migration could sit undrained for up to an hour absent a restart. 15
    ///     seconds is cheap: one indexed `maintenance_jobs` read per cadence-based job plus one
    ///     indexed outbox read for the on-demand one, and nothing in this loop opens a transaction
    ///     or writes unless a job is actually due.
    /// </summary>
    internal static readonly TimeSpan OnDemandPollInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Startup pass: one checkpoint before any traffic bounds the WAL for short-lived
        // processes; a failure logs and must not abort startup. Also the crash-recovery guarantee
        // for an on-demand job (ADR-0076): RunOnceAsync always calls _jobRunner.RunDueAsync, which
        // always asks each job's HasWorkAsync, regardless of any cadence or poll interval below.
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

        // Two independent loops, one connection each: the heavy pass stays on its own (long,
        // configurable) cadence exactly as before; the on-demand poll is new and short, and reuses
        // the SAME MaintenanceJobRunner.RunDueAsync a heavy pass already calls — not a second relay,
        // the same one invoked more often for the jobs that want that (ADR-0076).
        await Task.WhenAll(
            RunHeavyPassLoopAsync(timer, stoppingToken),
            RunOnDemandPollLoopAsync(stoppingToken)).ConfigureAwait(false);
    }

    private async Task RunHeavyPassLoopAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
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

    /// <summary>
    ///     Wakes every <see cref="OnDemandPollInterval" /> and asks the job runner whether anything
    ///     is due — cadence-based jobs answer from their own unaffected <c>Interval</c>, so this
    ///     cannot make vacuum/backfill/retention run any more often than before; only a job that
    ///     answers <see cref="IMaintenanceJob.HasWorkAsync" />&#160;true gets picked up sooner than
    ///     the heavy pass's own cadence would have found it.
    /// </summary>
    private async Task RunOnDemandPollLoopAsync(CancellationToken stoppingToken)
    {
        if (_jobRunner is null)
        {
            return; // no jobs configured (pre-ADR-0070 test shape) — nothing to poll for
        }

        using var poll = new PeriodicTimer(OnDemandPollInterval, timeProvider);
        while (await poll.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var connection = await factory.OpenBankAsync(stoppingToken).ConfigureAwait(false);
                await _jobRunner.RunDueAsync(connection, _jobs, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Same message as the heavy pass's own failure (513): both are "one maintenance
                // loop iteration failed, retried next time" — a second, near-duplicate EventId
                // for the on-demand loop would also have interleaved with MaintenanceJobRunner's
                // adjacent 525-526 block (EventIdBlocks_DoNotInterleaveBetweenOwners).
                Log.RunFailed(logger, ex);
            }
            finally
            {
                OnDemandPolls.Increment();
            }
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

            // Scheduling moved into MaintenanceJobRunner and the bank's maintenance_jobs ledger
            // (ADR-0070). The clock it replaces was the _lastVacuumUtc field below, seeded on first
            // run, so a bank only ever opened by short-lived processes never vacuumed at all.
            var outcomes = _jobRunner is null
                ? []
                : await _jobRunner.RunDueAsync(connection, _jobs, cancellationToken).ConfigureAwait(false);
            if (outcomes.Any(o => o.Ran))
            {
                pass.Tag("jobs.ran", string.Join(',', outcomes.Where(o => o.Ran).Select(o => o.Name)));

                // Sweep again ONLY when a job actually created rows to embed. chunk-backfill produced
                // 13,578 unembedded rows on a real bank and the sweep above had already run before it
                // did, so they sat until the next pass — up to a checkpoint interval away, absent from
                // vector search. Gated rather than unconditional: vacuum and reclaim never make work,
                // and a needless extra sweep doubles the window in which a background embed races an
                // in-flight write (an E2E asserting one embedding request caught exactly that).
                if (outcomes.Any(o => o.CreatedWork))
                {
                    await RetryPendingEmbedsAsync(cancellationToken).ConfigureAwait(false);
                }

                // A VACUUM rewrites the whole file through the WAL; truncate it now, not next tick.
                await RunCheckpointAsync(connection, cancellationToken).ConfigureAwait(false);
            }
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
