using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using AiRaccoon.Access;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Code;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     P5 (Stage 1): MCP envelope S3 join. The P4 sidecar (EvidenceByHash + Stats on Core
///     SearchResults) joins the memory_search response by hash as additive fields; Ranking
///     keeps its name, position, and semantics. Owns G4 (envelope compat) + G7 (docs).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MemorySearchEvidenceEnvelopeTests
{
    private readonly SpyCodeSearchService _codeSearch = new();
    private readonly FakeStore _store = new();
    private readonly MemoryTools _tools;

    public MemorySearchEvidenceEnvelopeTests()
    {
        var access = new MemoryAccessGuard(_store);
        var gate = new ToolGate(access, new FakePromotionQueue(), new NeverMigratingStore(), new AllowingRegistrationGuard());
        _tools = new MemoryTools(_store, gate, new SearchDispatcher(_store, _codeSearch, new NoOpSearchQualityService()),
            new QueryGuardService(new InMemorySettings()),
            new MemoryWriteService(_store, new FakePromotionQueue()), new NoOpMeasurementRecorder(),
            NullLogger<MemoryTools>.Instance);
    }

    /// <summary>
    ///     M5: absent evidence must serialize byte-identical to the legacy envelope — the new
    ///     members are null and the wire omits them, so old consumers see no new keys at all.
    ///     The byte assertion (not just null checks) is the proof: object shape can hide a
    ///     serialized-but-null leak.
    /// </summary>
    [Fact]
    public async Task Search_WithNoEvidence_OmitsNewFieldsFromWireBytes()
    {
        _store.StubResults = [new MemorySearchResult("h1", 1.0, "p.md", "snippet")];
        _store.StubEvidence = null;
        _store.StubStats = null;

        var envelope = await _tools.Search("acme", "widgets", kind: "memory",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.EvidenceByHash.ShouldBeNull();
        envelope.Data!.FusionStats.ShouldBeNull();
        var json = JsonSerializer.Serialize(envelope.Data, McpJsonUtilities.DefaultOptions);
        json.ShouldNotContain("evidenceByHash");
        json.ShouldNotContain("fusionStats");
        json.ShouldNotContain("fusionStrength");
        json.ShouldContain("\"results\"");
    }

    /// <summary>
    ///     The S3 join: served hashes pick up their strength/legs/cosine by hash; Ranking values
    ///     and order are untouched (zero behavior change — ordering still belongs to the store).
    /// </summary>
    [Fact]
    public async Task Search_WithEvidence_JoinsByHashWithoutTouchingRankingOrOrder()
    {
        _store.StubResults =
        [
            new MemorySearchResult("mem1", 1.0, "a.md", "first"),
            new MemorySearchResult("mem2", 0.8, "b.md", "second"),
        ];
        _store.StubEvidence = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
        {
            ["mem1"] = new RetrievalEvidence("mem1", 0.95,
                [new LegRank("fts", 1), new LegRank("vector", 1)], 0.87),
            ["mem2"] = new RetrievalEvidence("mem2", 0.42, [new LegRank("fts", 3)], null),
        };
        _store.StubStats = new FusionStats(0.2, 0.35, 0.0328, ["fts", "vector"]);

        var envelope = await _tools.Search("acme", "widgets", kind: "memory",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.Count.ShouldBe(2);
        envelope.Data!.Results[0].Hash.ShouldBe("mem1");
        envelope.Data!.Results[0].Ranking.ShouldBe(1.0);
        envelope.Data!.Results[1].Hash.ShouldBe("mem2");
        envelope.Data!.Results[1].Ranking.ShouldBe(0.8);
        var evidence = envelope.Data!.EvidenceByHash.ShouldNotBeNull();
        evidence.Count.ShouldBe(2);
        evidence["mem1"].FusionStrength.ShouldBe(0.95);
        evidence["mem1"].Legs.Count.ShouldBe(2);
        evidence["mem1"].Cosine.ShouldBe(0.87);
        evidence["mem2"].FusionStrength.ShouldBe(0.42);
        evidence["mem2"].Cosine.ShouldBeNull();
        var stats = envelope.Data!.FusionStats.ShouldNotBeNull();
        stats.TopMargin.ShouldBe(0.2);
        stats.TopVsMedian.ShouldBe(0.35);
        var json = JsonSerializer.Serialize(envelope.Data, McpJsonUtilities.DefaultOptions);
        json.ShouldContain("evidenceByHash");
        json.ShouldContain("fusionStrength");
        json.ShouldContain("fusionStats");
    }

    /// <summary>
    ///     S10 bounded payload: the envelope carries returned rows only. A sidecar entry for a
    ///     hash the floor/limit removed (not in served Results) must not leak onto the wire.
    /// </summary>
    [Fact]
    public async Task Search_WithEvidence_ExcludesSidecarHashesOutsideServedResults()
    {
        _store.StubResults = [new MemorySearchResult("mem1", 1.0, "a.md", "first")];
        _store.StubEvidence = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
        {
            ["mem1"] = new RetrievalEvidence("mem1", 0.9, [new LegRank("fts", 1)], null),
            ["floored"] = new RetrievalEvidence("floored", 0.3, [new LegRank("vector", 9)], 0.1),
        };
        _store.StubStats = new FusionStats(0.5, null, 0.0328, ["fts", "vector"]);

        var envelope = await _tools.Search("acme", "widgets", kind: "memory",
            cancellationToken: TestContext.Current.CancellationToken);

        var evidence = envelope.Data!.EvidenceByHash.ShouldNotBeNull();
        evidence.Count.ShouldBe(1);
        evidence.ShouldContainKey("mem1");
        evidence.ShouldNotContainKey("floored");
    }

    /// <summary>
    ///     Hash-namespace isolation (§8): doc and code corpora fuse independently, so a code hash
    ///     must never pick up doc evidence. The join iterates memory Results only — even a
    ///     sidecar entry keyed by a code hash is excluded because no served memory row claims it.
    /// </summary>
    [Fact]
    public async Task Search_KindBoth_CodeHashesNeverPickUpDocEvidence()
    {
        _store.StubResults = [new MemorySearchResult("mem1", 1.0, "a.md", "first")];
        _store.StubEvidence = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
        {
            ["mem1"] = new RetrievalEvidence("mem1", 0.9, [new LegRank("fts", 1)], null),
            ["code1"] = new RetrievalEvidence("code1", 0.99, [new LegRank("fts", 1)], null),
        };
        _store.StubStats = new FusionStats(0.1, null, 0.0328, ["fts", "vector"]);
        _codeSearch.StubResults = [new CodeSearchResult("code1", 1.0, "Foo.cs", "class Foo", 1, 10)];

        var envelope = await _tools.Search("acme", "widgets", kind: "both",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.ShouldHaveSingleItem().Hash.ShouldBe("mem1");
        envelope.Data!.Code!.ShouldHaveSingleItem().Hash.ShouldBe("code1");
        var evidence = envelope.Data!.EvidenceByHash.ShouldNotBeNull();
        evidence.ShouldContainKey("mem1");
        evidence.ShouldNotContainKey("code1",
            "the S3 join iterates memory Results only — code hashes live in a different namespace");
    }

    /// <summary>
    ///     G3: an empty response carries no stats and no per-row evidence (and does not crash),
    ///     even when the sidecar still describes a pre-floor population the floor removed.
    /// </summary>
    [Fact]
    public async Task Search_EmptyResults_OmitsEvidenceAndStats()
    {
        _store.StubResults = [];
        _store.StubEvidence = new Dictionary<string, RetrievalEvidence>(StringComparer.Ordinal)
        {
            ["orphan"] = new RetrievalEvidence("orphan", 0.5, [new LegRank("fts", 2)], null),
        };
        _store.StubStats = new FusionStats(0.1, null, 0.0328, ["fts", "vector"]);

        var envelope = await _tools.Search("acme", "widgets", kind: "memory",
            cancellationToken: TestContext.Current.CancellationToken);

        envelope.Data!.Results.ShouldBeEmpty();
        envelope.Data!.EvidenceByHash.ShouldBeNull("an empty response carries no per-row evidence");
        envelope.Data!.FusionStats.ShouldBeNull("G3: an empty response carries no stats");
        var json = JsonSerializer.Serialize(envelope.Data, McpJsonUtilities.DefaultOptions);
        json.ShouldNotContain("evidenceByHash");
        json.ShouldNotContain("fusionStats");
    }

    /// <summary>
    ///     G7: the tool description defines strength/legs/margin with the worked reading, states
    ///     what is not claimed (no relevance), and notes the pre-floor population (S5).
    /// </summary>
    [Fact]
    public void Search_ToolDescription_DefinesSignalsDisclaimsRelevanceAndNotesPreFloorPopulation()
    {
        var description = typeof(MemoryTools)
            .GetMethod(nameof(MemoryTools.Search), BindingFlags.Public | BindingFlags.Instance)
            .ShouldNotBeNull()
            .GetCustomAttribute<DescriptionAttribute>()
            .ShouldNotBeNull()
            .Description;

        description.ShouldContain("fusionStrength");
        description.ShouldContain("legs");
        description.ShouldContain("margin");
        description.ShouldContain("0.95");
        description.ShouldContain("thin");
        description.ShouldContain("relevance");
        description.ShouldContain("PRE-floor");
    }

    /// <summary>Only SearchAsync is exercised by this suite; the guard reads its own settings.</summary>
    private sealed class FakeStore : FakeMemoryStore
    {
        public IReadOnlyList<MemorySearchResult> StubResults { get; set; } = [];

        public IReadOnlyDictionary<string, RetrievalEvidence>? StubEvidence { get; set; }

        public FusionStats? StubStats { get; set; }

        public override Task<SearchResults> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchResults(StubResults, SearchTimings.Empty, null, StubEvidence, StubStats));
    }

    private sealed class SpyCodeSearchService : ICodeSearchService
    {
        public IReadOnlyList<CodeSearchResult> StubResults { get; set; } = [];

        public string? StubWarning { get; set; }

        public Task<CodeSearchResults> SearchAsync(CodeSearchQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodeSearchResults(StubResults, StubWarning));

        public Task<CodeEntry?> GetAsync(string projectId, string hash, CancellationToken cancellationToken = default) =>
            Task.FromResult<CodeEntry?>(null);
    }
}
