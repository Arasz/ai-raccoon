using System.Diagnostics;
using AiRaccoon.Observability;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>Pins the ToolExecutionActivity contract: activity tags, success/error metrics, and disposal.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ObservabilityCollection.Name)]
public class ToolExecutionActivityTests
{
    [Fact]
    public void Ctor_StartsActivity_WithToolAndProjectIdTags()
    {
        var metrics = new ToolCallMetrics();
        var startedActivities = new List<Activity>();
        using var listener = new ActivityListener();
        listener.ShouldListenTo = source => source.Name == OtlpNames.MemoryToolsScope;
        listener.Sample = (ref _) => ActivitySamplingResult.AllData;
        listener.ActivityStarted = startedActivities.Add;
        listener.ActivityStopped = _ => { };
        ActivitySource.AddActivityListener(listener);

        using var activity = new ToolExecutionActivity(metrics, "memory_write", "acme");

        var started = startedActivities.ShouldHaveSingleItem();
        started.OperationName.ShouldBe("tools/call memory_write");
        started.Tags.Any(kv => kv.Key == "tool" && kv.Value?.ToString() == "memory_write").ShouldBeTrue();
        started.Tags.Any(kv => kv.Key == "project_id" && kv.Value?.ToString() == "acme").ShouldBeTrue();
    }

    [Fact]
    public void Ctor_TagsMcpMethodName_AndGenAiToolName()
    {
        var metrics = new ToolCallMetrics();
        var startedActivities = new List<Activity>();
        using var listener = new ActivityListener();
        listener.ShouldListenTo = source => source.Name == OtlpNames.MemoryToolsScope;
        listener.Sample = (ref _) => ActivitySamplingResult.AllData;
        listener.ActivityStarted = startedActivities.Add;
        listener.ActivityStopped = _ => { };
        ActivitySource.AddActivityListener(listener);

        using var activity = new ToolExecutionActivity(metrics, "memory_search", "acme");

        var started = startedActivities.ShouldHaveSingleItem();
        started.Tags.Any(kv => kv.Key == "mcp.method.name" && kv.Value == "tools/call").ShouldBeTrue();
        started.Tags.Any(kv => kv.Key == "gen_ai.tool.name" && kv.Value == "memory_search").ShouldBeTrue();
    }

    [Fact]
    public void RecordInvocation_ForwardsProjectIdToTheCounter()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);

        using var activity = new ToolExecutionActivity(metrics, "memory_write", "jsaa");
        activity.RecordInvocation();

        var measurements = collector.GetMeasurementSnapshot();
        measurements.Count.ShouldBe(1);
        measurements[0].Tags["project_id"].ShouldBe("jsaa");
    }

    [Fact]
    public void RecordInvocation_EmitsSuccessMetric()
    {
        var metrics = new ToolCallMetrics();
        using var invocationCollector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);
        using var durationCollector = new MetricCollector<double>(metrics.Meter, OtlpNames.ToolDuration);
        var startedActivities = new List<Activity>();
        using var listener = new ActivityListener();
        listener.ShouldListenTo = source => source.Name == OtlpNames.MemoryToolsScope;
        listener.Sample = (ref _) => ActivitySamplingResult.AllData;
        listener.ActivityStarted = startedActivities.Add;
        listener.ActivityStopped = _ => { };
        ActivitySource.AddActivityListener(listener);

        using var activity = new ToolExecutionActivity(metrics, "memory_stats", "acme");

        activity.RecordInvocation();

        var invocations = invocationCollector.GetMeasurementSnapshot();
        invocations.Count.ShouldBe(1);
        invocations[0].Tags["tool"].ShouldBe("memory_stats");
        invocations[0].Tags["result"].ShouldBe("success");
        durationCollector.GetMeasurementSnapshot().Count.ShouldBe(1);

        // ADR-0002: error.type is absent on success (only set on the error path).
        var started = startedActivities.ShouldHaveSingleItem();
        started.Tags.Any(kv => kv.Key == "error.type").ShouldBeFalse();
    }

    [Fact]
    public void RecordError_EmitsErrorMetric_AndTagsActivity()
    {
        var metrics = new ToolCallMetrics();
        using var invocationCollector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener();
        listener.ShouldListenTo = source => source.Name == OtlpNames.MemoryToolsScope;
        listener.Sample = (ref _) => ActivitySamplingResult.AllData;
        listener.ActivityStarted = _ => { };
        listener.ActivityStopped = stoppedActivities.Add;
        ActivitySource.AddActivityListener(listener);

        var ex = new InvalidOperationException("boom");
        using (var activity = new ToolExecutionActivity(metrics, "memory_search", "acme"))
        {
            activity.RecordError(ex);
        }

        var invocations = invocationCollector.GetMeasurementSnapshot();
        invocations.Count.ShouldBe(1);
        invocations[0].Tags["result"].ShouldBe("error");
        invocations[0].Tags["error.type"].ShouldBe("InvalidOperationException");
        invocations[0].Tags["project_id"].ShouldBe("acme");

        var stopped = stoppedActivities.ShouldHaveSingleItem();
        stopped.Status.ShouldBe(ActivityStatusCode.Error);
        stopped.StatusDescription.ShouldBe("boom");
        stopped.Tags.Any(kv => kv.Key == "error.type" && kv.Value?.ToString() == "InvalidOperationException").ShouldBeTrue();
    }

    [Fact]
    public void RecordError_AddsExceptionEvent_WithStackTrace()
    {
        var metrics = new ToolCallMetrics();
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener();
        listener.ShouldListenTo = source => source.Name == OtlpNames.MemoryToolsScope;
        listener.Sample = (ref _) => ActivitySamplingResult.AllData;
        listener.ActivityStarted = _ => { };
        listener.ActivityStopped = stoppedActivities.Add;
        ActivitySource.AddActivityListener(listener);

        Exception thrown;
        try
        {
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException caught)
        {
            thrown = caught;
        }

        using (var activity = new ToolExecutionActivity(metrics, "memory_search", "acme"))
        {
            activity.RecordError(thrown);
        }

        var stopped = stoppedActivities.ShouldHaveSingleItem();
        var exceptionEvent = stopped.Events.ShouldHaveSingleItem();
        exceptionEvent.Name.ShouldBe("exception");
        exceptionEvent.Tags.Any(t => t.Key == "exception.type" && t.Value?.ToString() == "System.InvalidOperationException").ShouldBeTrue();
        var stackTrace = exceptionEvent.Tags.SingleOrDefault(t => t.Key == "exception.stacktrace").Value?.ToString();
        stackTrace.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void RecordInvocation_SetsOkStatus_AndResultTag_OnTheActivity()
    {
        var metrics = new ToolCallMetrics();
        var startedActivities = new List<Activity>();
        using var listener = new ActivityListener();
        listener.ShouldListenTo = source => source.Name == OtlpNames.MemoryToolsScope;
        listener.Sample = (ref _) => ActivitySamplingResult.AllData;
        listener.ActivityStarted = startedActivities.Add;
        listener.ActivityStopped = _ => { };
        ActivitySource.AddActivityListener(listener);

        using var activity = new ToolExecutionActivity(metrics, "memory_write", "acme");
        activity.RecordInvocation();

        var started = startedActivities.ShouldHaveSingleItem();
        started.Status.ShouldBe(ActivityStatusCode.Ok);
        started.Tags.Any(kv => kv.Key == "result" && kv.Value?.ToString() == "success").ShouldBeTrue();
    }

    [Fact]
    public void RecordError_AddsResultErrorTag_OnTheActivity()
    {
        var metrics = new ToolCallMetrics();
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener();
        listener.ShouldListenTo = source => source.Name == OtlpNames.MemoryToolsScope;
        listener.Sample = (ref _) => ActivitySamplingResult.AllData;
        listener.ActivityStarted = _ => { };
        listener.ActivityStopped = stoppedActivities.Add;
        ActivitySource.AddActivityListener(listener);

        using (var activity = new ToolExecutionActivity(metrics, "memory_search", "acme"))
        {
            activity.RecordError(new InvalidOperationException("boom"));
        }

        var stopped = stoppedActivities.ShouldHaveSingleItem();
        stopped.Tags.Any(kv => kv.Key == "result" && kv.Value?.ToString() == "error").ShouldBeTrue();
    }

    [Fact]
    public void Dispose_StopsTheActivity()
    {
        var metrics = new ToolCallMetrics();
        var stoppedActivities = new List<Activity>();
        using var listener = new ActivityListener();
        listener.ShouldListenTo = source => source.Name == OtlpNames.MemoryToolsScope;
        listener.Sample = (ref _) => ActivitySamplingResult.AllData;
        listener.ActivityStarted = _ => { };
        listener.ActivityStopped = stoppedActivities.Add;
        ActivitySource.AddActivityListener(listener);

        using (new ToolExecutionActivity(metrics, "memory_sync", "acme"))
        {
            stoppedActivities.Count.ShouldBe(0);
        }

        stoppedActivities.Count.ShouldBe(1);
    }
}
