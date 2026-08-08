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
    private const string ResultActivityTag = "result";
    private const string ResultSuccess = "success";
    private const string ResultError = "error";

    private readonly Activity? _activity;
    private readonly ToolCallMetrics _metrics;
    private readonly Stopwatch _stopwatch;
    private readonly string _toolName;
    private readonly string _projectId;
    private bool _recorded;

    public ToolExecutionActivity(ToolCallMetrics metrics, string toolName, string projectId)
    {
        _metrics = metrics;
        _toolName = toolName;
        _projectId = projectId;
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
    public void RecordInvocation()
    {
        if (_recorded)
        {
            return;
        }

        _recorded = true;
        _activity?.SetStatus(ActivityStatusCode.Ok);
        _activity?.SetTag(ResultActivityTag, ResultSuccess);
        _metrics.RecordInvocation(_toolName, _projectId, _stopwatch.Elapsed, false);
    }

    /// <summary>Marks the activity as failed and records the invocation with the exception's type name.</summary>
    public void RecordError(Exception exception)
    {
        if (_recorded)
        {
            return;
        }

        _recorded = true;
        _activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
        _activity?.SetTag(ErrorTypeActivityTag, exception.GetType().Name);
        _activity?.SetTag(ResultActivityTag, ResultError);
        _metrics.RecordInvocation(_toolName, _projectId, _stopwatch.Elapsed, true, exception.GetType().Name);
    }
}
