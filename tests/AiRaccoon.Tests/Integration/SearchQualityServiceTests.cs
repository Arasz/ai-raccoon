using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

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

    [RetryFact]
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

    [RetryFact]
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

    [RetryFact]
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

    [RetryFact]
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

    [RetryFact]
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

    [RetryFact]
    public async Task GetMetrics_FollowThroughRate_CalculatesCorrectly()
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchAsync("c1", "q1", "all", "proj-a", null, 1, [], TestContext.Current.CancellationToken);
        await _sut.RecordSearchAsync("c2", "q2", "all", "proj-a", null, 1, [], TestContext.Current.CancellationToken);
        await _sut.RecordFollowThroughAsync("c1", "/file.md", TestContext.Current.CancellationToken);

        var metrics = await _sut.GetMetricsAsync("proj-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metrics.FollowThroughRate.ShouldBe(0.5);
    }

    [RetryFact]
    public async Task RecordSearch_WithNullProjectId_Succeeds()
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchAsync(
            "corr-null", "query", null, null, null,
            0, [], TestContext.Current.CancellationToken);

        var metrics = await _sut.GetMetricsAsync(null, DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metrics.TotalSearches.ShouldBe(1);
    }

    [RetryFact]
    public async Task GetMetrics_PooledSemantics_CountsAllRowsRegardlessOfScope()
    {
        // P5 audit pin (kind-blindness, deliberate): GetMetricsAsync aggregates with no
        // kind/dimension filter — every row in the project window counts. A future narrowing
        // (e.g. a kind predicate) must trip this test instead of shifting dashboards silently.
        // Split only on a named new consumer (none: zero production callers at audit time).
        await EnsureSchemaAsync();
        var ct = TestContext.Current.CancellationToken;

        await _sut.RecordSearchAsync("pool-1", "q1", "all", "proj-a", null, 2, ["/a.md"], ct);
        await _sut.RecordSearchAsync("pool-2", "q2", "project", "proj-a", null, 3, ["/b.md"], ct);
        await _sut.RecordSearchAsync("pool-3", "q3", "shared", "proj-a", null, 1, [], ct);
        await _sut.RecordSearchAsync("pool-4", "q4", null, "proj-a", null, 0, [], ct);
        await _sut.RecordSearchAsync("pool-5", "q5", "all", "proj-b", null, 9, ["/c.md"], ct);

        await _sut.RecordGradeAsync("proj-a", "pool-1", 5, null, ct);
        await _sut.RecordFollowThroughAsync("pool-2", "/b.md", ct);

        var metrics = await _sut.GetMetricsAsync("proj-a", DateTimeOffset.MinValue, ct);
        metrics.TotalSearches.ShouldBe(4);
        metrics.GradedSearches.ShouldBe(1);
        metrics.FollowThroughSearches.ShouldBe(1);
        metrics.AverageGrade.ShouldBe(5.0);
        metrics.FollowThroughRate.ShouldBe(0.25);
        metrics.Coverage.ShouldBe(0.25);

        var all = await _sut.GetMetricsAsync(null, DateTimeOffset.MinValue, ct);
        all.TotalSearches.ShouldBe(5);
    }

    [RetryFact]
    public async Task RecordGrade_CorrelationIdOnlyKeying_ProjectIdNotAPredicate()
    {
        // P5 audit pin (absence pin, explicitly accepted out of scope): grade updates key on
        // correlation_id alone — the projectId argument is accepted but is not a SQL predicate
        // (RecordGradeAsync binds Id/Grade/Note only; follow-through takes no projectId at all).
        // Ruling: accepted-out-of-scope, no live cross-project grade demonstrated (correlation ids
        // are unique and unguessable; the tool gate still enforces write access). Any future
        // re-scoping (changed forwarding) must trip this test for deliberate revisit.
        await EnsureSchemaAsync();
        var ct = TestContext.Current.CancellationToken;

        await _sut.RecordSearchAsync("key-1", "q1", "all", "proj-a", null, 2, ["/a.md"], ct);
        await _sut.RecordSearchAsync("key-2", "q2", "all", "proj-a", null, 3, ["/b.md"], ct);

        await _sut.RecordGradeAsync("proj-B-NOT-THE-ROW-PROJECT", "key-1", 4, "audit pin", ct);

        var metrics = await _sut.GetMetricsAsync("proj-a", DateTimeOffset.MinValue, ct);
        metrics.TotalSearches.ShouldBe(2);
        metrics.GradedSearches.ShouldBe(1);
        metrics.AverageGrade.ShouldBe(4.0);
        metrics.Coverage.ShouldBe(0.5);
    }
}
