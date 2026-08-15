using System.Diagnostics;
using AiRaccoon.Core.Metrics;

namespace AiRaccoon.Observability;

/// <summary>
///     One tool invocation's observability unit: starts an Activity, times the call, and records
///     the invocation metric and — when a recorder is given — a tool-level measurement on success
///     or error (docs/plans/2026-08-15-performance-metrics-implementation.md, WP8). Dispose the
///     activity to stop the trace.
/// </summary>
public sealed class ToolExecutionActivity : IDisposable
{
    private const string ToolActivityTag = "tool";
    private const string ProjectIdActivityTag = "project_id";
    private const string ErrorTypeActivityTag = "error.type";
    private const string ResultActivityTag = "result";
    private const string ResultSuccess = "success";
    private const string ResultError = "error";
    private const string DurationUnit = "ms";

    // MCP semantic convention (open-telemetry/semantic-conventions-genai, docs/gen-ai/mcp.md):
    // span name "{mcp.method.name} {target}", both carried as attributes.
    private const string McpMethodNameActivityTag = "mcp.method.name";
    private const string GenAiToolNameActivityTag = "gen_ai.tool.name";
    private const string ToolsCallMethodName = "tools/call";

    private readonly Activity? _activity;
    private readonly string _metricProjectId;
    private readonly ToolCallMetrics _metrics;
    private readonly IMeasurementRecorder? _recorder;
    private readonly Stopwatch _stopwatch;
    private readonly string _toolName;
    private bool _recorded;

    /// <summary>
    ///     The span always tags <paramref name="projectId" />. The counter tags
    ///     <paramref name="metricProjectId" /> when given, or <paramref name="projectId" /> otherwise —
    ///     callers whose span id is unbounded (e.g. a joined composite) must pass a bounded value.
    ///     <paramref name="recorder" /> is best-effort (<see cref="IMeasurementRecorder" />'s own
    ///     contract): a null recorder — telemetry disabled, or a host that never registered one —
    ///     simply records nothing.
    /// </summary>
    public ToolExecutionActivity(ToolCallMetrics metrics, string toolName, string projectId, string? metricProjectId = null,
        IMeasurementRecorder? recorder = null)
    {
        _metrics = metrics;
        _recorder = recorder;
        _toolName = toolName;
        _metricProjectId = metricProjectId ?? projectId;
        _activity = metrics.ActivitySource.StartActivity($"{ToolsCallMethodName} {toolName}");
        _activity?.SetTag(ToolActivityTag, toolName);
        _activity?.SetTag(ProjectIdActivityTag, projectId);
        _activity?.SetTag(McpMethodNameActivityTag, ToolsCallMethodName);
        _activity?.SetTag(GenAiToolNameActivityTag, toolName);
        _stopwatch = Stopwatch.StartNew();
    }

    public void Dispose()
    {
        _activity?.Dispose();
        _stopwatch.Stop();
        _stopwatch.Reset();
    }

    /// <summary>Records a successful invocation: counter + duration histogram, result=success.</summary>
    public void RecordInvocation()
    {
        if (_recorded)
        {
            return;
        }

        _recorded = true;
        _activity?.SetStatus(ActivityStatusCode.Ok);
        _activity?.SetTag(ResultActivityTag, ResultSuccess);
        _metrics.RecordInvocation(_toolName, _metricProjectId, _stopwatch.Elapsed, false);
        RecordMeasurement(_metricProjectId);
    }

    /// <summary>
    ///     Marks the activity as failed and records the invocation with the exception's type name.
    ///     metricProjectId overrides the counter's project id — a refused call passes a bounded
    ///     sentinel so an unauthorised caller cannot mint one series per id it invents.
    /// </summary>
    public void RecordError(Exception exception, string? metricProjectId = null)
    {
        if (_recorded)
        {
            return;
        }

        _recorded = true;
        _activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        _activity?.SetTag(ErrorTypeActivityTag, exception.GetType().Name);
        _activity?.SetTag(ResultActivityTag, ResultError);
        _activity?.AddException(exception);
        var boundedProjectId = metricProjectId ?? _metricProjectId;
        _metrics.RecordInvocation(_toolName, boundedProjectId, _stopwatch.Elapsed, true, exception.GetType().Name);
        RecordMeasurement(boundedProjectId);
    }

    /// <summary>The tool-level measurement family (WP8) — no correlation id; that belongs to the search-phase family MemoryTools tags itself.</summary>
    private void RecordMeasurement(string projectId) =>
        _recorder?.Record(new Measurement(_toolName, MeasurementKind.Histogram, _stopwatch.Elapsed.TotalMilliseconds,
            DurationUnit, DateTimeOffset.UtcNow, projectId));
}
