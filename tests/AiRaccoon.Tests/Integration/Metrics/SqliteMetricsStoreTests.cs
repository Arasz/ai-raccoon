using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Metrics;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Metrics;

/// <summary>
///     SqliteMetricsStore's save path: batch insert, unix-seconds timestamps (matching the bank's
///     convention — SqliteMemoryStore.cs:63,80,496), and the save-time query-identity allowlist
///     (docs/plans/2026-08-15-performance-metrics-implementation.md, WP3 AC6, spec scenario 25).
///     The allowlist is exactly {query_hash, correlation_id}; a measurement whose Tags carry query
///     text is rejected on save, and fails closed on anything it cannot verify as safe.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMetricsStoreTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-metrics-store");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMetricsStore _store;

    public SqliteMetricsStoreTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = new SqliteMetricsStore(_factory, NullLogger<SqliteMetricsStore>.Instance);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private async Task<int> CountRowsAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM metrics");
    }

    private static readonly string ValidQueryHash = new('a', 64);
    private static readonly string ValidCorrelationId = new('b', 32);

    [Fact]
    public async Task SaveBatchAsync_ValidMeasurements_PersistsEveryColumn()
    {
        var measurement = new Measurement("search.fts", MeasurementKind.Histogram, 12.5, "ms", FixedNow,
            ProjectId: "acme", QueryHash: ValidQueryHash, CorrelationId: ValidCorrelationId, Tags: """{"phase":"fts"}""");

        await _store.SaveBatchAsync([measurement], TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var row = await connection.QuerySingleAsync(
            "SELECT name, kind, value, unit, project_id, query_hash, correlation_id, tags, recorded_at FROM metrics");

        ((string)row.name).ShouldBe("search.fts");
        ((string)row.kind).ShouldBe("Histogram");
        ((double)row.value).ShouldBe(12.5);
        ((string)row.unit).ShouldBe("ms");
        ((string)row.project_id).ShouldBe("acme");
        ((string)row.query_hash).ShouldBe(ValidQueryHash);
        ((string)row.correlation_id).ShouldBe(ValidCorrelationId);
        ((string)row.tags).ShouldBe("""{"phase":"fts"}""");
        ((long)row.recorded_at).ShouldBe(FixedNow.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task SaveBatchAsync_EmptyBatch_IsANoOp()
    {
        await _store.SaveBatchAsync([], TestContext.Current.CancellationToken);

        (await CountRowsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task SaveBatchAsync_MultipleMeasurements_PersistsAll()
    {
        var batch = Enumerable.Range(0, 5)
            .Select(i => new Measurement($"metric.{i}", MeasurementKind.Counter, i, "count", FixedNow))
            .ToList();

        await _store.SaveBatchAsync(batch, TestContext.Current.CancellationToken);

        (await CountRowsAsync()).ShouldBe(5);
    }

    [Fact]
    public async Task SaveBatchAsync_MeasurementCarryingQueryTextInTags_IsRejected()
    {
        var carriesQueryText = new Measurement("search.fts", MeasurementKind.Histogram, 1, "ms", FixedNow,
            Tags: """{"query":"raw user query text"}""");

        await _store.SaveBatchAsync([carriesQueryText], TestContext.Current.CancellationToken);

        (await CountRowsAsync()).ShouldBe(0, "a measurement carrying query text must be rejected on save");
    }

    [Theory]
    [InlineData("""{"queryText":"the raw text"}""")]
    [InlineData("""{"search_query":"the raw text"}""")]
    [InlineData("""{"q":"the raw text"}""")]
    [InlineData("""{"text":"the raw text"}""")]
    public async Task SaveBatchAsync_AnyForbiddenTagKey_IsRejected(string tags)
    {
        var measurement = new Measurement("search.fts", MeasurementKind.Histogram, 1, "ms", FixedNow, Tags: tags);

        await _store.SaveBatchAsync([measurement], TestContext.Current.CancellationToken);

        (await CountRowsAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task SaveBatchAsync_MalformedTags_FailsClosed()
    {
        var measurement = new Measurement("search.fts", MeasurementKind.Histogram, 1, "ms", FixedNow,
            Tags: "not valid json");

        await _store.SaveBatchAsync([measurement], TestContext.Current.CancellationToken);

        (await CountRowsAsync()).ShouldBe(0, "tags that cannot be verified safe must be rejected, not admitted");
    }

    [Fact]
    public async Task SaveBatchAsync_QueryIdentityIsExactlyHashAndCorrelationId_Allowed()
    {
        var measurement = new Measurement("search.fts", MeasurementKind.Histogram, 1, "ms", FixedNow,
            QueryHash: ValidQueryHash, CorrelationId: ValidCorrelationId);

        await _store.SaveBatchAsync([measurement], TestContext.Current.CancellationToken);

        (await CountRowsAsync()).ShouldBe(1, "hash + correlation id are the allowed query-identity fields");
    }

    /// <summary>Denylist-by-key would miss this: "prompt" was never on the old forbidden-key list.</summary>
    [Fact]
    public async Task SaveBatchAsync_TagsKeyNotOnTheAllowlist_IsRejected()
    {
        var measurement = new Measurement("search.fts", MeasurementKind.Histogram, 1, "ms", FixedNow,
            Tags: """{"prompt":"how do I rotate the prod DB password"}""");

        await _store.SaveBatchAsync([measurement], TestContext.Current.CancellationToken);

        (await CountRowsAsync()).ShouldBe(0, "an unlisted Tags key must be rejected, not merely a hand-picked few");
    }

    /// <summary>A key-only check would miss this: the value under an allowed key can still carry raw query text.</summary>
    [Fact]
    public async Task SaveBatchAsync_AllowedTagKeyWithAnUnrecognisedValue_IsRejected()
    {
        var measurement = new Measurement("search.fts", MeasurementKind.Histogram, 1, "ms", FixedNow,
            Tags: """{"phase":"how do I rotate the prod DB password"}""");

        await _store.SaveBatchAsync([measurement], TestContext.Current.CancellationToken);

        (await CountRowsAsync()).ShouldBe(0, "phase's value must be one of the known phase suffixes, not free text");
    }

    [Fact]
    public async Task SaveBatchAsync_QueryHashNotShapedLikeAHash_IsRejected()
    {
        var measurement = new Measurement("search.fts", MeasurementKind.Histogram, 1, "ms", FixedNow,
            QueryHash: "how do I rotate the prod DB password");

        await _store.SaveBatchAsync([measurement], TestContext.Current.CancellationToken);

        (await CountRowsAsync()).ShouldBe(0, "QueryHash must be a SHA-256 hex digest, not arbitrary text");
    }

    [Fact]
    public async Task SaveBatchAsync_CorrelationIdNotShapedLikeAnId_IsRejected()
    {
        var measurement = new Measurement("search.fts", MeasurementKind.Histogram, 1, "ms", FixedNow,
            CorrelationId: "how do I rotate the prod DB password");

        await _store.SaveBatchAsync([measurement], TestContext.Current.CancellationToken);

        (await CountRowsAsync()).ShouldBe(0, "CorrelationId must be a Guid-v7 N-format id, not arbitrary text");
    }

    [Fact]
    public async Task SaveBatchAsync_MixedBatch_RejectsOnlyTheOffendingRow()
    {
        var clean = new Measurement("search.fts", MeasurementKind.Histogram, 1, "ms", FixedNow);
        var dirty = new Measurement("search.fts", MeasurementKind.Histogram, 2, "ms", FixedNow,
            Tags: """{"query":"leaked text"}""");

        await _store.SaveBatchAsync([clean, dirty], TestContext.Current.CancellationToken);

        (await CountRowsAsync()).ShouldBe(1, "the batch is not all-or-nothing; only the offending row drops");
    }
}
