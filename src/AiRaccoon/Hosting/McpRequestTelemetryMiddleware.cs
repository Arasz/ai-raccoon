using System.Diagnostics;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Observability;
using DotNext.Threading;

namespace AiRaccoon.Hosting;

/// <summary>Measures dispatch delay and full response time for HTTP MCP POST requests.</summary>
public sealed class McpRequestTelemetryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TimeProvider timeProvider, ToolCallMetrics metrics, IMeasurementRecorder recorder)
    {
        if (context.Request.Path != "/mcp" || !HttpMethods.IsPost(context.Request.Method))
        {
            await next(context);
            return;
        }

        var startedAt = timeProvider.GetTimestamp();
        var requestActivity = Activity.Current ?? new Activity("POST /mcp").Start();
        context.Items[OtlpNames.McpRequestStartTimestampKey] = startedAt;

        var durationRecordingStatus = new Atomic<bool>();

        context.Response.OnCompleted(() =>
        {
            RecordDuration(ref durationRecordingStatus, recorder, requestActivity, timeProvider, metrics, startedAt);
            return Task.CompletedTask;
        });

        try
        {
            await next(context);
        }
        finally
        {
            if (context.RequestAborted.IsCancellationRequested)
            {
                RecordDuration(ref durationRecordingStatus, recorder, requestActivity, timeProvider, metrics, startedAt);
            }
        }
    }

    private static void RecordDuration(ref Atomic<bool> recordingStatus, IMeasurementRecorder recorder, Activity requestActivity, TimeProvider timeProvider, ToolCallMetrics metrics, long startedAt)
    {
        if (!recordingStatus.CompareExchange(true, false, out _))
        {
            return;
        }

        var duration = timeProvider.GetElapsedTime(startedAt);

        metrics.RecordRequestDuration(duration);

        var measurement = new Measurement(MetricsConfigKeys.McpRequestDurationMetricName,
            MeasurementKind.Histogram, duration.TotalMilliseconds, "ms", timeProvider.GetUtcNow(),
            MetricsConfigKeys.SelfMetricsProjectId);

        recorder.Record(measurement);

        requestActivity.Stop();
    }
}
