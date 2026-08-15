using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Metrics;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Metrics;

/// <summary>
///     Pure window/bucket aggregation (WP6, docs/plans/2026-08-15-performance-metrics-implementation.md).
///     No I/O: <see cref="PerformanceReportBuilder.Build" /> takes samples and the derived tool
///     inventory as plain data, so every scenario here runs without SQLite.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PerformanceReportBuilderTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Build_DefaultWindowAndBucket_Yields180Buckets()
    {
        var report = PerformanceReportBuilder.Build(["memory_write"], [], Now,
            TimeSpan.FromHours(3), TimeSpan.FromMinutes(1));

        report.Window.ShouldBe(TimeSpan.FromHours(3));
        report.Bucket.ShouldBe(TimeSpan.FromMinutes(1));
        report.BucketCount.ShouldBe(180);
        report.Series.Single().Buckets.Count.ShouldBe(180);
    }

    /// <summary>Clamp gate: a bucket wider than the window must not error — it clamps to the window.</summary>
    [Fact]
    public void Build_BucketWiderThanWindow_ClampsToOneAveragedPoint()
    {
        var samples = new[]
        {
            new MetricSample("memory_write", 10, Now - TimeSpan.FromMinutes(50)),
            new MetricSample("memory_write", 20, Now - TimeSpan.FromMinutes(10))
        };

        var report = PerformanceReportBuilder.Build(["memory_write"], samples, Now,
            TimeSpan.FromHours(1), TimeSpan.FromHours(2));

        report.Bucket.ShouldBe(TimeSpan.FromHours(1), "the bucket clamps down to the window, never the reverse");
        report.BucketCount.ShouldBe(1);
        var series = report.Series.Single();
        series.Buckets.Count.ShouldBe(1);
        series.Buckets[0].Count.ShouldBe(2);
        series.Buckets[0].Average.ShouldBe(15.0, "the single bucket averages every sample across the whole hour");
    }

    /// <summary>Derived-inventory gate: a tool with zero samples still gets a series, not an omitted key.</summary>
    [Fact]
    public void Build_ToolNeverCalled_AppearsWithZeroCountSeries()
    {
        var samples = new[] { new MetricSample("memory_write", 5, Now - TimeSpan.FromMinutes(1)) };

        var report = PerformanceReportBuilder.Build(["memory_write", "memory_search"], samples, Now,
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(30));

        report.Series.Count.ShouldBe(2, "the series list is derived from the tool inventory, not from what has data");
        var neverCalled = report.Series.Single(s => s.Tool == "memory_search");
        neverCalled.Count.ShouldBe(0);
        neverCalled.P50.ShouldBeNull();
        neverCalled.P95.ShouldBeNull();
        neverCalled.P99.ShouldBeNull();
        neverCalled.Min.ShouldBeNull();
        neverCalled.Max.ShouldBeNull();
        neverCalled.Buckets.Count.ShouldBe(2);
        neverCalled.Buckets.ShouldAllBe(b => b.Count == 0 && b.Average == null);
    }

    [Fact]
    public void Build_EmptySampleSet_EveryToolIsAnEmptySeries_NotAnError()
    {
        var report = PerformanceReportBuilder.Build(["memory_write", "memory_search"], [], Now,
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));

        report.Series.Count.ShouldBe(2);
        report.Series.ShouldAllBe(s => s.Count == 0);
    }

    /// <summary>
    ///     Percentiles must come from Core.Metrics.Statistics (WP0, G2), not a second implementation.
    ///     Same 21-sample hand-computed expectations as StatisticsTests.
    /// </summary>
    [Fact]
    public void Build_PerSeriesStatistics_MatchHandComputedNearestRank()
    {
        var samples = Enumerable.Range(1, 21)
            .Select(i => new MetricSample("memory_write", i, Now - TimeSpan.FromMinutes(i)))
            .ToArray();

        var report = PerformanceReportBuilder.Build(["memory_write"], samples, Now,
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(30));

        var series = report.Series.Single();
        series.Count.ShouldBe(21);
        series.P50.ShouldBe(11.0);
        series.P95.ShouldBe(20.0);
        series.P99.ShouldBe(21.0);
        series.Min.ShouldBe(1.0);
        series.Max.ShouldBe(21.0);
    }

    [Fact]
    public void Build_SamplesForAnUnlistedTool_AreIgnored()
    {
        var samples = new[] { new MetricSample("some_other_tool", 5, Now - TimeSpan.FromMinutes(1)) };

        var report = PerformanceReportBuilder.Build(["memory_write"], samples, Now,
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));

        report.Series.Single().Count.ShouldBe(0);
    }
}
