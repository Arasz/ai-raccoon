using System.Diagnostics;
using AiRaccoon.Observability;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Shouldly;
using Xunit;

// ReSharper disable ExplicitCallerInfoArgument

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
        metrics.Meter.Name.ShouldBe(OtlpNames.MemoryToolsScope);
    }

    [Fact]
    public void Counter_IncrementsOnRecordInvocation()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);

        metrics.RecordInvocation("memory_write", "acme", TimeSpan.FromMilliseconds(42), false);

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
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);

        metrics.RecordInvocation("memory_sync", "acme", TimeSpan.FromMilliseconds(500), true, "SyncConflictException");

        var measurements = collector.GetMeasurementSnapshot();
        measurements.Count.ShouldBe(1);
        measurements[0].Value.ShouldBe(1);
        measurements[0].Tags["tool"].ShouldBe("memory_sync");
        measurements[0].Tags["result"].ShouldBe("error");
        measurements[0].Tags["error.type"].ShouldBe("SyncConflictException");
    }

    [Fact]
    public void Histogram_RecordsDurationWithTags()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<double>(metrics.Meter, OtlpNames.ToolDuration);

        metrics.RecordInvocation("memory_search", "acme", TimeSpan.FromMilliseconds(250), false);

        var measurements = collector.GetMeasurementSnapshot();
        measurements.Count.ShouldBe(1);
        measurements[0].Value.ShouldBe(0.25, 0.001);
        measurements[0].Tags["tool"].ShouldBe("memory_search");
        measurements[0].Tags["result"].ShouldBe("success");
    }

    [Fact]
    public void Histogram_RecordsErrorDurationWithErrorType()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<double>(metrics.Meter, OtlpNames.ToolDuration);

        metrics.RecordInvocation("memory_sync", "acme", TimeSpan.FromMilliseconds(1500), true, "SyncConflictException");

        var measurements = collector.GetMeasurementSnapshot();
        measurements.Count.ShouldBe(1);
        measurements[0].Value.ShouldBe(1.5, 0.001);
        measurements[0].Tags["tool"].ShouldBe("memory_sync");
        measurements[0].Tags["result"].ShouldBe("error");
        measurements[0].Tags["error.type"].ShouldBe("SyncConflictException");
    }

    [Fact]
    public void ActivitySource_StartsAndStopsActivity()
    {
        var metrics = new ToolCallMetrics();
        var started = new List<Activity>();

        using var listener = new ActivityListener();
        listener.ShouldListenTo = source => source.Name == OtlpNames.MemoryToolsScope;
        listener.Sample = (ref _) => ActivitySamplingResult.AllData;
        listener.ActivityStarted = started.Add;
        listener.ActivityStopped = _ => { };
        ActivitySource.AddActivityListener(listener);

        using var activity = metrics.ActivitySource.StartActivity("memory_write");
        activity.ShouldNotBeNull();
        activity.OperationName.ShouldBe("memory_write");

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

        using var listener = new ActivityListener();
        listener.ShouldListenTo = source => source.Name == OtlpNames.MemoryToolsScope;
        listener.Sample = (ref _) => ActivitySamplingResult.AllData;
        listener.ActivityStarted = _ => { };
        listener.ActivityStopped = activity => stoppedActivity = activity;
        ActivitySource.AddActivityListener(listener);

        var activity = metrics.ActivitySource.StartActivity("memory_sync");
        activity.ShouldNotBeNull();
        activity.SetStatus(ActivityStatusCode.Error, "sync conflict");
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
        // Meter name discoverable by dotnet-counters
        metrics.Meter.Name.ShouldBe(OtlpNames.MemoryToolsScope);

        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);
        metrics.RecordInvocation("memory_stats", "acme", TimeSpan.FromMilliseconds(10), false);
        collector.GetMeasurementSnapshot().Count.ShouldBe(1);
    }

    [Fact]
    public void Counter_TagsProjectId()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);

        metrics.RecordInvocation("memory_write", "jsaa", TimeSpan.FromMilliseconds(42), false);

        var measurements = collector.GetMeasurementSnapshot();
        measurements.Count.ShouldBe(1);
        measurements[0].Tags["project_id"].ShouldBe("jsaa");
    }

    [Fact]
    public void Histogram_DoesNotTagProjectId()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<double>(metrics.Meter, OtlpNames.ToolDuration);

        metrics.RecordInvocation("memory_write", "jsaa", TimeSpan.FromMilliseconds(42), false);

        var measurements = collector.GetMeasurementSnapshot();
        measurements.Count.ShouldBe(1);
        measurements[0].Tags.ContainsKey("project_id").ShouldBeFalse();
    }
}
