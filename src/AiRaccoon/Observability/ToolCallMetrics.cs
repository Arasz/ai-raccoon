using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AiRaccoon.Observability;

/// <summary>
///     OpenTelemetry-compatible metrics and traces for AiRaccoon MCP tool invocations.
///     Meter name is "AiRaccoon.MemoryTools" for discoverability by dotnet-counters.
///     project_id Activity tag: fine while local-only; may need hashing when OTLP export is added.
/// </summary>
public sealed class ToolCallMetrics : IDisposable
{
    private readonly Counter<long> _invocationCount;
    private readonly Histogram<double> _invocationDurationMs;

    public ToolCallMetrics(bool statusWords = false, string? memoryLogPath = null)
    {
        StatusWords = statusWords;
        OperationLog = memoryLogPath is null ? null : new MemoryOperationLog(memoryLogPath);

        Meter = new Meter("AiRaccoon.MemoryTools");
        ActivitySource = new ActivitySource("AiRaccoon.MemoryTools");

        _invocationCount = Meter.CreateCounter<long>(
            "ai_raccoon_tool_invocations",
            description: "Number of MCP tool invocations");

        var advice = new InstrumentAdvice<double>
        {
            HistogramBucketBoundaries = [1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000]
        };
        _invocationDurationMs = Meter.CreateHistogram<double>(
            "ai_raccoon_tool_duration_ms",
            "ms",
            "Duration of MCP tool invocations in milliseconds",
            null,
            advice);
    }

    /// <summary>Meter named "AiRaccoon.MemoryTools" — discoverable by dotnet-counters via EventPipe.</summary>
    public Meter Meter { get; }

    /// <summary>ActivitySource for OpenTelemetry tracing.</summary>
    public ActivitySource ActivitySource { get; }

    /// <summary>When true, each tool call writes its one-word status to stderr as it starts.</summary>
    public bool StatusWords { get; }

    /// <summary>Append-only JSONL operation log; null when no AIRACCOON_MEMORY_LOG path is configured.</summary>
    public MemoryOperationLog? OperationLog { get; }

    /// <summary>Records a tool invocation: increments the counter and records the duration histogram.</summary>
    public void RecordInvocation(string tool, TimeSpan duration, bool isError, string? errorType = null)
    {
        var result = isError ? "error" : "success";

        var counterTags = new TagList
        {
            { "tool", tool },
            { "result", result }
        };
        if (errorType is not null)
        {
            counterTags.Add("error_type", errorType);
        }

        _invocationCount.Add(1, counterTags);

        var histoTags = new TagList
        {
            { "tool", tool },
            { "result", result }
        };
        if (errorType is not null)
        {
            histoTags.Add("error_type", errorType);
        }

        _invocationDurationMs.Record(duration.TotalMilliseconds, histoTags);
    }

    public void Dispose()
    {
        OperationLog?.Dispose();
        Meter.Dispose();
    }
}
