using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Metrics;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Metrics;

/// <summary>
///     MetricsReportService's read path over the real bank: window/default resolution, and the
///     project-scope gate (docs/plans/2026-08-15-performance-metrics-implementation.md, WP6).
///     Seeds rows directly through <see cref="SqliteMetricsStore" /> — WP3's own writer — rather than
///     hand-rolled SQL, so a schema drift between the writer and this reader would show up here too.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MetricsReportServiceTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-metrics-report");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMetricsStore _metricsStore;
    private readonly MetricsReportService _service;

    public MetricsReportServiceTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _metricsStore = new SqliteMetricsStore(_factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteMetricsStore>.Instance);
        _service = new MetricsReportService(_factory, new FakeTimeProvider(FixedNow));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private Task SeedAsync(string name, double value, DateTimeOffset recordedAt, string? projectId) =>
        _metricsStore.SaveBatchAsync(
            [new Measurement(name, MeasurementKind.Histogram, value, "ms", recordedAt, projectId)],
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task GetReportAsync_NoWindowOrBucket_DefaultsToThreeHoursIn180Buckets()
    {
        var report = await _service.GetReportAsync("acme", ["memory_search"], window: null, bucket: null,
            TestContext.Current.CancellationToken);

        report.Window.ShouldBe(TimeSpan.FromHours(3));
        report.Bucket.ShouldBe(TimeSpan.FromMinutes(1));
        report.BucketCount.ShouldBe(180);
    }

    /// <summary>
    ///     WP10/S1 restates this: the report now always carries the eight search-phase series plus
    ///     the measured total alongside whatever tools are asked for, so a quiet bank's series count
    ///     is toolNames.Count plus SeriesNames.Count — every one of them still at count zero.
    /// </summary>
    [Fact]
    public async Task GetReportAsync_QuietBank_ReturnsEmptySeriesWithoutError()
    {
        var report = await _service.GetReportAsync("acme", ["memory_search", "memory_write"],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        report.Series.Count.ShouldBe(2 + SearchTimings.SeriesNames.Count);
        report.Series.ShouldAllBe(s => s.Count == 0);
    }

    /// <summary>Project-scope gate: a second project's rows must never appear in the caller's report.</summary>
    [Fact]
    public async Task GetReportAsync_TwoProjects_ReportShowsOnlyTheCallingProject()
    {
        await SeedAsync("memory_search", 10, FixedNow - TimeSpan.FromMinutes(5), "acme");
        await SeedAsync("memory_search", 20, FixedNow - TimeSpan.FromMinutes(4), "acme");
        await SeedAsync("memory_search", 999, FixedNow - TimeSpan.FromMinutes(3), "other-project");

        var report = await _service.GetReportAsync("acme", ["memory_search"],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        var series = report.Series.Single(s => s.Tool == "memory_search");
        series.Count.ShouldBe(2, "only acme's two rows belong to this report");
        series.Max.ShouldBe(20.0, "other-project's 999 must never leak in");
    }

    /// <summary>
    ///     Finding 9: seriesNames is toolNames + SearchTimings.SeriesNames, and SeriesNames is never
    ///     empty, so seriesNames can never be empty either — an empty toolNames list still goes
    ///     through the normal query path and still returns the phase-plus-total series, at count 0
    ///     on a quiet bank.
    /// </summary>
    [Fact]
    public async Task GetReportAsync_NoToolNamesGiven_StillReturnsThePhaseSeries()
    {
        var report = await _service.GetReportAsync("acme", [], TimeSpan.FromHours(1), TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);

        report.Series.Select(s => s.Tool).ShouldBe(SearchTimings.SeriesNames, ignoreOrder: true);
        report.Series.ShouldAllBe(s => s.Count == 0);
    }

    [Fact]
    public async Task GetReportAsync_SampleOutsideTheWindow_IsExcluded()
    {
        await SeedAsync("memory_search", 10, FixedNow - TimeSpan.FromHours(2), "acme");

        var report = await _service.GetReportAsync("acme", ["memory_search"],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        report.Series.Single(s => s.Tool == "memory_search").Count.ShouldBe(0);
    }

    /// <summary>
    ///     Spec scenario 1: "a three-week-old bank still answers for its oldest measurement" — 21
    ///     days is inside the 28-day retention default AND exactly at PerformanceReportBuilder's
    ///     MaxWindow, so a 28-day window must cover it. Nothing before this seeded beyond one hour.
    /// </summary>
    [Fact]
    public async Task GetReportAsync_OldestMeasurementIs21DaysOld_WindowOf28Days_ReportCoversIt()
    {
        await SeedAsync("memory_search", 42, FixedNow - TimeSpan.FromDays(21), "acme");

        var report = await _service.GetReportAsync("acme", ["memory_search"],
            TimeSpan.FromDays(28), TimeSpan.FromDays(28), TestContext.Current.CancellationToken);

        report.Series.Single(s => s.Tool == "memory_search").Count.ShouldBe(1, "a 21-day-old row is inside a 28-day window");
    }

    /// <summary>
    ///     Spec scenario 2: "a bank holding more than four weeks is within contract, not over it" —
    ///     the .feature is explicit that four weeks is a BEST-EFFORT limit, not a guarantee (stage 04
    ///     amendment). This used to conflict with a landed review fix that hard-clamped every report's
    ///     WINDOW to <see cref="MetricsConfigKeys.DefaultRetentionDays" /> (28 days) to bound
    ///     allocation. Owner ruling: bound the bucket count, not the window
    ///     (<see cref="PerformanceReportBuilder.MaxBucketCount" />) — a wide window widens its bucket
    ///     instead of being truncated, so the whole window is covered and allocation still bounded.
    ///     RED evidence (captured before the ruling, window-clamp still in place): seeding rows at 40,
    ///     20 and 1 days old and asking for a 40-day window returned Count == 2, not 3 — the 40-day-old
    ///     row was discarded by the window clamp.
    /// </summary>
    [Fact]
    public async Task GetReportAsync_BankHolding40DaysOfMeasurements_WindowOf40Days_ReportCoversAll40Days()
    {
        await SeedAsync("memory_search", 1, FixedNow - TimeSpan.FromDays(40), "acme");
        await SeedAsync("memory_search", 2, FixedNow - TimeSpan.FromDays(20), "acme");
        await SeedAsync("memory_search", 3, FixedNow - TimeSpan.FromDays(1), "acme");

        var report = await _service.GetReportAsync("acme", ["memory_search"],
            TimeSpan.FromDays(40), TimeSpan.FromDays(40), TestContext.Current.CancellationToken);

        report.Series.Single(s => s.Tool == "memory_search").Count.ShouldBe(3,
            "a bank holding more than four weeks is within contract, not over it — the four-week limit is best effort");
    }

    /// <summary>
    ///     Finding 5: self-metrics (flush duration/batch size, drop count) are bank-wide, not
    ///     project-scoped, so they must not appear in an ordinary project's report — but they must
    ///     be readable from *somewhere*, or a drop count can never surface as a number.
    /// </summary>
    [Fact]
    public async Task GetReportAsync_OrdinaryProject_NeverSeesSelfMetrics()
    {
        await SeedAsync("metrics.dropped", 7, FixedNow - TimeSpan.FromMinutes(1), MetricsConfigKeys.SelfMetricsProjectId);

        var report = await _service.GetReportAsync("acme", ["memory_search"],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        report.Series.ShouldNotContain(s => s.Tool == "metrics.dropped",
            "self-metrics are bank-wide, not this project's — an ordinary report must not be polluted by them");
    }

    /// <summary>The self-metrics sentinel project id is the one surface that can show the drop count.</summary>
    [Fact]
    public async Task GetReportAsync_TheSelfMetricsProjectId_SurfacesTheDropCount()
    {
        await SeedAsync("metrics.dropped", 7, FixedNow - TimeSpan.FromMinutes(1), MetricsConfigKeys.SelfMetricsProjectId);

        var report = await _service.GetReportAsync(MetricsConfigKeys.SelfMetricsProjectId, [],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        var dropped = report.Series.Single(s => s.Tool == "metrics.dropped");
        dropped.Count.ShouldBe(1);
        dropped.Max.ShouldBe(7.0);
    }

    /// <summary>
    ///     WP10: the report carries the search-phase series alongside the tool series, reading them
    ///     back from the same `metrics` table WP3's writer fills — the phase names must not be
    ///     filtered out of the SQL query the same way a hand-written tool-only list would drop them.
    /// </summary>
    [Fact]
    public async Task GetReportAsync_PhaseMeasurements_AppearAsSeriesAlongsideTools()
    {
        await SeedAsync("memory_search", 10, FixedNow - TimeSpan.FromMinutes(5), "acme");
        await SeedAsync("search.fts", 3, FixedNow - TimeSpan.FromMinutes(4), "acme");
        await SeedAsync("search.fts", 7, FixedNow - TimeSpan.FromMinutes(3), "acme");

        var report = await _service.GetReportAsync("acme", ["memory_search"],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        report.Series.ShouldContain(s => s.Tool == "memory_search" && s.Count == 1);
        var fts = report.Series.Single(s => s.Tool == "search.fts");
        fts.Count.ShouldBe(2);
        fts.Max.ShouldBe(7.0);

        report.Series.ShouldContain(s => s.Tool == "search.vector" && s.Count == 0,
            "a phase never measured is still present, at count 0 — same derived-inventory contract as tools");
    }
}
