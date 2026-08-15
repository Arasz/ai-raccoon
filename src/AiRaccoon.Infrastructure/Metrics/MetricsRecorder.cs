using AiRaccoon.Core.Metrics;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Metrics;

/// <summary>
///     The <see cref="IMeasurementRecorder" /> port, backed by <see cref="IMeasurementBuffer" />.
///     Enqueue-only and exception-proof — a caller's search must succeed even when the buffer
///     misbehaves (docs/plans/2026-08-15-performance-metrics-implementation.md, WP3 AC1).
/// </summary>
public sealed partial class MetricsRecorder(IMeasurementBuffer buffer, ILogger<MetricsRecorder> logger)
    : IMeasurementRecorder
{
    public void Record(Measurement measurement)
    {
        try
        {
            buffer.TryEnqueue(measurement);
        }
        catch (Exception ex)
        {
            Log.RecordFailed(logger, ex);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 960, Level = LogLevel.Warning,
            Message = "Measurement enqueue failed; the measurement is dropped, the caller is unaffected")]
        public static partial void RecordFailed(ILogger logger, Exception exception);
    }
}
