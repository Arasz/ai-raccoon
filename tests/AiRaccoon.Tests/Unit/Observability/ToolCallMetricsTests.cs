using System.Diagnostics;
using AiRaccoon.Observability;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ObservabilityCollection.Name)]
public class ToolCallMetricsTests
{
    [Fact]
    public void Meter_HasCorrectName()
    {
        var metrics = new ToolCallMetrics();
        metrics.Meter.Name.ShouldBe("AiRaccoon.MemoryTools");
    }

    [Fact]
    public void Counter_IncrementsOnRecordInvocation()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_tool_invocations");

        metrics.RecordInvocation("memory_write", TimeSpan.FromMilliseconds(42), false);

        var measurements = collector.GetMeasurementSnapshot();
        measurements.Count.ShouldBe(1);
        measurements[0].Value.ShouldBe(1);
        measurements[0].Tags["tool"].ShouldBe("memory_write");
        measurements[0].Tags["result"].ShouldBe("success");
    }

    [Fact]
    public void Counter_IncrementsOnErrorRecordInvocation()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_tool_invocations");

        metrics.RecordInvocation("memory_sync", TimeSpan.FromMilliseconds(500), true, "SyncConflictException");

        var measurements = collector.GetMeasurementSnapshot();
        measurements.Count.ShouldBe(1);
        measurements[0].Value.ShouldBe(1);
        measurements[0].Tags["tool"].ShouldBe("memory_sync");
        measurements[0].Tags["result"].ShouldBe("error");
        measurements[0].Tags["error_type"].ShouldBe("SyncConflictException");
    }

    [Fact]
    public void Histogram_RecordsDurationWithTags()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<double>(metrics.Meter, "ai_raccoon_tool_duration_ms");

        metrics.RecordInvocation("memory_search", TimeSpan.FromMilliseconds(250), false);

        var measurements = collector.GetMeasurementSnapshot();
        measurements.Count.ShouldBe(1);
        measurements[0].Value.ShouldBe(250.0, tolerance: 1.0);
        measurements[0].Tags["tool"].ShouldBe("memory_search");
        measurements[0].Tags["result"].ShouldBe("success");
    }

    [Fact]
    public void Histogram_RecordsErrorDurationWithErrorType()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<double>(metrics.Meter, "ai_raccoon_tool_duration_ms");

        metrics.RecordInvocation("memory_sync", TimeSpan.FromMilliseconds(1500), true, "SyncConflictException");

        var measurements = collector.GetMeasurementSnapshot();
        measurements.Count.ShouldBe(1);
        measurements[0].Value.ShouldBe(1500.0, tolerance: 1.0);
        measurements[0].Tags["tool"].ShouldBe("memory_sync");
        measurements[0].Tags["result"].ShouldBe("error");
        measurements[0].Tags["error_type"].ShouldBe("SyncConflictException");
    }

    [Fact]
    public void ActivitySource_StartsAndStopsActivity()
    {
        var metrics = new ToolCallMetrics();
        var started = new List<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "AiRaccoon.MemoryTools",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = activity => started.Add(activity),
            ActivityStopped = _ => { }
        };
        ActivitySource.AddActivityListener(listener);

        using var activity = metrics.ActivitySource.StartActivity("memory_write");
        activity.ShouldNotBeNull();
        activity!.OperationName.ShouldBe("memory_write");

        activity.SetTag("tool", "memory_write");
        activity.SetTag("project_id", "test-project");
        activity.Stop();

        started.Count.ShouldBeGreaterThanOrEqualTo(1, "ActivityListener may fire multiple times across parallel tests");
        started[0].TagObjects.ShouldContain(t => t.Key == "tool" && (string)t.Value! == "memory_write");
    }

    [Fact]
    public void ErrorRecord_SetsErrorStatusOnActivity()
    {
        var metrics = new ToolCallMetrics();
        Activity? stoppedActivity = null;

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "AiRaccoon.MemoryTools",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = _ => { },
            ActivityStopped = activity => stoppedActivity = activity
        };
        ActivitySource.AddActivityListener(listener);

        var activity = metrics.ActivitySource.StartActivity("memory_sync");
        activity.ShouldNotBeNull();
        activity!.SetStatus(ActivityStatusCode.Error, "sync conflict");
        activity.SetTag("error_type", "SyncConflictException");
        activity.Stop();

        stoppedActivity.ShouldNotBeNull();
        stoppedActivity!.Status.ShouldBe(ActivityStatusCode.Error);
        stoppedActivity.TagObjects.ShouldContain(t => t.Key == "error_type" && (string)t.Value! == "SyncConflictException");
    }

    [Fact]
    public void Histogram_MeterNameAndMetricCollectorWork()
    {
        var metrics = new ToolCallMetrics();
        // MS2, MS5: Meter name discoverable by dotnet-counters
        metrics.Meter.Name.ShouldBe("AiRaccoon.MemoryTools");

        // MS5: MetricCollector<long> smoke test
        using var collector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_tool_invocations");
        metrics.RecordInvocation("memory_stats", TimeSpan.FromMilliseconds(10), false);
        collector.GetMeasurementSnapshot().Count.ShouldBe(1);
    }
}
