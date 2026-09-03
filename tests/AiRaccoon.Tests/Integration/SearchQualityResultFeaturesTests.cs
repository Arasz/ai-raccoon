using System.Text.Json;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using SQLitePCL;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     P6b — storage + honesty for the Stage-1 signals (plan §5, normative §9 M1/M2/M6/M8/S8/S10):
///     the tolerant result_features column, compact per-result feature JSON on the existing
///     single-statement quality write, and the labeled-row join. Retention needs no new code:
///     the existing row purge deletes whole rows, column included.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SearchQualityResultFeaturesTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot();
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteSearchQualityService _sut;

    public SearchQualityResultFeaturesTests()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _sut = new SqliteSearchQualityService(_factory, NullLogger<SqliteSearchQualityService>.Instance);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private async Task EnsureSchemaAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
    }

    private async Task<string?> ReadResultFeaturesAsync(string correlationId)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleOrDefaultAsync<string?>(
            new CommandDefinition("SELECT result_features FROM search_quality WHERE correlation_id = @Id",
                new { Id = correlationId }, cancellationToken: TestContext.Current.CancellationToken))
            .ConfigureAwait(false);
    }

    private static IReadOnlyList<RetrievalEvidence> TwoRowEvidence() =>
    [
        new RetrievalEvidence("hash-a", 1.0,
            [new LegRank("fts", 1), new LegRank("vector", 1)], 0.82),
        new RetrievalEvidence("hash-b", 0.5, [new LegRank("fts", 2)], null)
    ];

    [RetryFact]
    public async Task EnsureAsync_OnFreshBank_CreatesResultFeaturesColumn()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var columns = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM pragma_table_info('search_quality')",
            cancellationToken: TestContext.Current.CancellationToken)).ConfigureAwait(false)).ToList();
        columns.Contains("result_features").ShouldBeTrue(
            "M1: the digest-Ddl change carries the column to fresh banks");
    }

    [RetryFact]
    public async Task EnsureAsync_WhenColumnIsMissing_ReaddsItIdempotently()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        // A legacy bank: the table predates the P6b Ddl line (stale digest) and lacks the
        // column, so the digest-mismatch ensure must heal it. Dropping the column alone is
        // not a legacy bank — the digest gate would (correctly) skip the rerun.
        await connection.ExecuteAsync(new CommandDefinition(
            "ALTER TABLE search_quality DROP COLUMN result_features",
            cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA application_id = {MemorySchema.SchemaDigest + 1}",
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var columns = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM pragma_table_info('search_quality')",
            cancellationToken: TestContext.Current.CancellationToken)).ConfigureAwait(false)).ToList();
        columns.Contains("result_features").ShouldBeTrue(
            "the digest-mismatch ensure heals legacy banks with no version bump");

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
    }

    [RetryFact]
    public async Task RecordSearch_WithEvidence_PersistsResultFeaturesRoundTrip()
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchAsync("corr-feat-1", "query", "all", "proj-a", "memory", "sess-test",
            2, ["/a.md"], TestContext.Current.CancellationToken, TwoRowEvidence());

        var json = await ReadResultFeaturesAsync("corr-feat-1");
        json.ShouldNotBeNull("served evidence must persist on the already-written row");
        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.EnumerateArray().ToList();
        rows.Count.ShouldBe(2, "S10: features cover the passed (served-subset) rows only");
        rows[0].GetProperty("hash").GetString().ShouldBe("hash-a");
        rows[0].GetProperty("strength").GetDouble().ShouldBe(1.0);
        rows[0].GetProperty("legs").EnumerateArray().ToList().Count.ShouldBe(2);
        rows[0].GetProperty("legs")[0].GetProperty("name").GetString().ShouldBe("fts");
        rows[0].GetProperty("legs")[0].GetProperty("rank").GetInt32().ShouldBe(1);
        rows[0].GetProperty("cosine").GetDouble().ShouldBe(0.82);
        rows[1].GetProperty("hash").GetString().ShouldBe("hash-b");
        rows[1].GetProperty("cosine").ValueKind.ShouldBe(JsonValueKind.Null,
            "a hash with no vector leg keeps a null cosine, not a 0.0");
        json.Contains("snippet").ShouldBeFalse("features only — values and snippets never persist here");
        json.Contains("/a.md").ShouldBeFalse("features only — source paths stay in top_source_files, not in features");
    }

    [RetryFact]
    public async Task RecordSearch_WithEmptyEvidence_WritesRowWithNullFeatures()
    {
        await EnsureSchemaAsync();

        // Empty-results search with a present sidecar: the dispatcher joins zero rows.
        await _sut.RecordSearchAsync("corr-feat-empty", "query", "all", "proj-a", "memory", "sess-test",
            0, [], TestContext.Current.CancellationToken, []);

        var json = await ReadResultFeaturesAsync("corr-feat-empty");
        json.ShouldBeNull("empty evidence writes NULL (the top_source_files precedent) — and must not crash");
        var metrics = await _sut.GetMetricsAsync("proj-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metrics.TotalSearches.ShouldBe(1, "the row itself is still written");
    }

    [RetryFact]
    public async Task RecordSearch_WithNullEvidence_WritesRowWithNullFeatures()
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchAsync("corr-feat-null", "query", "all", "proj-a", "memory", "sess-test",
            0, [], TestContext.Current.CancellationToken, null);

        (await ReadResultFeaturesAsync("corr-feat-null")).ShouldBeNull("absent evidence writes exactly as before");
    }

    [RetryFact]
    public async Task RecordSearch_WithNonFiniteStrengthAndCosine_NullsThemAndWritesRow()
    {
        await EnsureSchemaAsync();
        var hostile = new RetrievalEvidence("hash-nan", double.NaN,
            [new LegRank("fts", 1)], double.PositiveInfinity);

        await _sut.RecordSearchAsync("corr-feat-nan", "query", "all", "proj-a", "memory", "sess-test",
            1, [], TestContext.Current.CancellationToken, [hostile]);

        var json = await ReadResultFeaturesAsync("corr-feat-nan");
        json.ShouldNotBeNull("M2 sanitization keeps a hostile row writable — System.Text.Json throws on non-finite doubles");
        using var document = JsonDocument.Parse(json);
        var row = document.RootElement.EnumerateArray().ToList().ShouldHaveSingleItem();
        row.GetProperty("strength").ValueKind.ShouldBe(JsonValueKind.Null, "P3's IsFinite rule: NaN strength becomes null");
        row.GetProperty("cosine").ValueKind.ShouldBe(JsonValueKind.Null, "P3's IsFinite rule: infinite cosine becomes null");
        row.GetProperty("hash").GetString().ShouldBe("hash-nan", "the row survives; only the hostile numbers null out");
    }

    [RetryFact]
    public async Task RecordSearch_WithFiveEvidenceRows_WritesExactlyOneStatement()
    {
        await EnsureSchemaAsync();
        var evidence = Enumerable.Range(0, 5)
            .Select(index => new RetrievalEvidence($"hash-{index}", 1.0 / (index + 1),
                [new LegRank("fts", index + 1)], null))
            .ToList();

        await using var shared = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var statements = new List<string>();
        strdelegate_trace tracer = (_, sql) => statements.Add(sql);
        raw.sqlite3_trace(shared.Handle, tracer, null);
        var service = new SqliteSearchQualityService(
            new SharedConnectionFactory(_factory, shared), NullLogger<SqliteSearchQualityService>.Instance);

        await service.RecordSearchAsync("corr-feat-count", "query", "all", "proj-a", "memory", "sess-test",
            5, [], TestContext.Current.CancellationToken, evidence);

        var qualityWrites = statements
            .Where(sql => sql.Contains("search_quality", StringComparison.OrdinalIgnoreCase)).ToList();
        qualityWrites.Count.ShouldBe(1,
            "M6: telemetry is one INSERT on the already-written row — no per-result statements, however many rows served");
        qualityWrites[0].ShouldContain("INSERT INTO search_quality");
    }

    [RetryFact]
    public async Task RecordSearch_ThenGradeThenFollowThrough_ReadsBackOneLabeledRow()
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchAsync("corr-join-1", "query", "all", "proj-a", "memory", "sess-test",
            2, ["/a.md"], TestContext.Current.CancellationToken, TwoRowEvidence());
        await _sut.RecordGradeAsync("proj-a", "corr-join-1", 4, "useful", TestContext.Current.CancellationToken);
        await _sut.RecordFollowThroughAsync("corr-join-1", "/a.md", ct: TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var row = await connection.QuerySingleAsync(new CommandDefinition(
            "SELECT usefulness_grade AS Grade, follow_through_count AS FollowThrough, follow_through_files AS Files, result_features AS Features FROM search_quality WHERE correlation_id = 'corr-join-1'",
            cancellationToken: TestContext.Current.CancellationToken));

        ((int)row.Grade).ShouldBe(4);
        ((int)row.FollowThrough).ShouldBe(1);
        ((string)row.Files).ShouldContain("/a.md");
        using var document = JsonDocument.Parse((string)row.Features);
        document.RootElement.EnumerateArray().ToList().Count.ShouldBe(2,
            "the correlation_id join to labels is free: one row holds features, grade, and follow-through with zero later joinery");
    }

    [RetryFact]
    public async Task DispatchAsync_WhenQualityTableIsMissing_StillReturnsFullResults()
    {
        await EnsureSchemaAsync();
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(new CommandDefinition("DROP TABLE search_quality",
                cancellationToken: TestContext.Current.CancellationToken));
        }

        var store = new StubSearchStore(
            [new MemorySearchResult("hash-a", 0.9, "a.md", "first"),
             new MemorySearchResult("hash-b", 0.8, "b.md", "second")],
            TwoRowEvidence());
        var dispatcher = new SearchDispatcher(store, new StubCodeSearch(), _sut);

        var result = await dispatcher.DispatchAsync(new SearchQuery("proj-a", "query"),
            SearchKind.Memory, "all", "corr-failopen", sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        result.Results.Count.ShouldBe(2, "G6 fail-open: a telemetry write failure still returns full results");
        result.Results.Select(r => r.Hash).ShouldBe(["hash-a", "hash-b"], "ordering untouched by the failed write");
    }

    [RetryFact]
    public async Task DispatchAsync_KindCode_WritesRowWithNullResultFeatures()
    {
        await EnsureSchemaAsync();
        var dispatcher = new SearchDispatcher(new StubSearchStore([], null),
            new StubCodeSearch([new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)]), _sut);

        await dispatcher.DispatchAsync(new SearchQuery("proj-a", "query"),
            SearchKind.Code, "all", "corr-code-1", sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        (await ReadResultFeaturesAsync("corr-code-1")).ShouldBeNull(
            "S8: kind=code has no memory sidecar — features null, row still written");
        var metrics = await _sut.GetMetricsAsync("proj-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metrics.TotalSearches.ShouldBe(1);
    }

    [RetryFact]
    public async Task DispatchAsync_EvidenceForNonServedHashes_StaysOutOfResultFeatures()
    {
        await EnsureSchemaAsync();
        var served = new MemorySearchResult("hash-a", 0.9, "a.md", "served");
        var evidence = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
        {
            ["hash-a"] = new RetrievalEvidence("hash-a", 1.0, [new LegRank("fts", 1)], null),
            // Fused but floored out upstream: joined by the dispatcher only over served rows.
            ["hash-dropped"] = new RetrievalEvidence("hash-dropped", 0.99, [new LegRank("vector", 1)], 0.9)
        };
        var dispatcher = new SearchDispatcher(new StubSearchStore([served], null, evidence),
            new StubCodeSearch(), _sut);

        await dispatcher.DispatchAsync(new SearchQuery("proj-a", "query"),
            SearchKind.Memory, "all", "corr-bounded-1", sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        var json = await ReadResultFeaturesAsync("corr-bounded-1");
        json.ShouldNotBeNull();
        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.EnumerateArray().ToList();
        rows.Count.ShouldBeLessThanOrEqualTo(1, "S10: the payload is bounded by the served rows, never the fused set");
        rows.ShouldHaveSingleItem().GetProperty("hash").GetString().ShouldBe("hash-a");
    }

    [RetryFact]
    public async Task PurgeOlderThan_RemovesRowsCarryingResultFeatures()
    {
        await EnsureSchemaAsync();
        await _sut.RecordSearchAsync("corr-purge-1", "query", "all", "proj-a", "memory", "sess-test",
            2, [], TestContext.Current.CancellationToken, TwoRowEvidence());

        var deleted = await _sut.PurgeOlderThanAsync(
            DateTimeOffset.UtcNow.AddDays(8).ToUnixTimeSeconds(), 7, TestContext.Current.CancellationToken);

        deleted.ShouldBe(1, "retention is the existing whole-row purge — the new column rides along, no new code");
        var metrics = await _sut.GetMetricsAsync("proj-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metrics.TotalSearches.ShouldBe(0);
    }

    [RetryFact]
    public async Task GetMetrics_LiveSearchesAreNotValidationN()
    {
        await EnsureSchemaAsync();
        await _sut.RecordSearchAsync("corr-hon-1", "q1", "all", "proj-a", "memory", "sess-test",
            1, [], TestContext.Current.CancellationToken, TwoRowEvidence());
        await _sut.RecordSearchAsync("corr-hon-2", "q2", "all", "proj-a", "memory", "sess-test",
            1, [], TestContext.Current.CancellationToken);
        await _sut.RecordSearchAsync("corr-hon-3", "q3", "all", "proj-a", "memory", "sess-test",
            1, [], TestContext.Current.CancellationToken);
        await _sut.RecordGradeAsync("proj-a", "corr-hon-1", 5, "great", TestContext.Current.CancellationToken);

        var metrics = await _sut.GetMetricsAsync("proj-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);

        metrics.TotalSearches.ShouldBe(3, "live traffic accrues every search");
        metrics.GradedSearches.ShouldBe(1, "labels stay sparse and opt-in");
        metrics.GradedSearches.ShouldNotBe(metrics.TotalSearches,
            "honesty (plan §5): never claim live-search counts as validation n — validation n is labeled rows");
    }

    private sealed class StubSearchStore(
        IReadOnlyList<MemorySearchResult> results,
        IReadOnlyList<RetrievalEvidence>? evidenceRows = null,
        IReadOnlyDictionary<string, RetrievalEvidence>? evidenceByHash = null) : FakeMemoryStore
    {
        public override Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
        {
            IReadOnlyDictionary<string, RetrievalEvidence>? byHash = evidenceByHash
                ?? evidenceRows?.ToDictionary(row => row.Hash, row => row, StringComparer.Ordinal);
            return Task.FromResult(new SearchResults(results, SearchTimings.Empty, null, byHash, null));
        }
    }

    private sealed class StubCodeSearch(IReadOnlyList<CodeSearchResult>? results = null) : ICodeSearchService
    {
        private readonly IReadOnlyList<CodeSearchResult> _results = results ?? [];

        public Task<CodeSearchResults> SearchAsync(CodeSearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodeSearchResults(_results, null));

        public Task<CodeEntry?> GetAsync(string projectId, string hash, CancellationToken cancellationToken = default) =>
            Task.FromResult<CodeEntry?>(null);
    }

    /// <summary>
    ///     Returns one shared connection so the test can trace the statements the service
    ///     executes; every other member delegates to the real factory.
    /// </summary>
    private sealed class SharedConnectionFactory(ISqliteConnectionFactory inner, SqliteConnection shared)
        : ISqliteConnectionFactory
    {
        public string BankPath => inner.BankPath;

        public Task<SqliteConnection> OpenBankAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(shared);

        public Task<bool> MigrateLegacyKeyAsync(CancellationToken cancellationToken = default) =>
            inner.MigrateLegacyKeyAsync(cancellationToken);

        public Task<SqliteConnection> OpenBankWithResolvedKeyAsync(
            AiRaccoon.Infrastructure.Sqlite.Encryption.ResolvedKey resolvedKey,
            CancellationToken cancellationToken = default) =>
            inner.OpenBankWithResolvedKeyAsync(resolvedKey, cancellationToken);

        public Task RekeyBankAsync(string newKey, CancellationToken cancellationToken = default) =>
            inner.RekeyBankAsync(newKey, cancellationToken);

        public Task RekeyBankAsync(string newKey, string? currentKey, CancellationToken cancellationToken = default) =>
            inner.RekeyBankAsync(newKey, currentKey, cancellationToken);

        public Task<SqliteConnection> OpenBankWithKeyAsync(string? key, CancellationToken cancellationToken = default) =>
            inner.OpenBankWithKeyAsync(key, cancellationToken);

        public Task<SqliteConnection> OpenBankSkippingEnsureAsync(CancellationToken cancellationToken = default) =>
            inner.OpenBankSkippingEnsureAsync(cancellationToken);
    }
}
