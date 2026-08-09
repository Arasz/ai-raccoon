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
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.QueueQueued);

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
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.QueueEvictions);

        metrics.RecordEviction("acme", 0.5, "capacity");

        var measurement = collector.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Tags["project_id"].ShouldBe("acme");
        measurement.Tags["reason"].ShouldBe("capacity");
    }

    [Fact]
    public void RecordPromoted_TagsProjectId()
    {
        using var metrics = new PromotionQueueMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.QueuePromoted);

        metrics.RecordPromoted("acme", 12.5);

        var measurement = collector.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Tags["project_id"].ShouldBe("acme");
    }

    [Fact]
    public void RecordDiscarded_TagsProjectId()
    {
        using var metrics = new PromotionQueueMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.QueueDiscarded);

        metrics.RecordDiscarded("acme", 3.0);

        var measurement = collector.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Tags["project_id"].ShouldBe("acme");
    }

    [Fact]
    public void RecordEviction_RecordsTheVictimScore()
    {
        using var metrics = new PromotionQueueMetrics();
        using var collector = new MetricCollector<double>(metrics.Meter, OtlpNames.QueueEvictedScore);

        metrics.RecordEviction("acme", 0.42, "capacity");

        collector.GetMeasurementSnapshot().ShouldHaveSingleItem().Value.ShouldBe(0.42);
    }

    /// <summary>Histograms here stay untagged (the wait-seconds sibling does too); the counters carry project_id.</summary>
    [Fact]
    public void RecordEviction_LeavesTheScoreHistogramUntagged()
    {
        using var metrics = new PromotionQueueMetrics();
        using var collector = new MetricCollector<double>(metrics.Meter, OtlpNames.QueueEvictedScore);

        metrics.RecordEviction("acme", 0.42, "capacity");

        collector.GetMeasurementSnapshot().ShouldHaveSingleItem().Tags.ShouldBeEmpty();
    }

    /// <summary>
    ///     Regression check on the publish→observe round-trip and the project_id tag once a
    ///     snapshot has been published — not a pin on restart/discard behavior, which production
    ///     code does not execute here; that service-level property is covered by
    ///     PromotionQueueServiceTests through a real store.
    /// </summary>
    [Fact]
    public void AfterASnapshotIsPublished_DepthReflectsQueuedRowsPerProject()
    {
        const int queuedRows = 35;
        using var metrics = new PromotionQueueMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.QueueQueued);

        var stats = new PromotionQueueStats(queuedRows, null, new Dictionary<string, int> { ["acme"] = queuedRows });
        metrics.RecordSnapshot(stats, capacity: 100);
        collector.RecordObservableInstruments();

        var measurement = collector.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Value.ShouldBe(queuedRows);
        measurement.Tags["project_id"].ShouldBe("acme");
    }

    /// <summary>
    ///     A2: before any propose/promote/discard has published a snapshot in this process
    ///     (e.g. a fresh boot), the observable instruments must yield no measurement at all —
    ///     never a confident 0.0/empty that a collector can mistake for "queue is empty".
    /// </summary>
    [Fact]
    public void BeforeAnySnapshotIsPublished_ObservableInstruments_YieldNoMeasurement()
    {
        using var metrics = new PromotionQueueMetrics();
        using var depthCollector = new MetricCollector<long>(metrics.Meter, OtlpNames.QueueQueued);
        using var utilizationCollector = new MetricCollector<double>(metrics.Meter, OtlpNames.QueueCapacityUtilization);

        depthCollector.RecordObservableInstruments();
        utilizationCollector.RecordObservableInstruments();

        depthCollector.GetMeasurementSnapshot().ShouldBeEmpty("depth was never measured, not measured-as-zero");
        utilizationCollector.GetMeasurementSnapshot().ShouldBeEmpty("utilization was never measured, not measured-as-zero");
    }

    /// <summary>
    ///     Regression check on the ratio arithmetic once a snapshot has been published — not a
    ///     pin on boot behavior (see <see cref="BeforeAnySnapshotIsPublished_ObservableInstruments_YieldNoMeasurement" />
    ///     for that).
    /// </summary>
    [Fact]
    public void AfterASnapshotIsPublished_UtilizationReflectsOccupancy()
    {
        using var metrics = new PromotionQueueMetrics();
        using var collector = new MetricCollector<double>(metrics.Meter, OtlpNames.QueueCapacityUtilization);

        var stats = new PromotionQueueStats(800, null, new Dictionary<string, int> { ["acme"] = 800 });
        metrics.RecordSnapshot(stats, capacity: 1000);
        collector.RecordObservableInstruments();

        var measurement = collector.GetMeasurementSnapshot().ShouldHaveSingleItem();
        measurement.Value.ShouldBe(0.8, "queue is 80% full");
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
        using var collector = new MetricCollector<double>(metrics.Meter, OtlpNames.QueueCapacityUtilization);

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
    }
}
