using System.Diagnostics;

namespace AiRaccoon.Observability;

/// <summary>
///     One tool invocation's observability unit: starts an Activity, times the call, and records
///     the invocation metric on success or error. Dispose the activity to stop the trace.
/// </summary>
public sealed class ToolExecutionActivity : IDisposable
{
    private const string ToolActivityTag = "tool";
    private const string ProjectIdActivityTag = "project_id";
    private const string ErrorTypeActivityTag = "error.type";
    private const string ResultActivityTag = "result";
    private const string ResultSuccess = "success";
    private const string ResultError = "error";

    // MCP semantic convention (open-telemetry/semantic-conventions-genai, docs/gen-ai/mcp.md):
    // span name "{mcp.method.name} {target}", both carried as attributes.
    private const string McpMethodNameActivityTag = "mcp.method.name";
    private const string GenAiToolNameActivityTag = "gen_ai.tool.name";
    private const string ToolsCallMethodName = "tools/call";

    private readonly Activity? _activity;
    private readonly string _metricProjectId;
    private readonly ToolCallMetrics _metrics;
    private readonly Stopwatch _stopwatch;
    private readonly string _toolName;
    private bool _recorded;

    /// <summary>
    ///     The span always tags <paramref name="projectId" />. The counter tags
    ///     <paramref name="metricProjectId" /> when given, or <paramref name="projectId" /> otherwise —
    ///     callers whose span id is unbounded (e.g. a joined composite) must pass a bounded value.
    /// </summary>
    public ToolExecutionActivity(ToolCallMetrics metrics, string toolName, string projectId, string? metricProjectId = null)
    {
        _metrics = metrics;
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
        _metrics.RecordInvocation(_toolName, metricProjectId ?? _metricProjectId, _stopwatch.Elapsed, true,
            exception.GetType().Name);
    }
}
