using AiRaccoon.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     P6b — Stage-1 response-shape series on the existing metrics path (plan §5, normative §9):
///     search.fusion.top_strength, search.fusion.top_margin, search.fusion.legs_fired.
///     Same queryHash rule as every other series (null for code-adjacent kinds); skip-null for
///     margins (M8 — a 0.0 sentinel would poison Stage-2 distributions); a throwing recorder
///     still returns full results (G6 fail-open).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MemorySearchFusionSignalMetricsTests
{
    private readonly StubCodeSearch _codeSearch = new();
    private readonly EvidenceStore _store = new();
    private readonly SpyMeasurementRecorder _recorder = new();

    private MemoryTools CreateTools(IMeasurementRecorder? recorder = null) =>
        new(_store,
            new ToolGate(new MemoryAccessGuard(_store), new FakePromotionQueue(), new NeverMigratingStore(),
                new AllowingRegistrationGuard()),
            new SearchDispatcher(_store, _codeSearch, new NoOpSearchQualityService()),
            new QueryGuardService(new InMemorySettings()),
            new MemoryWriteService(_store, new FakePromotionQueue()),
            recorder ?? _recorder,
            NullLogger<MemoryTools>.Instance);

    private void StubTwoLegSearch()
    {
        _store.StubResults =
        [
            new MemorySearchResult("hash-b", 0.8, "b.md", "second"),
            new MemorySearchResult("hash-a", 0.9, "a.md", "first")
        ];
        _store.StubEvidence = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
        {
            ["hash-a"] = new RetrievalEvidence("hash-a", 1.0,
                [new LegRank("fts", 1), new LegRank("vector", 1)], 0.82),
            ["hash-b"] = new RetrievalEvidence("hash-b", 0.5, [new LegRank("fts", 2)], null)
        };
        _store.StubStats = new FusionStats(0.5, null, 0.0328, ["fts", "vector"]);
    }

    private IReadOnlyList<Measurement> FusionSignalSeries() =>
        _recorder.Recorded.Where(m =>
                m.Name is "search.fusion.top_strength" or "search.fusion.top_margin" or "search.fusion.legs_fired")
            .ToList();

    [Fact]
    public async Task Search_WithEvidenceAndStats_RecordsTopStrengthTopMarginAndLegsFired()
    {
        StubTwoLegSearch();
        var tools = CreateTools();

        var envelope = await tools.Search("acme", "widgets", kind: "memory",
            cancellationToken: TestContext.Current.CancellationToken);

        var series = FusionSignalSeries();
        series.Count.ShouldBe(3, "plan §5 names exactly three response-shape series");
        series.Select(m => m.Name).ShouldBe(
            ["search.fusion.top_strength", "search.fusion.top_margin", "search.fusion.legs_fired"],
            "series emit in plan-§5 order");
        series.ShouldAllBe(m => m.Kind == MeasurementKind.Gauge, "shape signals are gauges, like the fusion-diff precedent");
        series.Single(m => m.Name == "search.fusion.top_strength").Value.ShouldBe(1.0,
            "top_strength is the max over served rows with evidence — reorder-invariant, not rank-1-served (0.5 here)");
        series.Single(m => m.Name == "search.fusion.top_margin").Value.ShouldBe(0.5);
        series.Single(m => m.Name == "search.fusion.legs_fired").Value.ShouldBe(2);
        series.ShouldAllBe(m => m.QueryHash != null, "kind=memory keeps its query hash on every series");
        series.ShouldAllBe(m => m.CorrelationId == envelope.Meta.CorrelationId,
            "every series joins the quality row by correlation id");
    }

    [Fact]
    public async Task Search_SingleResult_OmitsTopMarginButKeepsStrengthAndLegsFired()
    {
        _store.StubResults = [new MemorySearchResult("hash-a", 0.9, "a.md", "only")];
        _store.StubEvidence = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
        {
            ["hash-a"] = new RetrievalEvidence("hash-a", 1.0, [new LegRank("fts", 1)], null)
        };
        // Single-result stats: margins null (fewer than 2 results), strength domain defined.
        _store.StubStats = new FusionStats(null, null, 0.0164, ["fts"]);
        var tools = CreateTools();

        await tools.Search("acme", "widgets", kind: "memory",
            cancellationToken: TestContext.Current.CancellationToken);

        var series = FusionSignalSeries();
        series.Select(m => m.Name).ShouldBe(
            ["search.fusion.top_strength", "search.fusion.legs_fired"],
            "M8: a missing margin emits no series — a 0.0 sentinel would poison Stage-2 distributions");
    }

    [Fact]
    public async Task Search_KindBoth_FusionSignalSeriesCarryNullQueryHash()
    {
        StubTwoLegSearch();
        _codeSearch.StubResults = [new CodeSearchResult("code-hash", 1.0, "Foo.cs", "class Foo", 1, 10)];
        var tools = CreateTools();

        await tools.Search("acme", "widgets", kind: "both",
            cancellationToken: TestContext.Current.CancellationToken);

        var series = FusionSignalSeries();
        series.Count.ShouldBe(3, "kind=both still records the memory leg's shape — only the query hash is excluded");
        series.ShouldAllBe(m => m.QueryHash == null,
            "a kind=both query hash would leak a code-adjacent query the same way the S6 rule prevents");
    }

    [Fact]
    public async Task Search_WithoutEvidenceOrStats_EmitsNoFusionSignalSeries()
    {
        _store.StubResults = [new MemorySearchResult("mem-hash", 0.9, "p.md", "memory hit")];
        var tools = CreateTools();

        await tools.Search("acme", "widgets", kind: "memory",
            cancellationToken: TestContext.Current.CancellationToken);

        FusionSignalSeries().ShouldBeEmpty("absent evidence writes exactly as before — no new series appear");
        _recorder.Recorded.ShouldNotBeEmpty("the existing phase series still record");
    }

    [Fact]
    public async Task Search_WhenRecorderThrows_StillReturnsFullResults()
    {
        StubTwoLegSearch();
        var tools = CreateTools(new ThrowingMeasurementRecorder());

        var envelope = await tools.Search("acme", "widgets", kind: "memory",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.Count.ShouldBe(2, "G6: telemetry write failure never fails a search");
    }

    /// <summary>Store stub carrying the P4 sidecar (evidence + stats) beside the served rows.</summary>
    private sealed class EvidenceStore : FakeMemoryStore
    {
        public IReadOnlyList<MemorySearchResult> StubResults { get; set; } = [];

        public IReadOnlyDictionary<string, RetrievalEvidence>? StubEvidence { get; set; }

        public FusionStats? StubStats { get; set; }

        public override Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchResults(StubResults, SearchTimings.Empty, null, StubEvidence, StubStats));
    }

    private sealed class StubCodeSearch : ICodeSearchService
    {
        public IReadOnlyList<CodeSearchResult> StubResults { get; set; } = [];

        public Task<CodeSearchResults> SearchAsync(CodeSearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodeSearchResults(StubResults, null));

        public Task<CodeEntry?> GetAsync(string projectId, string hash, CancellationToken cancellationToken = default) =>
            Task.FromResult<CodeEntry?>(null);
    }

    private sealed class SpyMeasurementRecorder : IMeasurementRecorder
    {
        public List<Measurement> Recorded { get; } = [];

        public void Record(Measurement measurement) => Recorded.Add(measurement);
    }

    private sealed class ThrowingMeasurementRecorder : IMeasurementRecorder
    {
        public void Record(Measurement measurement) =>
            throw new InvalidOperationException("telemetry write failure (injected)");
    }
}
