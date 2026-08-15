using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     Integration tests for <see cref="SqliteSearchQualityService"/> against a real temp bank.
///     See docs/plans/2026-08-11-search-quality-metric-plan.md WP3.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SearchQualityServiceTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot();
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteSearchQualityService _sut;

    public SearchQualityServiceTests()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _sut = new SqliteSearchQualityService(_factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteSearchQualityService>.Instance);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private async Task EnsureSchemaAsync()
    {
        await using var conn = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await MemorySchema.EnsureAsync(conn, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RecordSearch_CreatesRow_GetMetricsReturnsOne()
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchAsync(
            "corr-001", "test query", "all", "proj-a", "session-1",
            5, ["/path/a.md", "/path/b.md"], TestContext.Current.CancellationToken);

        var metrics = await _sut.GetMetricsAsync("proj-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metrics.TotalSearches.ShouldBe(1);
        metrics.FollowThroughSearches.ShouldBe(0);
        metrics.GradedSearches.ShouldBe(0);
    }

    [Fact]
    public async Task RecordFollowThrough_UpdatesRow_CountIncrements()
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchAsync(
            "corr-002", "query", "all", "proj-a", null,
            3, ["/file.md"], TestContext.Current.CancellationToken);

        await _sut.RecordFollowThroughAsync("corr-002", "/file.md", TestContext.Current.CancellationToken);
        await _sut.RecordFollowThroughAsync("corr-002", "/other.md", TestContext.Current.CancellationToken);

        var metrics = await _sut.GetMetricsAsync("proj-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metrics.FollowThroughSearches.ShouldBe(1);
    }

    [Fact]
    public async Task RecordFollowThrough_DuplicateFile_CountsOnce()
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchAsync(
            "corr-003", "query", "all", "proj-a", null,
            1, ["/file.md"], TestContext.Current.CancellationToken);

        await _sut.RecordFollowThroughAsync("corr-003", "/file.md", TestContext.Current.CancellationToken);
        await _sut.RecordFollowThroughAsync("corr-003", "/file.md", TestContext.Current.CancellationToken);

        var metrics = await _sut.GetMetricsAsync("proj-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metrics.FollowThroughSearches.ShouldBe(1);
    }

    [Fact]
    public async Task RecordGrade_UpdatesRow_MetricsReflectGrade()
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchAsync(
            "corr-004", "query", "all", "proj-a", null,
            5, ["/file.md"], TestContext.Current.CancellationToken);

        await _sut.RecordGradeAsync("proj-a", "corr-004", 5, "great result", TestContext.Current.CancellationToken);

        var metrics = await _sut.GetMetricsAsync("proj-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metrics.GradedSearches.ShouldBe(1);
        metrics.AverageGrade.ShouldBe(5.0);
        metrics.Coverage.ShouldBe(1.0);
    }

    [Fact]
    public async Task GetMetrics_ProjectFilter_WorksCorrectly()
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchAsync("c1", "q1", "all", "proj-a", null, 1, [], TestContext.Current.CancellationToken);
        await _sut.RecordSearchAsync("c2", "q2", "all", "proj-b", null, 1, [], TestContext.Current.CancellationToken);

        var metricsA = await _sut.GetMetricsAsync("proj-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metricsA.TotalSearches.ShouldBe(1);

        var metricsAll = await _sut.GetMetricsAsync(null, DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metricsAll.TotalSearches.ShouldBe(2);
    }

    [Fact]
    public async Task GetMetrics_FollowThroughRate_CalculatesCorrectly()
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchAsync("c1", "q1", "all", "proj-a", null, 1, [], TestContext.Current.CancellationToken);
        await _sut.RecordSearchAsync("c2", "q2", "all", "proj-a", null, 1, [], TestContext.Current.CancellationToken);
        await _sut.RecordFollowThroughAsync("c1", "/file.md", TestContext.Current.CancellationToken);

        var metrics = await _sut.GetMetricsAsync("proj-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metrics.FollowThroughRate.ShouldBe(0.5);
    }

    [Fact]
    public async Task RecordSearch_WithNullProjectId_Succeeds()
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchAsync(
            "corr-null", "query", null, null, null,
            0, [], TestContext.Current.CancellationToken);

        var metrics = await _sut.GetMetricsAsync(null, DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metrics.TotalSearches.ShouldBe(1);
    }
}
