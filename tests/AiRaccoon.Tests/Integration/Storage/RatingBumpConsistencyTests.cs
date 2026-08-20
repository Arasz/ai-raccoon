using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Rating;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     `rating` must always be the rating of the `access_count` stored beside it. It was computed in
///     C# from a value read in an earlier round trip, so concurrent hits on one hash raced: the
///     counter is a relative SQL expression and survives, the rating is a literal and loses
///     (2026-08-14 project-scope review, data-access F7). `rating` is the sweep's deletion input.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class RatingBumpConsistencyTests : IDisposable
{
    private const string ProjectId = "rating-bump";
    private static readonly DateTimeOffset Now = new(2026, 2, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-rating-bump");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _store;

    public RatingBumpConsistencyTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), TestData.RealMarkdownChunker(),
            new FakeTimeProvider(Now), TestData.CreateEmbeddingService());
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task ConcurrentSearches_LeaveRatingConsistentWithTheStoredAccessCount()
    {
        await _store.AddContentAsync(ProjectId, "/notes/hot.md", "the rating bump consistency probe",
            ContextNaming.ProjectContext(ProjectId), cancellationToken: TestContext.Current.CancellationToken);

        // Task.Run, not a bare Select: Microsoft.Data.Sqlite has no true async I/O, so Dapper's
        // ExecuteAsync completes synchronously and Task.WhenAll over a Select runs the searches one
        // after another. Without real thread-pool parallelism this test passes against the defect.
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() => _store.SearchAsync(
            new SearchQuery(ProjectId, "rating bump consistency probe"), TestContext.Current.CancellationToken))));

        var (accessCount, rating, createdAt) = await RowAsync();

        accessCount.ShouldBeGreaterThan(0, "the searches must have hit the row");
        var ageDays = Math.Max(0, (Now.ToUnixTimeSeconds() - createdAt) / 86_400.0);
        var expected = RatingPolicy.Rating(
            RatingPolicy.DefaultBaseScore, accessCount, ageDays, RatingPolicy.DefaultHalfLifeDays);

        rating.ShouldBe(expected, 1e-9,
            $"rating {rating} does not match the rating of the stored access_count {accessCount} " +
            $"({expected}); a bump's rating contribution was lost while its count survived");
    }

    [Fact]
    public async Task SequentialSearches_AlsoLeaveRatingConsistent()
    {
        // The uncontended path must keep the same invariant — the fix must not trade a race for a
        // formula that is merely wrong more quietly.
        await _store.AddContentAsync(ProjectId, "/notes/warm.md", "the sequential rating probe",
            ContextNaming.ProjectContext(ProjectId), cancellationToken: TestContext.Current.CancellationToken);

        for (var i = 0; i < 3; i++)
        {
            await _store.SearchAsync(new SearchQuery(ProjectId, "sequential rating probe"),
                TestContext.Current.CancellationToken);
        }

        var (accessCount, rating, createdAt) = await RowAsync();
        var ageDays = Math.Max(0, (Now.ToUnixTimeSeconds() - createdAt) / 86_400.0);

        rating.ShouldBe(RatingPolicy.Rating(
            RatingPolicy.DefaultBaseScore, accessCount, ageDays, RatingPolicy.DefaultHalfLifeDays), 1e-9);
    }

    private async Task<(int AccessCount, double Rating, long CreatedAt)> RowAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var row = await connection.QueryFirstAsync<(int, double, long)>(
            "SELECT access_count, rating, created_at FROM entries ORDER BY access_count DESC LIMIT 1");
        return row;
    }
}
