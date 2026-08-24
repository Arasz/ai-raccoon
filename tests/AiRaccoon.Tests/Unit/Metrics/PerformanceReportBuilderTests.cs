using AiRaccoon.Core.Metrics;
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

    /// <summary>
    ///     Restated for the phase series (WP10): a name that is neither a tool nor a phase — not in
    ///     the derived series-name list at all — must still not invent a series.
    /// </summary>
    [Fact]
    public void Build_SamplesForAnUnknownName_AreIgnored()
    {
        var samples = new[] { new MetricSample("totally_unknown_name", 5, Now - TimeSpan.FromMinutes(1)) };

        var report = PerformanceReportBuilder.Build(["memory_write"], samples, Now,
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));

        report.Series.Single().Count.ShouldBe(0);
    }

    /// <summary>
    ///     WP10: the per-phase search timings (search.fts, search.vector, …) must appear as series
    ///     alongside the tool series, not be silently dropped for having a name that is not a tool.
    /// </summary>
    [Fact]
    public void Build_PhaseNamesGiven_AppearAsSeriesAlongsideTools()
    {
        var samples = new[]
        {
            new MetricSample("memory_search", 10, Now - TimeSpan.FromMinutes(1)),
            new MetricSample("search.fts", 3, Now - TimeSpan.FromMinutes(1)),
            new MetricSample("search.fts", 7, Now - TimeSpan.FromMinutes(2))
        };

        var report = PerformanceReportBuilder.Build(["memory_search"], samples, Now,
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), ["search.fts", "search.vector"]);

        report.Series.Select(s => s.Tool).ShouldBe(["memory_search", "search.fts", "search.vector"]);
        var fts = report.Series.Single(s => s.Tool == "search.fts");
        fts.Count.ShouldBe(2);
        fts.Max.ShouldBe(7.0);

        var neverRecorded = report.Series.Single(s => s.Tool == "search.vector");
        neverRecorded.Count.ShouldBe(0, "a phase never measured is still present, at count 0 — same derived-inventory contract as tools");
    }

    /// <summary>
    ///     Finding 3, resolved per owner ruling ("bound the bucket count, not the window" —
    ///     docs/work/specs/PerformanceMetrics.feature rules four weeks a best-effort limit, not a
    ///     guarantee): an agent-supplied window with no upper bound must not allocate
    ///     ceil(window/bucket) buckets per series unbounded — 525600 minutes (a year, the review's
    ///     windowMinutes: 525600) at the 1-minute default bucket would be ~18.9M bucket objects
    ///     across ~36 series. The window is honoured in full; the bucket widens instead, so the
    ///     per-series bucket count never exceeds <see cref="PerformanceReportBuilder.MaxBucketCount" />.
    /// </summary>
    [Fact]
    public void Build_ExtremeWindow_WidensTheBucketInsteadOfTruncatingTheWindow()
    {
        var window = TimeSpan.FromMinutes(525_600);

        var report = PerformanceReportBuilder.Build(["memory_write"], [], Now, window, TimeSpan.FromMinutes(1));

        report.Window.ShouldBe(window, "the window is never truncated — only the bucket widens");
        report.BucketCount.ShouldBeLessThanOrEqualTo(PerformanceReportBuilder.MaxBucketCount);
        report.Bucket.ShouldBeGreaterThan(TimeSpan.FromMinutes(1),
            "the requested 1-minute bucket must widen to keep the bucket count bounded");
        report.Series.Single().Buckets.Count.ShouldBe(report.BucketCount);
    }

    /// <summary>
    ///     A window that DOES fit within the cap at the requested bucket width must not be widened —
    ///     the cap only bites when it would otherwise be exceeded, matching the default-case gate
    ///     (<see cref="Build_DefaultWindowAndBucket_Yields180Buckets" />) at a different scale.
    /// </summary>
    [Fact]
    public void Build_WindowWithinCapAtRequestedBucket_BucketIsNotWidened()
    {
        var report = PerformanceReportBuilder.Build(["memory_write"], [], Now,
            TimeSpan.FromDays(1), TimeSpan.FromMinutes(1));

        report.Bucket.ShouldBe(TimeSpan.FromMinutes(1), "1440 buckets is under the cap, so the bucket stays as requested");
        report.BucketCount.ShouldBe(1440);
    }

    /// <summary>
    ///     Finding 7: MetricsReportService floors `now - window` to whole seconds for its SQL
    ///     boundary (the `metrics` table only stores whole-second recorded_at), so a sub-second
    ///     `now` lets a row from just before the true window start through. Build must exclude it
    ///     from both the series stats and every bucket — never let a row feed Count/P50 while
    ///     landing in no bucket.
    /// </summary>
    [Fact]
    public void Build_SubSecondNow_ExcludesASampleFromBeforeTheTrueWindowStart()
    {
        var subSecondNow = new DateTimeOffset(2026, 8, 15, 12, 0, 0, 700, TimeSpan.Zero);
        var window = TimeSpan.FromHours(1);
        var trueStart = subSecondNow - window; // 11:00:00.700
        // What a floor(from)-based SQL boundary would admit: the whole second below the true start.
        var flooredStart = new DateTimeOffset(trueStart.Year, trueStart.Month, trueStart.Day, trueStart.Hour,
            trueStart.Minute, trueStart.Second, TimeSpan.Zero); // 11:00:00.000
        var samples = new[] { new MetricSample("memory_write", 5, flooredStart) };

        var report = PerformanceReportBuilder.Build(["memory_write"], samples, subSecondNow, window, TimeSpan.FromMinutes(1));

        var series = report.Series.Single();
        series.Count.ShouldBe(0, "a sample from before the true sub-second window start must not feed Count");
        series.Buckets.Sum(b => b.Count).ShouldBe(series.Count, "sum(buckets[].Count) must equal series.Count");
    }

    /// <summary>Omitting phaseNames keeps the exact pre-WP10 behaviour: tool series only.</summary>
    [Fact]
    public void Build_NoPhaseNamesGiven_BehavesExactlyAsBeforePhaseSeriesExisted()
    {
        var samples = new[] { new MetricSample("search.fts", 3, Now - TimeSpan.FromMinutes(1)) };

        var report = PerformanceReportBuilder.Build(["memory_search"], samples, Now,
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1));

        report.Series.Single().Tool.ShouldBe("memory_search");
        report.Series.Single().Count.ShouldBe(0, "search.fts is not a tool and no phaseNames were given, so it must not appear");
    }
}
