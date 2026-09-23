using System.Text.Json;
using AiRaccoon.Access;
using AiRaccoon.Core.Isolation;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.Projects;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     K6 (F52): an absolute relevance floor plus an explicit unranked marker, so a zero-overlap
///     query stops returning every entry as a confident top hit. The floor is absolute (the fused
///     content cosine), unlike minRelativeScore which is a fraction of each response's own top hit;
///     the marker names the one wire field that says "this ranking is not evidence of relevance".
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MemorySearchAbsoluteRelevanceTests
{
    private readonly FakeStore _store = new();
    private readonly MemoryTools _tools;

    public MemorySearchAbsoluteRelevanceTests()
    {
        var access = new MemoryAccessGuard(_store);
        var gate = new ToolGate(access, new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard(), new StubMigrationGate(migrated: false));
        _tools = new MemoryTools(_store, gate, new SearchDispatcher(_store, new NoOpCodeSearchService(), new NoOpSearchQualityService()),
            new QueryGuardService(new InMemorySettings()),
            new MemoryWriteService(_store, new FakePromotionQueue()), new NoOpMeasurementRecorder(),
            new InMemorySettings(), NullLogger<MemoryTools>.Instance, new CountingEmbeddingService());
    }

    /// <summary>
    ///     The measured F52 shape (review probe "quantum chromodynamics lattice gauge"): four rows,
    ///     cosines 0.028/0.022/−0.016/−0.034, single vector leg, flat top margin 0.0161 — and yet
    ///     every row served at ranking 1.0. The absolute floor must drop all four: nothing in the
    ///     bank answers the query, so the honest response is empty.
    /// </summary>
    [Fact]
    public async Task Search_ZeroOverlapResponse_IsEmptiedByTheAbsoluteRelevanceFloor()
    {
        _store.StubResults =
        [
            new MemorySearchResult("h1", 1.0, "a.md", "first"),
            new MemorySearchResult("h2", 0.98, "b.md", "second"),
            new MemorySearchResult("h3", 0.96, "c.md", "third"),
            new MemorySearchResult("h4", 0.94, "d.md", "fourth")
        ];
        _store.StubEvidence = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
        {
            ["h1"] = new RetrievalEvidence("h1", 1.0, [new LegRank("vector", 1)], 0.028),
            ["h2"] = new RetrievalEvidence("h2", 0.98, [new LegRank("vector", 2)], 0.022),
            ["h3"] = new RetrievalEvidence("h3", 0.96, [new LegRank("vector", 3)], -0.016),
            ["h4"] = new RetrievalEvidence("h4", 0.94, [new LegRank("vector", 4)], -0.034)
        };
        _store.StubStats = new FusionStats(0.0161, null, 0.0164, ["vector"]);

        var envelope = await _tools.Search("acme", "quantum chromodynamics lattice gauge", kind: "memory",
            sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.ShouldBeEmpty(
            "no row carries absolute relevance — a zero-overlap query must not return confident top hits");
    }

    /// <summary>
    ///     Positive control for the floor: a genuine match (cosines well over the absolute floor)
    ///     returns every row — a floor that drops everything, or anything it should keep, fails here.
    /// </summary>
    [Fact]
    public async Task Search_GenuineMatch_ReturnsEveryRowUnmarked()
    {
        _store.StubResults =
        [
            new MemorySearchResult("g1", 1.0, "a.md", "first"),
            new MemorySearchResult("g2", 0.8, "b.md", "second")
        ];
        _store.StubEvidence = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
        {
            ["g1"] = new RetrievalEvidence("g1", 0.95, [new LegRank("fts", 1), new LegRank("vector", 1)], 0.62),
            ["g2"] = new RetrievalEvidence("g2", 0.42, [new LegRank("fts", 2), new LegRank("vector", 3)], 0.55)
        };
        _store.StubStats = new FusionStats(0.508, 0.4, 0.0328, ["fts", "vector"]);

        var envelope = await _tools.Search("acme", "sourdough starter hydration", kind: "memory",
            sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.Count.ShouldBe(2, "a genuine match is kept whole");
        // A genuine match returns unmarked results.
        JsonSerializer.Serialize(envelope.Data, McpJsonUtilities.DefaultOptions).ShouldNotContain("unranked");
    }

    /// <summary>
    ///     The marker's own arm: rows with no absolute signal (FTS-only legs, no cosine) survive the
    ///     floor but carry rank-derived rankings only, and the measured best-of-a-bad-lot signature
    ///     (single leg, flat top margin) must mark the response unranked instead of confident.
    /// </summary>
    [Fact]
    public async Task Search_FlatMarginSingleLegResponse_IsExplicitlyMarkedUnranked()
    {
        _store.StubResults =
        [
            new MemorySearchResult("f1", 1.0, "a.md", "first"),
            new MemorySearchResult("f2", 0.98, "b.md", "second"),
            new MemorySearchResult("f3", 0.96, "c.md", "third"),
            new MemorySearchResult("f4", 0.94, "d.md", "fourth")
        ];
        _store.StubEvidence = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
        {
            ["f1"] = new RetrievalEvidence("f1", 1.0, [new LegRank("fts", 1)], null),
            ["f2"] = new RetrievalEvidence("f2", 0.98, [new LegRank("fts", 2)], null),
            ["f3"] = new RetrievalEvidence("f3", 0.96, [new LegRank("fts", 3)], null),
            ["f4"] = new RetrievalEvidence("f4", 0.94, [new LegRank("fts", 4)], null)
        };
        _store.StubStats = new FusionStats(0.0161, null, 0.0164, ["fts"]);

        var envelope = await _tools.Search("acme", "background process dotnet test output", kind: "memory",
            sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.Count.ShouldBe(4, "rows without an absolute signal are kept, not dropped");
        // A flat-margin single-leg response is explicitly marked unranked.
        JsonSerializer.Serialize(envelope.Data, McpJsonUtilities.DefaultOptions).ShouldContain("\"unranked\"");
    }

    /// <summary>
    ///     Positive control against "always mark": the same single-leg shape with a real margin
    ///     (the genuine query measured topMargin 0.508) is a decisive hit and stays unmarked.
    /// </summary>
    [Fact]
    public async Task Search_GenuineSpreadSingleLegResponse_IsNotMarkedUnranked()
    {
        _store.StubResults =
        [
            new MemorySearchResult("s1", 1.0, "a.md", "first"),
            new MemorySearchResult("s2", 0.8, "b.md", "second")
        ];
        _store.StubEvidence = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
        {
            ["s1"] = new RetrievalEvidence("s1", 1.0, [new LegRank("fts", 1)], null),
            ["s2"] = new RetrievalEvidence("s2", 0.8, [new LegRank("fts", 2)], null)
        };
        _store.StubStats = new FusionStats(0.508, null, 0.0328, ["fts"]);

        var envelope = await _tools.Search("acme", "anchovy shipments ledger", kind: "memory",
            sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.Count.ShouldBe(2);
        // A decisive single-leg hit is ranked evidence, not a bad lot.
        JsonSerializer.Serialize(envelope.Data, McpJsonUtilities.DefaultOptions).ShouldNotContain("unranked");
    }

    /// <summary>
    ///     Positive control against "always mark", second shape: a flat margin that is nonetheless
    ///     absolute-backed (one row clears the cosine floor) is kept and unmarked — the marker fires
    ///     on ranking with no absolute backing, not on margins alone.
    /// </summary>
    [Fact]
    public async Task Search_FlatMarginWithAnAbsoluteHit_IsNotMarkedUnranked()
    {
        _store.StubResults =
        [
            new MemorySearchResult("m1", 1.0, "a.md", "first"),
            new MemorySearchResult("m2", 0.98, "b.md", "second")
        ];
        _store.StubEvidence = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
        {
            ["m1"] = new RetrievalEvidence("m1", 1.0, [new LegRank("fts", 1), new LegRank("vector", 1)], 0.66),
            ["m2"] = new RetrievalEvidence("m2", 0.98, [new LegRank("fts", 2)], null)
        };
        _store.StubStats = new FusionStats(0.0161, null, 0.0328, ["fts", "vector"]);

        var envelope = await _tools.Search("acme", "harbor tide charts", kind: "memory",
            sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.Count.ShouldBe(2, "the absolute-backed row is kept and the signal-less sibling with it");
        // One absolute-backed hit means the ranking has absolute backing.
        JsonSerializer.Serialize(envelope.Data, McpJsonUtilities.DefaultOptions).ShouldNotContain("unranked");
    }

    /// <summary>
    ///     ADR-0096's full-recall hatch stays one call: minRelativeScore=0 serves every candidate the
    ///     store ranked, absolute relevance or not (F20 measured exactly this call returning 66 of 66).
    /// </summary>
    [Fact]
    public async Task Search_FullRecallRequest_ServesEveryRowRegardlessOfAbsoluteRelevance()
    {
        _store.StubResults =
        [
            new MemorySearchResult("h1", 1.0, "a.md", "first"),
            new MemorySearchResult("h2", 0.98, "b.md", "second"),
            new MemorySearchResult("h3", 0.96, "c.md", "third"),
            new MemorySearchResult("h4", 0.94, "d.md", "fourth")
        ];
        _store.StubEvidence = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
        {
            ["h1"] = new RetrievalEvidence("h1", 1.0, [new LegRank("vector", 1)], 0.028),
            ["h2"] = new RetrievalEvidence("h2", 0.98, [new LegRank("vector", 2)], 0.022),
            ["h3"] = new RetrievalEvidence("h3", 0.96, [new LegRank("vector", 3)], -0.016),
            ["h4"] = new RetrievalEvidence("h4", 0.94, [new LegRank("vector", 4)], -0.034)
        };
        _store.StubStats = new FusionStats(0.0161, null, 0.0164, ["vector"]);

        var envelope = await _tools.Search("acme", "quantum chromodynamics lattice gauge", kind: "memory",
            minRelativeScore: 0.0, sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.Count.ShouldBe(4,
            "minRelativeScore=0 is documented full recall — the absolute floor must not silently shorten it");
        // Full recall keeps the rows but still says the ranking is not relevance.
        JsonSerializer.Serialize(envelope.Data, McpJsonUtilities.DefaultOptions).ShouldContain("\"unranked\"");
    }

    /// <summary>
    ///     A row whose text contains every query term is an answer even when its cosine is under
    ///     the floor (an identifier such as a ticket key embeds far from prose); its below-floor
    ///     neighbours that matched only part of the query are still dropped.
    /// </summary>
    [Fact]
    public async Task Search_BelowFloorRowMatchingEveryQueryTerm_IsKeptAndItsPartialNeighbourDropped()
    {
        _store.StubResults =
        [
            new MemorySearchResult("t1", 1.0, "a.md", "Deploy blocked on AIR-4471"),
            new MemorySearchResult("t2", 0.98, "b.md", "air freight notes")
        ];
        _store.StubEvidence = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
        {
            ["t1"] = new RetrievalEvidence("t1", 0.98, [new LegRank("fts", 1), new LegRank("vector", 2)], 0.21),
            ["t2"] = new RetrievalEvidence("t2", 0.5, [new LegRank("fts", 2)], 0.12)
        };
        _store.StubStats = new FusionStats(0.3, null, 0.0328, ["fts", "vector"]);
        _store.StubAllTermsMatched = new HashSet<string>(["t1"], StringComparer.Ordinal);

        var envelope = await _tools.Search("acme", "AIR-4471", kind: "memory",
            sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.Select(row => row.Hash).ShouldBe(["t1"]);
        envelope.Data!.Truncation.ShouldNotBeNull().ShouldHaveSingleItem().Dropped.ShouldBe(1,
            "the partial match is the one row the absolute floor cut");
    }

    /// <summary>
    ///     A flat single-leg response whose top row contains every query term has absolute backing,
    ///     so it is not marked unranked.
    /// </summary>
    [Fact]
    public async Task Search_FlatSingleLegResponseWithAnAllTermsMatch_IsNotMarkedUnranked()
    {
        _store.StubResults =
        [
            new MemorySearchResult("k1", 1.0, "a.md", "first"),
            new MemorySearchResult("k2", 0.98, "b.md", "second")
        ];
        _store.StubEvidence = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
        {
            ["k1"] = new RetrievalEvidence("k1", 1.0, [new LegRank("fts", 1)], null),
            ["k2"] = new RetrievalEvidence("k2", 0.98, [new LegRank("fts", 2)], null)
        };
        _store.StubStats = new FusionStats(0.0161, null, 0.0164, ["fts"]);
        _store.StubAllTermsMatched = new HashSet<string>(["k1"], StringComparer.Ordinal);

        var envelope = await _tools.Search("acme", "AIR-4471", kind: "memory",
            sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.Count.ShouldBe(2);
        JsonSerializer.Serialize(envelope.Data, McpJsonUtilities.DefaultOptions).ShouldNotContain("unranked");
    }

    /// <summary>ADR-0108: the floor belongs to the engine that produced the cosines — granite scores
    /// off-topic text up to 0.787, so 0.35 would keep everything.</summary>
    [Theory]
    [InlineData(0.75, 0)]
    [InlineData(0.85, 2)]
    public async Task Search_AppliesTheEnginesOwnRelevanceFloor_NotTheMiniLmConstant(double cosine, int served)
    {
        _store.StubResults = [new MemorySearchResult("h1", 1.0, "a.md", "first"), new MemorySearchResult("h2", 0.9, "b.md", "second")];
        _store.StubEvidence = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
        {
            ["h1"] = new RetrievalEvidence("h1", 1.0, [new LegRank("vector", 1)], cosine),
            ["h2"] = new RetrievalEvidence("h2", 0.9, [new LegRank("vector", 2)], cosine)
        };
        _store.StubRelevanceFloor = 0.79;

        var envelope = await _tools.Search("acme", "lighthouse lamp room", kind: "memory",
            sessionId: "sess-test", cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.Count.ShouldBe(served);
        if (served == 0)
        {
            envelope.Data.Truncation.ShouldNotBeNull().ShouldContain(t => t.Floor == "absoluteRelevance" && Math.Abs(t.Threshold - 0.79) < 1e-9);
        }
    }

    private sealed class FakeStore : FakeMemoryStore
    {
        public IReadOnlyList<MemorySearchResult> StubResults { get; set; } = [];

        public IReadOnlyDictionary<string, RetrievalEvidence>? StubEvidence { get; set; }

        public FusionStats? StubStats { get; set; }

        public IReadOnlySet<string>? StubAllTermsMatched { get; set; }

        public double? StubRelevanceFloor { get; set; }

        public override Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchResults(StubResults, SearchTimings.Empty, null, StubEvidence, StubStats,
                AllTermsMatched: StubAllTermsMatched, RelevanceFloor: StubRelevanceFloor));
    }

    private sealed class StubMigrationGate(bool migrated) : IProjectIdsMigrationGate
    {
        public Task<bool> IsMigratedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(migrated);
    }
}
