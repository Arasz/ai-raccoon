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

    [Fact]
    public async Task GetReportAsync_QuietBank_ReturnsEmptySeriesWithoutError()
    {
        var report = await _service.GetReportAsync("acme", ["memory_search", "memory_write"],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        report.Series.Count.ShouldBe(2);
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

        var series = report.Series.Single();
        series.Count.ShouldBe(2, "only acme's two rows belong to this report");
        series.Max.ShouldBe(20.0, "other-project's 999 must never leak in");
    }

    [Fact]
    public async Task GetReportAsync_SampleOutsideTheWindow_IsExcluded()
    {
        await SeedAsync("memory_search", 10, FixedNow - TimeSpan.FromHours(2), "acme");

        var report = await _service.GetReportAsync("acme", ["memory_search"],
            TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

        report.Series.Single().Count.ShouldBe(0);
    }
}
