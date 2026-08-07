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
}
