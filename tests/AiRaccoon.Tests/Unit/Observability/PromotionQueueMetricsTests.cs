using AiRaccoon.Observability;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>project_id is tagged on purpose (owner ruling supersedes ADR-0002's cardinality stance here) — pins that it stays, so a future cleanup doesn't drop it by accident.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class PromotionQueueMetricsTests
{
    [Fact]
    public void RecordQueued_TagsProjectId()
    {
        using var metrics = new PromotionQueueMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_queue_queued");

        metrics.RecordQueued("acme", 3);

        var measurement = collector.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Tags["project_id"].ShouldBe("acme");
    }

    [Fact]
    public void RecordEviction_TagsProjectId()
    {
        using var metrics = new PromotionQueueMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_queue_evictions_total");

        metrics.RecordEviction("acme", 0.5, "capacity");

        var measurement = collector.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Tags["project_id"].ShouldBe("acme");
        measurement.Tags["reason"].ShouldBe("capacity");
    }

    [Fact]
    public void RecordPromoted_TagsProjectId()
    {
        using var metrics = new PromotionQueueMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_queue_promoted_total");

        metrics.RecordPromoted("acme", 12.5);

        var measurement = collector.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Tags["project_id"].ShouldBe("acme");
    }

    [Fact]
    public void RecordDiscarded_TagsProjectId()
    {
        using var metrics = new PromotionQueueMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_queue_discarded_total");

        metrics.RecordDiscarded("acme", 3.0);

        var measurement = collector.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Tags["project_id"].ShouldBe("acme");
    }

    [Fact]
    public void RecordEviction_RecordsTheVictimScore()
    {
        using var metrics = new PromotionQueueMetrics();
        using var collector = new MetricCollector<double>(metrics.Meter, "ai_raccoon_queue_evicted_score");

        metrics.RecordEviction("acme", 0.42, "capacity");

        collector.GetMeasurementSnapshot().ShouldHaveSingleItem().Value.ShouldBe(0.42);
    }

    /// <summary>Histograms here stay untagged (the wait-seconds sibling does too); the counters carry project_id.</summary>
    [Fact]
    public void RecordEviction_LeavesTheScoreHistogramUntagged()
    {
        using var metrics = new PromotionQueueMetrics();
        using var collector = new MetricCollector<double>(metrics.Meter, "ai_raccoon_queue_evicted_score");

        metrics.RecordEviction("acme", 0.42, "capacity");

        collector.GetMeasurementSnapshot().ShouldHaveSingleItem().Tags.ShouldBeEmpty();
    }

    /// <summary>
    ///     Not a deterministic reproduction of a race on an unsynchronized field — exercises
    ///     concurrent writer/reader access to the gauge and requires it to complete without
    ///     exception and observe an actually-written value.
    /// </summary>
    [Fact]
    public async Task RecordUtilization_ConcurrentWithGaugeCollection_NeverThrows_AndObservesAWrittenValue()
    {
        using var metrics = new PromotionQueueMetrics();
        using var collector = new MetricCollector<double>(metrics.Meter, "ai_raccoon_queue_capacity_utilization");

        var writer = Task.Run(() =>
        {
            for (var i = 1; i <= 500; i++)
            {
                metrics.RecordUtilization(i / 500.0);
            }
        }, TestContext.Current.CancellationToken);
        var reader = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++)
            {
                collector.RecordObservableInstruments();
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(writer, reader);
        collector.RecordObservableInstruments();

        var snapshot = collector.GetMeasurementSnapshot();
        snapshot.ShouldNotBeEmpty();
        snapshot[^1].Value.ShouldBeInRange(0.0, 1.0, "the gauge must observe a value RecordUtilization actually wrote, not garbage");
    }
}
