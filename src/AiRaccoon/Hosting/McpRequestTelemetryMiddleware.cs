using System.Diagnostics;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Observability;

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
        var ownedActivity = Activity.Current is null ? new Activity("POST /mcp").Start() : null;
        context.Items[OtlpNames.McpRequestStartTimestampKey] = startedAt;

        var recorded = 0;

        context.Response.OnCompleted(() =>
        {
            RecordOnce();
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
                RecordOnce();
            }
        }

        return;

        void RecordOnce()
        {
            if (Interlocked.Exchange(ref recorded, 1) == 0)
            {
                RecordDuration(recorder, ownedActivity, timeProvider, metrics, startedAt);
            }
        }
    }

    private static void RecordDuration(IMeasurementRecorder recorder, Activity? ownedActivity, TimeProvider timeProvider, ToolCallMetrics metrics, long startedAt)
    {
        var duration = timeProvider.GetElapsedTime(startedAt);

        metrics.RecordRequestDuration(duration);

        var measurement = new Measurement(MetricsConfigKeys.McpRequestDurationMetricName,
            MeasurementKind.Histogram, duration.TotalMilliseconds, "ms", timeProvider.GetUtcNow(),
            MetricsConfigKeys.SelfMetricsProjectId);

        recorder.Record(measurement);

        ownedActivity?.Dispose();
    }
}
