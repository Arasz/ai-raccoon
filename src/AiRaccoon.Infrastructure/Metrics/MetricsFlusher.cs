using System.Diagnostics;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Core.Observability;
using AiRaccoon.Infrastructure.Maintenance;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Metrics;

/// <summary>
///     Fixed-interval background flusher (docs/plans/2026-08-15-performance-metrics-implementation.md,
///     WP3): on every tick, drains the buffer and batch-writes it. Only when that batch was
///     non-empty, it also records its own flush duration, batch size and cumulative drop count
///     directly to the store (F7 owner ruling: no self-metrics for an empty run) — never through the
///     buffer, so it cannot recurse by construction. A batch write retries a transient BUSY/LOCKED
///     failure (WP12 Fix C) before it is logged and dropped, un-retried, for the pass.
/// </summary>
public sealed partial class MetricsFlusher(
    IMeasurementBuffer buffer,
    IMetricsStore store,
    ISettingsStore settings,
    TimeProvider timeProvider,
    IOperationTelemetry telemetry,
    ILogger<MetricsFlusher> logger)
    : BackgroundService
{
    /// <summary>Span name and `operation` tag of one flush pass.</summary>
    internal const string OperationName = "metrics.flush";

    /// <summary>Caps the shutdown-time final flush so a slow or hanging store can never hang shutdown.</summary>
    internal static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Completed after each flush pass (batch write + self-metrics write); test seam.</summary>
    internal TickSignal Flushes { get; } = new();

    /// <summary>Completed once the periodic timer is armed; test seam.</summary>
    internal TickSignal TimerArmed { get; } = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ApplyBufferCapacitySafeAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(await ReadFlushIntervalSafeAsync(stoppingToken).ConfigureAwait(false),
            timeProvider);
        TimerArmed.Increment();
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            await FlushOnceAsync(stoppingToken).ConfigureAwait(false);
            timer.Period = await ReadFlushIntervalSafeAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Drains and flushes whatever is still buffered before the host stops — without this, a
    ///     session that only ever enqueues (never lives to the next periodic tick) writes zero rows.
    ///     Bounded by <see cref="ShutdownFlushTimeout" />, on <paramref name="cancellationToken"/> and
    ///     the timer's own <see cref="TimeProvider"/>, so a slow or hanging store cannot hang shutdown.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource(ShutdownFlushTimeout, timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            await FlushOnceAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.ShutdownFlushTimedOut(logger);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The host's own shutdown token fired, not our timeout: nothing more to log.
        }
        catch (Exception ex)
        {
            Log.ShutdownFlushFailed(logger, ex);
        }

        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One flush pass: drain the buffer, batch-write it, then record self-metrics directly. Test seam.</summary>
    internal async Task FlushOnceAsync(CancellationToken cancellationToken)
    {
        using var pass = telemetry.Begin(OperationName);
        var start = timeProvider.GetTimestamp();
        var batch = buffer.DrainAll();

        if (batch.Count > 0)
        {
            // A flush that persisted buffered measurements is always worth reading, unlike an
            // idle one (WP13 span-volume fix, SweepHostedService's precedent): NoteWork only
            // fires here, never unconditionally. At this service's cadence (default 30s) —
            // far more frequent than BankMaintenanceHostedService's hourly-or-slower pass —
            // spanning every idle tick would flood span storage for no reason.
            pass.NoteWork();
        }

        var batchFailure = await TrySaveBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        // F7 (owner ruling): self-metrics are worth a row under exactly the condition NoteWork()
        // already uses above — an empty run gets no self-metrics either, not just no span.
        var selfMetricsFailure = batch.Count > 0
            ? await RecordSelfMetricsAsync(start, batch.Count, cancellationToken).ConfigureAwait(false)
            : null;
        Flushes.Increment();

        var failure = batchFailure ?? selfMetricsFailure;
        if (failure is not null)
        {
            pass.Failed(failure);
        }
        else
        {
            pass.Succeeded();
        }
    }

    /// <summary>Attempts, at most: the first counts as an attempt too, so this is the retry ceiling, not a retry count.</summary>
    internal const int MaxSaveAttempts = 3;

    /// <summary>Short and bounded — a convoy on the write lock clears in milliseconds, not seconds; TimeProvider-driven so tests never wait on the real clock.</summary>
    private static readonly TimeSpan SaveRetryDelay = TimeSpan.FromMilliseconds(50);

    private async Task<Exception?> TrySaveBatchAsync(IReadOnlyList<Measurement> batch, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return null;
        }

        for (var attempt = 1; attempt <= MaxSaveAttempts; attempt++)
        {
            try
            {
                await store.SaveBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6 && attempt < MaxSaveAttempts)
            {
                // SQLITE_BUSY / SQLITE_LOCKED: the same write-lock convoy WP12 shortens elsewhere.
                // Retried silently — only the final failure (below) is worth a log line.
                await Task.Delay(SaveRetryDelay, timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.FlushFailed(logger, ex, batch.Count);
                return ex;
            }
        }

        // Unreachable: MaxSaveAttempts >= 1 guarantees the loop above always returns or throws on
        // its own final iteration (the BUSY/LOCKED catch guard is false there, so it falls through
        // to the general catch above).
        throw new UnreachableException();
    }

    /// <summary>
    ///     Written directly to the store — never through <see cref="IMeasurementBuffer" /> or
    ///     <see cref="IMeasurementRecorder" /> — so a flush cannot recurse into itself, and self-metrics
    ///     land even when the buffer is at capacity (AC5).
    /// </summary>
    private async Task<Exception?> RecordSelfMetricsAsync(long start, int batchSize, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var durationMs = timeProvider.GetElapsedTime(start).TotalMilliseconds;
        const string selfMetricsProjectId = MetricsConfigKeys.SelfMetricsProjectId;
        Measurement[] selfMetrics =
        [
            new("metrics.flush.duration_ms", MeasurementKind.Histogram, durationMs, "ms", now, selfMetricsProjectId),
            new("metrics.flush.batch_size", MeasurementKind.Gauge, batchSize, "count", now, selfMetricsProjectId),
            new("metrics.dropped", MeasurementKind.Counter, buffer.DroppedCount, "count", now, selfMetricsProjectId)
        ];

        try
        {
            await store.SaveBatchAsync(selfMetrics, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.SelfMetricsWriteFailed(logger, ex);
            return ex;
        }
    }

    private async Task ApplyBufferCapacitySafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var raw = await settings.GetSettingAsync(MetricsConfigKeys.BufferCapacityGlobal, cancellationToken)
                .ConfigureAwait(false);
            buffer.ApplyCapacity(MetricsConfigKeys.ParseBufferCapacity(raw));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.SettingReadFailed(logger, ex);
        }
    }

    private async Task<TimeSpan> ReadFlushIntervalSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            var raw = await settings.GetSettingAsync(MetricsConfigKeys.FlushIntervalSecondsGlobal, cancellationToken)
                .ConfigureAwait(false);
            return TimeSpan.FromSeconds(MetricsConfigKeys.ParseFlushIntervalSeconds(raw));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.SettingReadFailed(logger, ex);
            return TimeSpan.FromSeconds(MetricsConfigKeys.DefaultFlushIntervalSeconds);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 970, Level = LogLevel.Warning,
            Message = "Metrics flush failed for a batch of {BatchSize}; the batch is dropped, not retried")]
        public static partial void FlushFailed(ILogger logger, Exception exception, int batchSize);

        [LoggerMessage(EventId = 971, Level = LogLevel.Warning, Message = "Metrics self-instrumentation write failed")]
        public static partial void SelfMetricsWriteFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 972, Level = LogLevel.Warning,
            Message = "Metrics setting read failed; falling back to the default")]
        public static partial void SettingReadFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 973, Level = LogLevel.Warning,
            Message = "Shutdown metrics flush timed out; some buffered measurements from this session may be lost")]
        public static partial void ShutdownFlushTimedOut(ILogger logger);

        [LoggerMessage(EventId = 974, Level = LogLevel.Warning, Message = "Shutdown metrics flush failed")]
        public static partial void ShutdownFlushFailed(ILogger logger, Exception exception);
    }
}
