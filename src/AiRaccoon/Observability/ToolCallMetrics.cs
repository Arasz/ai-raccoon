using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AiRaccoon.Observability;

/// <summary>
///     OpenTelemetry-compatible metrics and traces for AiRaccoon MCP tool invocations.
///     Meter name is "AiRaccoon.MemoryTools" for discoverability by dotnet-counters.
///     project_id tags the counter but not the histogram, on cardinality grounds — see ADR 0002.
/// </summary>
public sealed class ToolCallMetrics : IDisposable
{
    private readonly Counter<long> _invocationCount;
    private readonly Histogram<double> _invocationDuration;

    public ToolCallMetrics()
    {
        Meter = new Meter(OtlpNames.MemoryToolsScope);
        ActivitySource = new ActivitySource(OtlpNames.MemoryToolsScope);

        _invocationCount = Meter.CreateCounter<long>(
            OtlpNames.ToolInvocations,
            unit: "{invocation}",
            description: "Number of MCP tool invocations");

        // Bucket boundaries prescribed by the MCP semantic convention for mcp.server.operation.duration
        // (open-telemetry/semantic-conventions-genai, docs/gen-ai/mcp.md, read at implementation time).
        var advice = new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 30, 60, 120, 300]
        };
        _invocationDuration = Meter.CreateHistogram<double>(
            OtlpNames.ToolDuration,
            "s",
            "Duration of MCP tool invocations",
            null,
            advice);
    }

    /// <summary>Meter named "AiRaccoon.MemoryTools" — discoverable by dotnet-counters via EventPipe.</summary>
    public Meter Meter { get; }

    /// <summary>ActivitySource for OpenTelemetry tracing.</summary>
    public ActivitySource ActivitySource { get; }

    /// <summary>Records a tool invocation: increments the counter and records the duration histogram.</summary>
    public void RecordInvocation(string tool, string projectId, TimeSpan duration, bool isError, string? errorType = null)
    {
        var result = isError ? "error" : "success";

        var counterTags = new TagList
        {
            { "tool", tool },
            { "result", result },
            { "project_id", projectId }
        };
        if (errorType is not null)
        {
            counterTags.Add("error.type", errorType);
        }

        _invocationCount.Add(1, counterTags);

        var histoTags = new TagList
        {
            { "tool", tool },
            { "result", result }
        };
        if (errorType is not null)
        {
            histoTags.Add("error.type", errorType);
        }

        _invocationDuration.Record(duration.TotalSeconds, histoTags);
    }

    public void Dispose() => Meter.Dispose();
}
