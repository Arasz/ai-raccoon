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
    ///     WP10 restates this: the report now always carries the six search-phase series alongside
    ///     whatever tools are asked for, so a quiet bank's series count is toolNames.Count plus the
    ///     six phases — every one of them still at count zero.
    /// </summary>
    [Fact]
    public async Task GetReportAsync_QuietBank_ReturnsEmptySeriesWithoutError()
    {
        var report = await _service.GetReportAsync("acme", ["memory_search", "memory_write"],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        report.Series.Count.ShouldBe(2 + SearchTimings.PhaseNames.Count);
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

    [Fact]
    public async Task GetReportAsync_SampleOutsideTheWindow_IsExcluded()
    {
        await SeedAsync("memory_search", 10, FixedNow - TimeSpan.FromHours(2), "acme");

        var report = await _service.GetReportAsync("acme", ["memory_search"],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        report.Series.Single(s => s.Tool == "memory_search").Count.ShouldBe(0);
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
