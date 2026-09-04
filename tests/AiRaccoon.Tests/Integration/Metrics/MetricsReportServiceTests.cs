using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Metrics;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

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

    [RetryFact]
    public async Task GetReportAsync_NoWindowOrBucket_DefaultsToThreeHoursIn180Buckets()
    {
        var report = await _service.GetReportAsync("acme", ["memory_search"], window: null, bucket: null,
            TestContext.Current.CancellationToken);

        report.Window.ShouldBe(TimeSpan.FromHours(3));
        report.Bucket.ShouldBe(TimeSpan.FromMinutes(1));
        report.BucketCount.ShouldBe(180);
    }

    /// <summary>
    ///     WP10/S1 restates this: the report now always carries the search-phase series plus
    ///     the measured total alongside whatever tools are asked for, so a quiet bank's series count
    ///     is toolNames.Count plus SeriesNames.Count — every one of them still at count zero.
    /// </summary>
    [RetryFact]
    public async Task GetReportAsync_QuietBank_ReturnsEmptySeriesWithoutError()
    {
        var report = await _service.GetReportAsync("acme", ["memory_search", "memory_write"],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        report.Series.Count.ShouldBe(2 + SearchTimings.SeriesNames.Count);
        report.Series.ShouldAllBe(s => s.Count == 0);
    }

    /// <summary>Project-scope gate: a second project's rows must never appear in the caller's report.</summary>
    [RetryFact]
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
    [RetryFact]
    public async Task GetReportAsync_NoToolNamesGiven_StillReturnsThePhaseSeries()
    {
        var report = await _service.GetReportAsync("acme", [], TimeSpan.FromHours(1), TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken);

        report.Series.Select(s => s.Tool).ShouldBe(SearchTimings.SeriesNames, ignoreOrder: true);
        report.Series.ShouldAllBe(s => s.Count == 0);
    }

    [RetryFact]
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
    [RetryFact]
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
    [RetryFact]
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
    [RetryFact]
    public async Task GetReportAsync_OrdinaryProject_NeverSeesSelfMetrics()
    {
        await SeedAsync("metrics.dropped", 7, FixedNow - TimeSpan.FromMinutes(1), MetricsConfigKeys.SelfMetricsProjectId);

        var report = await _service.GetReportAsync("acme", ["memory_search"],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        report.Series.ShouldNotContain(s => s.Tool == "metrics.dropped",
            "self-metrics are bank-wide, not this project's — an ordinary report must not be polluted by them");
    }

    /// <summary>The self-metrics sentinel project id is the one surface that can show the drop count.</summary>
    [RetryFact]
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
    ///     Review B2 (#477): job.* series are dynamic (one per maintenance job, defined in the
    ///     Infrastructure layer this project cannot reference) — discovered by prefix from what is
    ///     actually recorded, not a hand-maintained name list (derive-or-delete).
    /// </summary>
    [RetryFact]
    public async Task GetReportAsync_TheSelfMetricsProjectId_SurfacesJobSeries()
    {
        await SeedAsync("job.pending-embed.duration_ms", 42, FixedNow - TimeSpan.FromMinutes(1), MetricsConfigKeys.SelfMetricsProjectId);
        await SeedAsync("job.code-reindex.rows", 5, FixedNow - TimeSpan.FromMinutes(1), MetricsConfigKeys.SelfMetricsProjectId);

        var report = await _service.GetReportAsync(MetricsConfigKeys.SelfMetricsProjectId, [],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        var duration = report.Series.Single(s => s.Tool == "job.pending-embed.duration_ms");
        duration.Count.ShouldBe(1);
        duration.Max.ShouldBe(42.0);
        var rows = report.Series.Single(s => s.Tool == "job.code-reindex.rows");
        rows.Count.ShouldBe(1);
        rows.Max.ShouldBe(5.0);
    }

    /// <summary>Mirrors the self-metrics isolation gate (finding 5): a bank-wide job series must never leak into an ordinary project's report either.</summary>
    [RetryFact]
    public async Task GetReportAsync_OrdinaryProject_NeverSeesJobSeries()
    {
        await SeedAsync("job.pending-embed.duration_ms", 42, FixedNow - TimeSpan.FromMinutes(1), MetricsConfigKeys.SelfMetricsProjectId);

        var report = await _service.GetReportAsync("acme", ["memory_search"],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        report.Series.ShouldNotContain(s => s.Tool == "job.pending-embed.duration_ms");
    }

    /// <summary>
    ///     WP11 (log-values-as-metrics): drain.&lt;corpus&gt;.* is bank-wide, discovered the same way
    ///     job.* is — MetricsConfigKeys.InternalSeriesPrefixes now covers "drain." too.
    /// </summary>
    [RetryFact]
    public async Task GetReportAsync_TheSelfMetricsProjectId_SurfacesDrainSeries()
    {
        await SeedAsync("drain.code.rows", 12, FixedNow - TimeSpan.FromMinutes(1), MetricsConfigKeys.SelfMetricsProjectId);

        var report = await _service.GetReportAsync(MetricsConfigKeys.SelfMetricsProjectId, [],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        var rows = report.Series.Single(s => s.Tool == "drain.code.rows");
        rows.Count.ShouldBe(1);
        rows.Max.ShouldBe(12.0);
    }

    /// <summary>Mirrors the job-series isolation gate: a bank-wide drain series must never leak into an ordinary project's report.</summary>
    [RetryFact]
    public async Task GetReportAsync_OrdinaryProject_NeverSeesDrainSeries()
    {
        await SeedAsync("drain.code.rows", 12, FixedNow - TimeSpan.FromMinutes(1), MetricsConfigKeys.SelfMetricsProjectId);

        var report = await _service.GetReportAsync("acme", ["memory_search"],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        report.Series.ShouldNotContain(s => s.Tool == "drain.code.rows");
    }

    /// <summary>
    ///     WP11/WP12: write.replace.* is recorded under the writing project's own id (ReplaceCoreAsync
    ///     already has it), so it must surface through the SAME prefix-discovery mechanism scoped to
    ///     an ORDINARY project — not just the self-metrics id. Seeds the actual wait_ms/held_ms series
    ///     names (WP12 split the WP11 combined lock_ms in two), not a stand-in example name — a
    ///     rename here must fail this test, not just the prefix-discovery mechanism in the abstract.
    /// </summary>
    [RetryFact]
    public async Task GetReportAsync_OrdinaryProject_SurfacesWriteReplaceSeries()
    {
        await SeedAsync("write.replace.wait_ms", 3, FixedNow - TimeSpan.FromMinutes(1), "acme");
        await SeedAsync("write.replace.held_ms", 5, FixedNow - TimeSpan.FromMinutes(1), "acme");

        var report = await _service.GetReportAsync("acme", [],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        var waitMs = report.Series.Single(s => s.Tool == "write.replace.wait_ms");
        waitMs.Count.ShouldBe(1);
        waitMs.Max.ShouldBe(3.0);
        var heldMs = report.Series.Single(s => s.Tool == "write.replace.held_ms");
        heldMs.Count.ShouldBe(1);
        heldMs.Max.ShouldBe(5.0);
    }

    /// <summary>
    ///     #601 fusion signals are recorded under the searching project's own id (MemoryTools.RecordSearchMeasurements
    ///     already has it), so they must surface through the SAME prefix-discovery mechanism scoped to
    ///     an ORDINARY project. Seeds the actual FusionStats.MetricNames, not stand-in names — a
    ///     rename must fail this test, not just the prefix mechanism in the abstract. Red-proof:
    ///     without "search.fusion." in InternalSeriesPrefixes the fusion rows stay write-only.
    /// </summary>
    [RetryFact]
    public async Task GetReportAsync_OrdinaryProject_SurfacesFusionSignalSeries()
    {
        await SeedAsync("search.fusion.top_strength", 0.95, FixedNow - TimeSpan.FromMinutes(1), "acme");
        await SeedAsync("search.fusion.top_margin", 0.2, FixedNow - TimeSpan.FromMinutes(1), "acme");
        await SeedAsync("search.fusion.legs_fired", 3, FixedNow - TimeSpan.FromMinutes(1), "acme");

        var report = await _service.GetReportAsync("acme", [],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        var strength = report.Series.Single(s => s.Tool == "search.fusion.top_strength");
        strength.Count.ShouldBe(1);
        strength.Max.ShouldBe(0.95);
        var margin = report.Series.Single(s => s.Tool == "search.fusion.top_margin");
        margin.Count.ShouldBe(1);
        margin.Max.ShouldBe(0.2);
        var legs = report.Series.Single(s => s.Tool == "search.fusion.legs_fired");
        legs.Count.ShouldBe(1);
        legs.Max.ShouldBe(3.0);
    }

    /// <summary>WP11: search.query.truncated_tokens is bank-wide (the embedding engine's query-trim path has no project id), so it discovers the same way job./drain. do.</summary>
    [RetryFact]
    public async Task GetReportAsync_TheSelfMetricsProjectId_SurfacesQueryTruncationSeries()
    {
        await SeedAsync("search.query.truncated_tokens", 30, FixedNow - TimeSpan.FromMinutes(1), MetricsConfigKeys.SelfMetricsProjectId);

        var report = await _service.GetReportAsync(MetricsConfigKeys.SelfMetricsProjectId, [],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        var truncated = report.Series.Single(s => s.Tool == "search.query.truncated_tokens");
        truncated.Count.ShouldBe(1);
        truncated.Max.ShouldBe(30.0);
    }

    /// <summary>
    ///     WP10: the report carries the search-phase series alongside the tool series, reading them
    ///     back from the same `metrics` table WP3's writer fills — the phase names must not be
    ///     filtered out of the SQL query the same way a hand-written tool-only list would drop them.
    /// </summary>
    [RetryFact]
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
