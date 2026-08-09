using AiRaccoon.Core.Memory;
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
    public void RecordSnapshot_TagsDepthByProjectId()
    {
        using var metrics = new PromotionQueueMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_queue_queued");

        metrics.RecordSnapshot(new PromotionQueueStats(3, null, new Dictionary<string, int> { ["acme"] = 3 }), 100);
        collector.RecordObservableInstruments();

        var measurement = collector.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Value.ShouldBe(3);
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
    ///     A1: the queue is persisted in SQLite, not tracked by process-lifetime deltas. A
    ///     restart (a fresh <see cref="PromotionQueueMetrics" /> instance, so no prior snapshot
    ///     exists) followed by a discard must still report the store's real remaining depth.
    /// </summary>
    [Fact]
    public void RestartThenDiscard_ObservableDepth_ReflectsRealQueueState_NotAProcessLifetimeDelta()
    {
        const int rowsInStoreBeforeRestart = 40;
        const int discardedAfterRestart = 5;
        using var metrics = new PromotionQueueMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, "ai_raccoon_queue_queued");

        var remaining = rowsInStoreBeforeRestart - discardedAfterRestart;
        var stats = new PromotionQueueStats(remaining, null, new Dictionary<string, int> { ["acme"] = remaining });
        metrics.RecordSnapshot(stats, capacity: 100);
        collector.RecordObservableInstruments();

        var measurement = collector.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Value.ShouldBe(35, "the store holds 35 rows after the discard; a process-lifetime delta cannot know that");
        measurement.Tags["project_id"].ShouldBe("acme");
    }

    /// <summary>
    ///     A2: the cached-field version defaulted to 0.0 ("queue empty") until the first mutation
    ///     in-process, even when the store was already occupied at boot.
    /// </summary>
    [Fact]
    public void UtilizationGauge_ReadBeforeAnyMutation_ReflectsPreloadedOccupancy_NotZero()
    {
        using var metrics = new PromotionQueueMetrics();
        using var collector = new MetricCollector<double>(metrics.Meter, "ai_raccoon_queue_capacity_utilization");

        // A store pre-loaded at nonzero occupancy against a nonzero cap, published before any
        // propose/promote/discard runs in this process.
        var stats = new PromotionQueueStats(800, null, new Dictionary<string, int> { ["acme"] = 800 });
        metrics.RecordSnapshot(stats, capacity: 1000);
        collector.RecordObservableInstruments();

        var measurement = collector.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Value.ShouldBe(0.8, "queue is 80% full; a cached field defaulting to 0.0 would misreport it as empty");
    }

    /// <summary>
    ///     Not a deterministic reproduction of a race on an unsynchronized field — exercises
    ///     concurrent writer/reader access to the gauge and requires it to complete without
    ///     exception and observe an actually-written value.
    /// </summary>
    [Fact]
    public async Task RecordSnapshot_ConcurrentWithGaugeCollection_NeverThrows_AndObservesAWrittenValue()
    {
        using var metrics = new PromotionQueueMetrics();
        using var collector = new MetricCollector<double>(metrics.Meter, "ai_raccoon_queue_capacity_utilization");

        var writer = Task.Run(() =>
        {
            for (var i = 1; i <= 500; i++)
            {
                metrics.RecordSnapshot(new PromotionQueueStats(i, null, new Dictionary<string, int> { ["acme"] = i }), 500);
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
        snapshot[^1].Value.ShouldBeInRange(0.0, 1.0, "the gauge must observe a value RecordSnapshot actually wrote, not garbage");
    }
}
