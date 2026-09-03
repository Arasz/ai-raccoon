using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Infrastructure.Sqlite.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Search;

/// <summary>
///     P2 S1 capture: FuseWithEvidence fuses exactly like Fuse (G1) while attaching
///     pre-normalization evidence (G3 shapes, vector-cosine extraction). Every numeric
///     literal below was derived off-implementation (Python floats over the §3 formulas,
///     same IEEE-754 op order as the fusion loop) — never by running the seam.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ReciprocalRankFusionEvidenceTests
{
    private const int K = 60;

    private const double Lambda = 0.01;

    // Tail T's post-Rank ranking: (1.0/64)/((1.0/61)+(1.0/62)). Exact because the
    // Rank renormalization divides by maxScore 1.0, and x/1.0 is exact.
    private const double Floor = 0.4804369918699186;

    private const int Limit = 10;

    private static MemorySearchResult Candidate(
        string hash,
        double ranking,
        string path,
        string? sourceFile = null,
        int chunkIndex = 0) => new(hash, ranking, path, "snippet", sourceFile, chunkIndex);

    private static IReadOnlyList<MemorySearchResult> RankAndFloor(
        IReadOnlyList<MemorySearchResult> fused,
        double floor) =>
    [
        .. SourceAffinityRanker.Rank(fused, Lambda, double.PositiveInfinity, DocScoreFormula.Max)
            .Where(result => result.Ranking >= floor)
            .Take(Limit)
    ];

    /// <summary>
    ///     G1: the S1 wiring must not perturb scores, order, or the floor. The fixture
    ///     exercises a fused tie (A/B broken by Path), a sibling flip (X over T under
    ///     lambda), and a floor-boundary tail (T kept at exactly floor, H cut).
    /// </summary>
    [Fact]
    public void FuseWithEvidence_MatchesFuse_ThroughRankAndFloor()
    {
        var fts = new[]
        {
            Candidate("a", -2.1, "b-tie.md"),
            Candidate("b", -2.4, "a-tie.md"),
            Candidate("m", -3.0, "m.md"),
            Candidate("t", -3.5, "t-tail.md"),
            Candidate("x", -4.0, "x-flip.md", "x.md", 0),
            Candidate("h", -4.5, "h-cut.md", "x.md", 1),
        };
        var vector = new[]
        {
            Candidate("b", 0.88, "a-tie.md"),
            Candidate("a", 0.77, "b-tie.md"),
        };

        var fusedBaseline = ReciprocalRankFusion.Fuse(
            [new WeightedResults(fts, 1.0), new WeightedResults(vector, 1.0)], K, 0, Limit);
        var wired = ReciprocalRankFusion.FuseWithEvidence(
            [new NamedWeightedCandidates(fts, 1.0, "fts"), new NamedWeightedCandidates(vector, 1.0, "vector")],
            K, 0, Limit);

        wired.Results.Select(result => result.Hash).ShouldBe(fusedBaseline.Select(result => result.Hash));
        wired.Results.Select(result => result.Ranking).ShouldBe(fusedBaseline.Select(result => result.Ranking));

        var servedPlain = RankAndFloor(fusedBaseline, Floor);
        var servedWired = RankAndFloor(wired.Results, Floor);

        servedWired.Select(result => result.Hash).ShouldBe(servedPlain.Select(result => result.Hash));
        servedWired.Select(result => result.Ranking).ShouldBe(servedPlain.Select(result => result.Ranking));

        fusedBaseline.Select(result => result.Hash).ShouldBe(["b", "a", "m", "t", "x", "h"]);
        servedWired.Select(result => result.Hash).ShouldBe(["b", "a", "m", "x", "t"]);
        servedWired.Count.ShouldBe(5);
        servedWired[^1].Ranking.ShouldBe(Floor);
        wired.Stats.ShouldNotBeNull();
        // Fuse-level floor is 0 here (the Floor applies post-Rank below), so every
        // fused hash is served and carries evidence.
        wired.EvidenceByHash.Keys.OrderBy(hash => hash, StringComparer.Ordinal)
            .ShouldBe(["a", "b", "h", "m", "t", "x"]);
    }

    /// <summary>
    ///     The sidecar covers served rows only: a hash cut by the Fuse-level floor keeps
    ///     its shape in the pre-floor Stats but carries no evidence entry.
    /// </summary>
    [Fact]
    public void FuseWithEvidence_FloorCutHash_CarriesNoEvidenceButShapesStats()
    {
        var fts = new[]
        {
            Candidate("b", -2.0, "b.md"),
            Candidate("a", -2.5, "a.md"),
            Candidate("cut", -3.0, "cut.md"),
        };
        var vector = new[]
        {
            Candidate("b", 0.88, "b.md"),
            Candidate("a", 0.77, "a.md"),
        };

        // Norms: b = 1.0, a = 61/62 ≈ 0.984, cut = 61/126 ≈ 0.484 — the 0.9 floor
        // keeps the pair and cuts only the fts-only tail.
        var wired = ReciprocalRankFusion.FuseWithEvidence(
            [new NamedWeightedCandidates(fts, 1.0, "fts"), new NamedWeightedCandidates(vector, 1.0, "vector")],
            K, 0.9, Limit);

        wired.Results.Select(result => result.Hash).ShouldBe(["b", "a"]);
        wired.EvidenceByHash.Keys.OrderBy(hash => hash, StringComparer.Ordinal).ShouldBe(["a", "b"]);
        wired.EvidenceByHash["b"].Cosine.ShouldBe(0.88);
        var stats = wired.Stats.ShouldNotBeNull();
        stats.ParticipatingLegs.ShouldBe(["fts", "vector"]);
        stats.TopMargin.ShouldNotBeNull();
    }

    /// <summary>G3: no legs means no distribution — empty results, no evidence, null stats.</summary>
    [Fact]
    public void FuseWithEvidence_NoLegs_ReturnsEmptyWithoutStats()
    {
        var wired = ReciprocalRankFusion.FuseWithEvidence([], K, 0, Limit);

        wired.Results.ShouldBeEmpty();
        wired.EvidenceByHash.ShouldBeEmpty();
        wired.Stats.ShouldBeNull();
    }

    /// <summary>G3: legs that fired nothing hold no opinion — same empty outcome, no crash.</summary>
    [Fact]
    public void FuseWithEvidence_AllLegsEmpty_ReturnsEmptyWithoutStats()
    {
        var wired = ReciprocalRankFusion.FuseWithEvidence(
            [new NamedWeightedCandidates([], 1.0, "fts"), new NamedWeightedCandidates([], 1.0, "vector")],
            K, 0, Limit);

        wired.Results.ShouldBeEmpty();
        wired.EvidenceByHash.ShouldBeEmpty();
        wired.Stats.ShouldBeNull();
    }

    /// <summary>G3: one result defines its own strength but no margin exists yet.</summary>
    [Fact]
    public void FuseWithEvidence_SingleResult_DefinesStrengthButNullsBothMargins()
    {
        var wired = ReciprocalRankFusion.FuseWithEvidence(
            [new NamedWeightedCandidates([Candidate("only", -1.5, "only.md")], 1.0, "fts")],
            K, 0, Limit);

        var hit = wired.Results.ShouldHaveSingleItem();
        hit.Ranking.ShouldBe(1.0);
        var evidence = wired.EvidenceByHash.ShouldHaveSingleItem();
        evidence.Key.ShouldBe("only");
        evidence.Value.FusionStrength.ShouldBe(1.0, 1e-9);
        evidence.Value.Legs.ShouldBe([new LegRank("fts", 1)]);
        evidence.Value.Cosine.ShouldBeNull();
        var stats = wired.Stats.ShouldNotBeNull();
        stats.TopMargin.ShouldBeNull();
        stats.TopVsMedian.ShouldBeNull();
        stats.MaxPossible.ShouldBe(1.0 / 61, 1e-9);
    }

    /// <summary>G3: a single leg's maxPossible spans that leg only, and every entry has one leg.</summary>
    [Fact]
    public void FuseWithEvidence_SingleLeg_ReportsLegOnlyMaxPossibleAndOneLegPerEntry()
    {
        var wired = ReciprocalRankFusion.FuseWithEvidence(
            [new NamedWeightedCandidates([Candidate("a", -2.0, "a.md"), Candidate("b", -2.5, "b.md")], 1.0, "fts")],
            K, 0, Limit);

        wired.Results.Select(result => result.Hash).ShouldBe(["a", "b"]);
        wired.Stats.ShouldNotBeNull().MaxPossible.ShouldBe(1.0 / 61, 1e-9);
        foreach (var entry in wired.EvidenceByHash.Values)
        {
            entry.Legs.Count.ShouldBe(1);
        }
    }

    /// <summary>
    ///     The vector leg's candidate Ranking is the fused cosine: it attaches to vector
    ///     participants only, and the FTS leg's (negative BM25) Ranking is never read.
    /// </summary>
    [Fact]
    public void FuseWithEvidence_VectorLeg_AttachesCosineOnlyToVectorParticipants()
    {
        var fts = new[]
        {
            Candidate("b", -2.0, "b.md"),
            Candidate("a", -2.5, "a.md"),
            Candidate("fts-only", -3.0, "c.md"),
        };
        var vector = new[]
        {
            Candidate("b", 0.88, "b.md"),
            Candidate("a", 0.77, "a.md"),
        };

        var wired = ReciprocalRankFusion.FuseWithEvidence(
            [new NamedWeightedCandidates(fts, 1.0, "fts"), new NamedWeightedCandidates(vector, 1.0, "vector")],
            K, 0, Limit);

        wired.EvidenceByHash["b"].Cosine.ShouldBe(0.88);
        wired.EvidenceByHash["a"].Cosine.ShouldBe(0.77);
        wired.EvidenceByHash["fts-only"].Cosine.ShouldBeNull();
    }

    /// <summary>M4i (basic): a NaN vector Ranking nulls that hash's Cosine only.</summary>
    [Fact]
    public void FuseWithEvidence_NaNVectorRanking_NullsCosineAndKeepsStrengthAndLegs()
    {
        var fts = new[]
        {
            Candidate("ok", -2.0, "ok.md"),
            Candidate("nan", -2.5, "nan.md"),
        };
        var vector = new[]
        {
            Candidate("nan", double.NaN, "nan.md"),
            Candidate("ok", 0.5, "ok.md"),
        };

        var wired = ReciprocalRankFusion.FuseWithEvidence(
            [new NamedWeightedCandidates(fts, 1.0, "fts"), new NamedWeightedCandidates(vector, 1.0, "vector")],
            K, 0, Limit);

        var nan = wired.EvidenceByHash["nan"];
        nan.Cosine.ShouldBeNull();
        nan.FusionStrength.ShouldBe(wired.EvidenceByHash["ok"].FusionStrength, 1e-9);
        nan.Legs.Count.ShouldBe(2);
    }
}
