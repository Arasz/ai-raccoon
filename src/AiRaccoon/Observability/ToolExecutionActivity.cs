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
    private const string ErrorTypeActivityTag = "error_type";

    private readonly Activity? _activity;
    private readonly ToolCallMetrics _metrics;
    private readonly Stopwatch _stopwatch;
    private readonly string _toolName;

    public ToolExecutionActivity(ToolCallMetrics metrics, string toolName, string projectId)
    {
        _metrics = metrics;
        _toolName = toolName;
        _activity = metrics.ActivitySource.StartActivity(toolName);
        _activity?.SetTag(ToolActivityTag, toolName);
        _activity?.SetTag(ProjectIdActivityTag, projectId);
        _stopwatch = Stopwatch.StartNew();
    }

    public void Dispose()
    {
        _activity?.Dispose();
        _stopwatch.Stop();
        _stopwatch.Reset();
    }

    /// <summary>Records a successful invocation: counter + duration histogram, result=success.</summary>
    public void RecordInvocation() => _metrics.RecordInvocation(_toolName, _stopwatch.Elapsed, false);

    /// <summary>Marks the activity as failed and records the invocation with the exception's type name.</summary>
    public void RecordError(Exception exception)
    {
        _activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        _activity?.SetTag(ErrorTypeActivityTag, exception.GetType().Name);
        _metrics.RecordInvocation(_toolName, _stopwatch.Elapsed, true, exception.GetType().Name);
    }
}
