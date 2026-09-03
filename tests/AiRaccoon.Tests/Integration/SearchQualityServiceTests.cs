using System.Text.Json;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
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
            "corr-001", "test query", "all", "proj-a", "memory", "session-1",
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
            "corr-002", "query", "all", "proj-a", kind: "memory", sessionId: "sess-test",
            resultCount: 3, topSourceFiles: ["/file.md"], ct: TestContext.Current.CancellationToken);

        await _sut.RecordFollowThroughAsync("corr-002", "/file.md", ct: TestContext.Current.CancellationToken);
        await _sut.RecordFollowThroughAsync("corr-002", "/other.md", ct: TestContext.Current.CancellationToken);

        var metrics = await _sut.GetMetricsAsync("proj-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metrics.FollowThroughSearches.ShouldBe(1);
    }

    [RetryFact]
    public async Task RecordFollowThrough_DuplicateFile_CountsOnce()
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchAsync(
            "corr-003", "query", "all", "proj-a", kind: "memory", sessionId: "sess-test",
            resultCount: 1, topSourceFiles: ["/file.md"], ct: TestContext.Current.CancellationToken);

        await _sut.RecordFollowThroughAsync("corr-003", "/file.md", ct: TestContext.Current.CancellationToken);
        await _sut.RecordFollowThroughAsync("corr-003", "/file.md", ct: TestContext.Current.CancellationToken);

        var metrics = await _sut.GetMetricsAsync("proj-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metrics.FollowThroughSearches.ShouldBe(1);
    }

    [RetryFact]
    public async Task RecordGrade_UpdatesRow_MetricsReflectGrade()
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchAsync(
            "corr-004", "query", "all", "proj-a", kind: "memory", sessionId: "sess-test",
            resultCount: 5, topSourceFiles: ["/file.md"], ct: TestContext.Current.CancellationToken);

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

        await _sut.RecordSearchAsync(correlationId: "c1", query: "q1", scope: "all", projectId: "proj-a", kind: "memory", sessionId: "sess-test", resultCount: 1, topSourceFiles: [], ct: TestContext.Current.CancellationToken);
        await _sut.RecordSearchAsync(correlationId: "c2", query: "q2", scope: "all", projectId: "proj-b", kind: "memory", sessionId: "sess-test", resultCount: 1, topSourceFiles: [], ct: TestContext.Current.CancellationToken);

        var metricsA = await _sut.GetMetricsAsync("proj-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metricsA.TotalSearches.ShouldBe(1);

        var metricsAll = await _sut.GetMetricsAsync(null, DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metricsAll.TotalSearches.ShouldBe(2);
    }

    [RetryFact]
    public async Task GetMetrics_FollowThroughRate_CalculatesCorrectly()
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchAsync(correlationId: "c1", query: "q1", scope: "all", projectId: "proj-a", kind: "memory", sessionId: "sess-test", resultCount: 1, topSourceFiles: [], ct: TestContext.Current.CancellationToken);
        await _sut.RecordSearchAsync(correlationId: "c2", query: "q2", scope: "all", projectId: "proj-a", kind: "memory", sessionId: "sess-test", resultCount: 1, topSourceFiles: [], ct: TestContext.Current.CancellationToken);
        await _sut.RecordFollowThroughAsync("c1", "/file.md", ct: TestContext.Current.CancellationToken);

        var metrics = await _sut.GetMetricsAsync("proj-a", DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metrics.FollowThroughRate.ShouldBe(0.5);
    }

    [RetryFact]
    public async Task RecordSearch_WithNullProjectId_Succeeds()
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchAsync(
            correlationId: "corr-null", query: "query", scope: null, projectId: null, kind: "memory", sessionId: "sess-test",
            resultCount: 0, topSourceFiles: [], ct: TestContext.Current.CancellationToken);

        var metrics = await _sut.GetMetricsAsync(null, DateTimeOffset.MinValue, TestContext.Current.CancellationToken);
        metrics.TotalSearches.ShouldBe(1);
    }

    /// <summary>
    ///     P1: the present session id is stored verbatim — row-read through the real service,
    ///     not spy-only. Pre-P1 the Safe path hardcoded null, so this read back NULL.
    /// </summary>
    [RetryFact]
    public async Task RecordSearchSafe_PresentSessionId_StoredVerbatim()
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchSafeAsync(
            correlationId: "corr-sess-1", query: "session verbatim query", scope: "all", projectId: "proj-a",
            kind: "memory", sessionId: "sess-abc-123", resultCount: 1, topSourceFiles: [],
            ct: TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var stored = await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT session_id FROM search_quality WHERE correlation_id = @Id",
            new { Id = "corr-sess-1" });
        stored.ShouldBe("sess-abc-123");
    }

    /// <summary>
    ///     P3 (ADR-0097): kind is required on the write path — every recorded row names the leg
    ///     it describes. Both verbs take it as a required string after projectId / before
    ///     sessionId; the dispatcher passes <c>SearchKind.ToString().ToLowerInvariant()</c>.
    /// </summary>
    [RetryTheory]
    [InlineData("memory")]
    [InlineData("code")]
    [InlineData("both")]
    public async Task RecordSearch_KindStoredVerbatim(string kind)
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchAsync(
            correlationId: $"corr-kind-{kind}", query: "kind query", scope: "all", projectId: "proj-a",
            kind: kind, sessionId: "sess-test", resultCount: 1, topSourceFiles: [],
            ct: TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var stored = await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT kind FROM search_quality WHERE correlation_id = @Id",
            new { Id = $"corr-kind-{kind}" });
        stored.ShouldBe(kind);
    }

    [RetryTheory]
    [InlineData("memory")]
    [InlineData("code")]
    [InlineData("both")]
    public async Task RecordSearchSafe_KindStoredVerbatim(string kind)
    {
        await EnsureSchemaAsync();

        await _sut.RecordSearchSafeAsync(
            correlationId: $"corr-kind-safe-{kind}", query: "kind query", scope: "all", projectId: "proj-a",
            kind: kind, sessionId: "sess-test", resultCount: 1, topSourceFiles: [],
            ct: TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var stored = await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT kind FROM search_quality WHERE correlation_id = @Id",
            new { Id = $"corr-kind-safe-{kind}" });
        stored.ShouldBe(kind);
    }

    /// <summary>
    ///     P3: the service guards kind fail-fast on the throwing verb — anything outside
    ///     memory/code/both is a caller bug, and the CHECK constraint is the backstop, never the
    ///     validator. (The Safe verb keeps its never-throws contract and swallows into a log.)
    ///     Mutation: drop the guard → this fails.
    /// </summary>
    [RetryTheory]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData("MEMORY")]
    public async Task RecordSearch_InvalidKind_RejectedFailFast(string kind)
    {
        await EnsureSchemaAsync();

        await Should.ThrowAsync<ArgumentException>(() => _sut.RecordSearchAsync(
            correlationId: "corr-kind-bad", query: "kind query", scope: "all", projectId: "proj-a",
            kind: kind, sessionId: "sess-test", resultCount: 1, topSourceFiles: [],
            ct: TestContext.Current.CancellationToken));
    }

    /// <summary>
    ///     P4: a legacy plain-string cell upgrades losslessly to object rows on append — the codec
    ///     is the migration (no DDL). Pre-P4 this stored '["/a.md","/b.md"]' (bare strings).
    /// </summary>
    [RetryFact]
    public async Task RecordFollowThrough_LegacyStringRow_UpgradesLosslesslyToObjectRows()
    {
        await EnsureSchemaAsync();
        await _sut.RecordSearchAsync(
            correlationId: "corr-p4-legacy", query: "query", scope: "all", projectId: "proj-a",
            kind: "memory", sessionId: "sess-test", resultCount: 1, topSourceFiles: ["/a.md"],
            ct: TestContext.Current.CancellationToken);
        await using (var seed = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await seed.ExecuteAsync(
                "UPDATE search_quality SET follow_through_count = 1, "
                + "follow_through_files = '[\"/a.md\"]' WHERE correlation_id = 'corr-p4-legacy'");
        }

        await _sut.RecordFollowThroughAsync("corr-p4-legacy", "/b.md", ct: TestContext.Current.CancellationToken);

        var raw = await ReadFollowThroughFilesAsync("corr-p4-legacy");
        raw.ShouldNotBeNull();
        var rows = JsonDocument.Parse(raw).RootElement.EnumerateArray().ToList();
        rows.Count.ShouldBe(2);
        rows[0].GetProperty("path").GetString().ShouldBe("/a.md");
        rows[0].GetProperty("rank").ValueKind.ShouldBe(JsonValueKind.Null);
        rows[1].GetProperty("path").GetString().ShouldBe("/b.md");
    }

    /// <summary>
    ///     P4: legacy and new rows coexist in one cell byte-identically — order preserved,
    ///     lowercase keys, null rank rendered. A writer that rewrites every row (drops legacy,
    ///     reorders, PascalCase keys) fails this oracle.
    /// </summary>
    [RetryFact]
    public async Task RecordFollowThrough_MixedCoexistence_StoredByteIdentical()
    {
        await EnsureSchemaAsync();
        await _sut.RecordSearchAsync(
            correlationId: "corr-p4-mixed", query: "query", scope: "all", projectId: "proj-a",
            kind: "memory", sessionId: "sess-test", resultCount: 1, topSourceFiles: ["/a.md"],
            ct: TestContext.Current.CancellationToken);
        await using (var seed = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await seed.ExecuteAsync(
                "UPDATE search_quality SET follow_through_count = 1, "
                + "follow_through_files = '[\"/a.md\"]' WHERE correlation_id = 'corr-p4-mixed'");
        }

        await _sut.RecordFollowThroughAsync("corr-p4-mixed", "/b.md", ct: TestContext.Current.CancellationToken);

        (await ReadFollowThroughFilesAsync("corr-p4-mixed")).ShouldBe(
            "[{\"path\":\"/a.md\",\"rank\":null},{\"path\":\"/b.md\",\"rank\":null}]",
            "legacy + new rows coexist byte-identically — a rewriting writer fails here");
    }

    /// <summary>
    ///     P4 owns the unknown-id pin for BOTH paths (plan ruling 4; P5 must not re-pin): an
    ///     unknown correlation id is a silent no-op — no throw, no row created.
    ///     Mutation: throw on 0-row UPDATE → this fails.
    /// </summary>
    [RetryFact]
    public async Task RecordFollowThrough_UnknownCorrelationId_SilentNoOp()
    {
        await EnsureSchemaAsync();

        await _sut.RecordFollowThroughAsync("no-such-corr", "/file.md", ct: TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM search_quality")).ShouldBe(0);
    }

    /// <summary>
    ///     P4 owns the unknown-id pin for BOTH paths (plan ruling 4; P5 must not re-pin): grade
    ///     on an unknown correlation id is a silent no-op — no throw, no row created.
    ///     Mutation: throw on 0-row UPDATE → this fails.
    /// </summary>
    [RetryFact]
    public async Task RecordGrade_UnknownCorrelationId_SilentNoOp()
    {
        await EnsureSchemaAsync();

        await _sut.RecordGradeAsync("proj-a", "no-such-corr", 4, null, TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM search_quality")).ShouldBe(0);
    }

    /// <summary>
    ///     P4: served ranks round-trip over two files with distinct ranks. Each rank assertion
    ///     names the path it pins (d-393 SHOULD-10); ranks are section-agnostic by design —
    ///     rank-only telemetry under kind=both cannot say which leg served the file.
    /// </summary>
    [RetryFact]
    public async Task RecordFollowThrough_RankRoundTrip_PersistsDistinctRanks()
    {
        await EnsureSchemaAsync();
        await _sut.RecordSearchAsync(
            correlationId: "corr-p4-rank", query: "query", scope: "all", projectId: "proj-a",
            kind: "both", sessionId: "sess-test", resultCount: 5, topSourceFiles: ["/a.md", "/b.md"],
            ct: TestContext.Current.CancellationToken);

        await _sut.RecordFollowThroughAsync("corr-p4-rank", "/a.md", servedRank: 1, ct: TestContext.Current.CancellationToken);
        await _sut.RecordFollowThroughAsync("corr-p4-rank", "/b.md", servedRank: 3, ct: TestContext.Current.CancellationToken);

        var raw = await ReadFollowThroughFilesAsync("corr-p4-rank");
        raw.ShouldNotBeNull();
        var rows = JsonDocument.Parse(raw).RootElement.EnumerateArray().ToList();
        rows.Count.ShouldBe(2);
        rows[0].GetProperty("path").GetString().ShouldBe("/a.md");
        rows[0].GetProperty("rank").GetInt32().ShouldBe(1, "row for /a.md keeps servedRank 1 (rank-only, section-agnostic by design)");
        rows[1].GetProperty("path").GetString().ShouldBe("/b.md");
        rows[1].GetProperty("rank").GetInt32().ShouldBe(3, "row for /b.md keeps servedRank 3 (rank-only, section-agnostic by design)");
    }

    /// <summary>
    ///     P4 dedupe rule: ordinal by path — an existing non-null rank is never clobbered, a
    ///     null rank is filled once by a later non-null, then pinned. Count stays distinct paths.
    ///     (This overrides any "first-supplied wins" phrasing elsewhere.)
    /// </summary>
    [RetryFact]
    public async Task RecordFollowThrough_Dedupe_KeepsFirstNonNullRank_FillsNullOnce()
    {
        await EnsureSchemaAsync();
        await _sut.RecordSearchAsync(
            correlationId: "corr-p4-dedupe", query: "query", scope: "all", projectId: "proj-a",
            kind: "memory", sessionId: "sess-test", resultCount: 2, topSourceFiles: ["/a.md", "/b.md"],
            ct: TestContext.Current.CancellationToken);

        await _sut.RecordFollowThroughAsync("corr-p4-dedupe", "/a.md", servedRank: 3, ct: TestContext.Current.CancellationToken);
        await _sut.RecordFollowThroughAsync("corr-p4-dedupe", "/a.md", servedRank: 5, ct: TestContext.Current.CancellationToken);
        await _sut.RecordFollowThroughAsync("corr-p4-dedupe", "/a.md", ct: TestContext.Current.CancellationToken);
        await _sut.RecordFollowThroughAsync("corr-p4-dedupe", "/b.md", ct: TestContext.Current.CancellationToken);
        await _sut.RecordFollowThroughAsync("corr-p4-dedupe", "/b.md", servedRank: 7, ct: TestContext.Current.CancellationToken);
        await _sut.RecordFollowThroughAsync("corr-p4-dedupe", "/b.md", servedRank: 9, ct: TestContext.Current.CancellationToken);

        var raw = await ReadFollowThroughFilesAsync("corr-p4-dedupe");
        raw.ShouldNotBeNull();
        var rows = JsonDocument.Parse(raw).RootElement.EnumerateArray().ToList();
        rows.Count.ShouldBe(2, "count == distinct paths");
        var ranks = rows.ToDictionary(
            e => e.GetProperty("path").GetString()!,
            e => e.GetProperty("rank").ValueKind == JsonValueKind.Null ? (int?)null : e.GetProperty("rank").GetInt32());
        ranks["/a.md"].ShouldBe(3, "existing non-null rank is never clobbered — not by a later rank, not by null");
        ranks["/b.md"].ShouldBe(7, "null rank is filled once by a later non-null, then pinned against rank 9");

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        (await connection.QuerySingleOrDefaultAsync<int>(
            "SELECT follow_through_count FROM search_quality WHERE correlation_id = @Id",
            new { Id = "corr-p4-dedupe" })).ShouldBe(2, "follow_through_count == distinct paths");
    }

    /// <summary>
    ///     P4: ranks below 1 are a caller bug — rejected fail-fast before any DB touch. No upper
    ///     bound: result-set size is unknowable at write time.
    /// </summary>
    [RetryTheory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public async Task RecordFollowThrough_RankBelowOne_RejectedFailFast(int rank)
    {
        await EnsureSchemaAsync();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => _sut.RecordFollowThroughAsync(
            "corr-p4-guard", "/file.md", servedRank: rank, ct: TestContext.Current.CancellationToken));
    }

    /// <summary>P4: no upper bound on rank — the writer cannot know the result-set size.</summary>
    [RetryFact]
    public async Task RecordFollowThrough_HugeRankAccepted_NoUpperBound()
    {
        await EnsureSchemaAsync();
        await _sut.RecordSearchAsync(
            correlationId: "corr-p4-big", query: "query", scope: "all", projectId: "proj-a",
            kind: "memory", sessionId: "sess-test", resultCount: 1, topSourceFiles: ["/file.md"],
            ct: TestContext.Current.CancellationToken);

        await _sut.RecordFollowThroughAsync("corr-p4-big", "/file.md", servedRank: int.MaxValue, ct: TestContext.Current.CancellationToken);

        var raw = await ReadFollowThroughFilesAsync("corr-p4-big");
        raw.ShouldNotBeNull();
        JsonDocument.Parse(raw).RootElement.EnumerateArray().Single()
            .GetProperty("rank").GetInt32().ShouldBe(int.MaxValue);
    }

    /// <summary>
    ///     P4 is codec-only: no column, no ladder rung (P3 owned v11→v12). Pins the exact
    ///     search_quality column set and the v12 stamp, so any DDL or version bump fails here.
    /// </summary>
    [RetryFact]
    public async Task GetMetrics_PooledSemantics_CountsAllRowsRegardlessOfScopeAndKind()
    {
        // P5 audit pin (kind-blindness, deliberate) extended at P7 with multi-kind seeds:
        // GetMetricsAsync aggregates with no kind/dimension filter — every row in the project
        // window counts, whatever its scope or kind. A future narrowing (a kind predicate or a
        // scope predicate) must trip this test instead of shifting dashboards silently.
        // Split only on a named new consumer (none: zero production callers at audit time).
        // Red-proof: AND kind = 'memory' mutation drops TotalSearches 4 -> 2; AND scope = 'all'
        // mutation drops it 4 -> 1 (verified at P7 join).
        await EnsureSchemaAsync();
        var ct = TestContext.Current.CancellationToken;

        await _sut.RecordSearchAsync(correlationId: "pool-1", query: "q1", scope: "all", projectId: "proj-a", kind: "memory", sessionId: "sess-test", resultCount: 2, topSourceFiles: ["/a.md"], ct: ct);
        await _sut.RecordSearchAsync(correlationId: "pool-2", query: "q2", scope: "project", projectId: "proj-a", kind: "code", sessionId: "sess-test", resultCount: 3, topSourceFiles: ["/b.md"], ct: ct);
        await _sut.RecordSearchAsync(correlationId: "pool-3", query: "q3", scope: "shared", projectId: "proj-a", kind: "both", sessionId: "sess-test", resultCount: 1, topSourceFiles: [], ct: ct);
        await _sut.RecordSearchAsync(correlationId: "pool-4", query: "q4", scope: null, projectId: "proj-a", kind: "memory", sessionId: "sess-test", resultCount: 0, topSourceFiles: [], ct: ct);
        await _sut.RecordSearchAsync(correlationId: "pool-5", query: "q5", scope: "all", projectId: "proj-b", kind: "code", sessionId: "sess-test", resultCount: 9, topSourceFiles: ["/c.md"], ct: ct);

        await _sut.RecordGradeAsync("proj-a", "pool-1", 5, null, ct);
        await _sut.RecordFollowThroughAsync("pool-2", "/b.md", ct: ct);

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
        // Red-proof: AND project_id = @ProjectId mutation drops GradedSearches 1 -> 0 (verified
        // at P5 audit, re-verified at P7 join).
        await EnsureSchemaAsync();
        var ct = TestContext.Current.CancellationToken;

        await _sut.RecordSearchAsync(correlationId: "key-1", query: "q1", scope: "all", projectId: "proj-a", kind: "memory", sessionId: "sess-test", resultCount: 2, topSourceFiles: ["/a.md"], ct: ct);
        await _sut.RecordSearchAsync(correlationId: "key-2", query: "q2", scope: "all", projectId: "proj-a", kind: "code", sessionId: "sess-test", resultCount: 3, topSourceFiles: ["/b.md"], ct: ct);

        await _sut.RecordGradeAsync("proj-B-NOT-THE-ROW-PROJECT", "key-1", 4, "audit pin", ct);

        var metrics = await _sut.GetMetricsAsync("proj-a", DateTimeOffset.MinValue, ct);
        metrics.TotalSearches.ShouldBe(2);
        metrics.GradedSearches.ShouldBe(1);
        metrics.AverageGrade.ShouldBe(4.0);
        metrics.Coverage.ShouldBe(0.5);
    }

    [RetryFact]
    public async Task SchemaSearchQualityShape_UnchangedByRankWork_NoColumnOrVersionBump()
    {
        await EnsureSchemaAsync();

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var columns = (await connection.QueryAsync<string>("SELECT name FROM pragma_table_info('search_quality')")).ToList();
        columns.ShouldBe(
            [
                "id", "correlation_id", "query", "scope", "project_id", "session_id", "kind",
                "result_count", "top_source_files", "follow_through_count", "follow_through_files",
                "usefulness_grade", "grade_note", "created_at"
            ],
            "P4 is codec-only — follow_through_files carries ranks as JSON, no column may be added");
        (await connection.ExecuteScalarAsync<long>("PRAGMA user_version")).ShouldBe(12,
            "P4 owns no ladder rung — P3's v12 is current");
    }

    private async Task<string?> ReadFollowThroughFilesAsync(string correlationId)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.QuerySingleOrDefaultAsync<string?>(
            "SELECT follow_through_files FROM search_quality WHERE correlation_id = @Id",
            new { Id = correlationId });
    }
}
