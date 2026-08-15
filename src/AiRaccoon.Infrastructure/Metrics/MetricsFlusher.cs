using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Maintenance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Metrics;

/// <summary>
///     Fixed-interval background flusher (docs/plans/2026-08-15-performance-metrics-implementation.md,
///     WP3): on every tick, drains the buffer and batch-writes it, then records its own flush
///     duration, batch size and cumulative drop count directly to the store — never through the
///     buffer, so it cannot recurse by construction. A failed write is logged and never retried
///     within the same pass.
/// </summary>
public sealed partial class MetricsFlusher(
    IMeasurementBuffer buffer,
    IMetricsStore store,
    ISettingsStore settings,
    TimeProvider timeProvider,
    ILogger<MetricsFlusher> logger)
    : BackgroundService
{
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

    /// <summary>One flush pass: drain the buffer, batch-write it, then record self-metrics directly. Test seam.</summary>
    internal async Task FlushOnceAsync(CancellationToken cancellationToken)
    {
        var start = timeProvider.GetTimestamp();
        var batch = buffer.DrainAll();
        try
        {
            if (batch.Count > 0)
            {
                await store.SaveBatchAsync(batch, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.FlushFailed(logger, ex, batch.Count);
        }
        finally
        {
            await RecordSelfMetricsAsync(start, batch.Count, cancellationToken).ConfigureAwait(false);
            Flushes.Increment();
        }
    }

    /// <summary>
    ///     Written directly to the store — never through <see cref="IMeasurementBuffer" /> or
    ///     <see cref="IMeasurementRecorder" /> — so a flush cannot recurse into itself, and self-metrics
    ///     land even when the buffer is at capacity (AC5).
    /// </summary>
    private async Task RecordSelfMetricsAsync(long start, int batchSize, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var durationMs = timeProvider.GetElapsedTime(start).TotalMilliseconds;
        Measurement[] selfMetrics =
        [
            new("metrics.flush.duration_ms", MeasurementKind.Histogram, durationMs, "ms", now),
            new("metrics.flush.batch_size", MeasurementKind.Gauge, batchSize, "count", now),
            new("metrics.dropped", MeasurementKind.Counter, buffer.DroppedCount, "count", now)
        ];

        try
        {
            await store.SaveBatchAsync(selfMetrics, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.SelfMetricsWriteFailed(logger, ex);
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
        [LoggerMessage(EventId = 962, Level = LogLevel.Warning,
            Message = "Metrics flush failed for a batch of {BatchSize}; the batch is dropped, not retried")]
        public static partial void FlushFailed(ILogger logger, Exception exception, int batchSize);

        [LoggerMessage(EventId = 963, Level = LogLevel.Warning, Message = "Metrics self-instrumentation write failed")]
        public static partial void SelfMetricsWriteFailed(ILogger logger, Exception exception);

        [LoggerMessage(EventId = 964, Level = LogLevel.Warning,
            Message = "Metrics setting read failed; falling back to the default")]
        public static partial void SettingReadFailed(ILogger logger, Exception exception);
    }
}
