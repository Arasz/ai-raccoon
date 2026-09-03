using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite.Memory;
using Shouldly;
using Xunit;
using SearchResults = AiRaccoon.Infrastructure.Sqlite.Memory.SearchResults;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Unit.Search;

/// <summary>
///     P3 contract pin + rule matrix for the S1 cosine transport (plan §§2-4, normative §9 M3/M4).
///     (a) The vector candidate Ranking IS the fused cosine: FusedRank.Score must survive
///     BuildDualVectorResults and ByCosine verbatim into FuseWithEvidence's "vector" leg, whose
///     Ranking becomes evidence Cosine. (b) Non-finite rule matrix at the consumption point.
///     (c) Negative BM25 is health: Fuse never reads any Ranking but the "vector" leg's.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class VectorCosineTransportTests
{
    private const int K = 60;

    private const int Limit = 10;

    private const double Tolerance = 1e-9;

    private static MemorySearchResult Candidate(string hash, double ranking, string path) =>
        new(hash, ranking, path, "snippet");

    private static SqliteMemoryStore.VectorRow Row(string hash) =>
        new SqliteMemoryStore.VectorRow { Hash = hash, Path = $"{hash}.md", Value = $"value {hash}" };

    private static SqliteMemoryStore.SearchRow FtsRow(string hash, double ranking) =>
        new SqliteMemoryStore.SearchRow { Hash = hash, Ranking = ranking, Path = $"{hash}.md", Value = $"value {hash}" };

    /// <summary>
    ///     Builders carry scores verbatim: fused cosines (zero and negative are legitimate per
    ///     SimFromDistance) and raw BM25 magnitudes (routinely negative) reach Ranking untouched.
    /// </summary>
    [Fact]
    public void Builders_CarryScoresVerbatim_IncludingNegativeAndZero()
    {
        var built = SqliteMemoryStore.BuildDualVectorResults(
        [
            (Row: Row("h1"), Score: 0.95),
            (Row: Row("h2"), Score: 0.0),
            (Row: Row("h3"), Score: -0.4),
        ]);

        built.Select(candidate => candidate.Hash).ShouldBe(["h1", "h2", "h3"]);
        built.Select(candidate => candidate.Ranking).ShouldBe([0.95, 0.0, -0.4]);

        var fts = SqliteMemoryStore.BuildFtsResults([FtsRow("h1", -9.0), FtsRow("h2", -0.25)]);

        fts.Select(candidate => candidate.Ranking).ShouldBe([-9.0, -0.25]);
    }

    /// <summary>
    ///     End-to-end transport pin: StructureFusion FusedRank.Score flows through
    ///     BuildDualVectorResults and ByCosine into evidence Cosine bit-identical, while the FTS
    ///     leg (opposite order, negative BM25) shapes ranks only. A refactor corrupting the
    ///     implicit Ranking-carries-cosine chain breaks this test.
    /// </summary>
    [Fact]
    public void FusedScore_SurvivesThroughByCosine_ToEvidenceCosine()
    {
        var fused = StructureFusion.Rank(
            [new VectorHit("h1", 0.9), new VectorHit("h2", 0.2), new VectorHit("h3", -0.3)],
            [new VectorHit("h1", 0.5)],
            0.5, 10);

        fused.Select(rank => rank.Hash).ShouldBe(["h1", "h2", "h3"]);
        var scoreByHash = fused.ToDictionary(rank => rank.Hash, rank => rank.Score);
        scoreByHash["h1"].ShouldBe(0.7, Tolerance);
        scoreByHash["h2"].ShouldBe(0.1, Tolerance);
        scoreByHash["h3"].ShouldBe(-0.15, Tolerance);

        var rowsByHash = new Dictionary<string, SqliteMemoryStore.VectorRow>(StringComparer.Ordinal)
        {
            ["h1"] = Row("h1"),
            ["h2"] = Row("h2"),
            ["h3"] = Row("h3"),
        };
        var built = SqliteMemoryStore.BuildDualVectorResults(
            [.. fused.Select(rank => (Row: rowsByHash[rank.Hash], Score: rank.Score))]);
        foreach (var candidate in built)
        {
            candidate.Ranking.ShouldBe(scoreByHash[candidate.Hash]);
        }

        var searchResults = new SearchResults();
        searchResults.AddResults(
            new VectorSearchResult([built[0]], TimeSpan.Zero),
            new FtsSearchResult([], TimeSpan.Zero));
        searchResults.AddResults(
            new VectorSearchResult([built[1], built[2]], TimeSpan.Zero),
            new FtsSearchResult([], TimeSpan.Zero));
        var vectorCandidates = ModalityCandidates.ByCosine(searchResults);
        vectorCandidates.Select(candidate => candidate.Hash).ShouldBe(["h1", "h2", "h3"]);
        foreach (var candidate in vectorCandidates)
        {
            candidate.Ranking.ShouldBe(scoreByHash[candidate.Hash]);
        }

        var fts = SqliteMemoryStore.BuildFtsResults([FtsRow("h3", -9.0), FtsRow("h2", -5.0), FtsRow("h1", -1.0)]);
        var wired = ReciprocalRankFusion.FuseWithEvidence(
            [new NamedWeightedCandidates(fts, 1.0, "fts"), new NamedWeightedCandidates(vectorCandidates, 1.0, "vector")],
            K, 0, Limit);

        wired.Results.Count.ShouldBe(3);
        foreach (var hash in new[] { "h1", "h2", "h3" })
        {
            wired.EvidenceByHash[hash].Cosine.ShouldBe(scoreByHash[hash]);
        }
    }

    /// <summary>
    ///     Only the leg named exactly "vector" (ordinal, matching LegsFor) supplies Cosine: a
    ///     near-miss name still votes in ranks but yields null Cosine, while a high FTS Ranking
    ///     is never mistaken for one.
    /// </summary>
    [Fact]
    public void OnlyLegNamedVector_SuppliesCosine_OrdinalMatch()
    {
        var fts = new[] { Candidate("a", 0.99, "a.md") };
        var nearMiss = new[] { Candidate("a", 0.42, "a.md") };

        var wired = ReciprocalRankFusion.FuseWithEvidence(
            [new NamedWeightedCandidates(fts, 1.0, "fts"), new NamedWeightedCandidates(nearMiss, 1.0, "Vector")],
            K, 0, Limit);

        var evidence = wired.EvidenceByHash["a"];
        evidence.Cosine.ShouldBeNull();
        evidence.FusionStrength.ShouldBe(1.0, Tolerance);
        evidence.Legs.ShouldBe([new LegRank("fts", 1), new LegRank("Vector", 1)]);

        var control = ReciprocalRankFusion.FuseWithEvidence(
            [new NamedWeightedCandidates(fts, 1.0, "fts"), new NamedWeightedCandidates(nearMiss, 1.0, "vector")],
            K, 0, Limit);

        control.EvidenceByHash["a"].Cosine.ShouldBe(0.42);
    }

    /// <summary>
    ///     Non-finite rule matrix: NaN and ±Inf vector Rankings all null Cosine while strength,
    ///     legs, and serving survive (fail-open). DECISION (confirm-and-keep, not narrow): P2's
    ///     IsFinite extension to Inf stays — System.Text.Json rejects ALL non-finite doubles (M2
    ///     premise), so Inf would crash the S3/quality JSON write exactly like NaN; a fused cosine
    ///     is bounded in [-1,1] (SimFromDistance maps [0,2] there, Fused is a convex combination),
    ///     so ±Inf is corruption, never signal; narrowing would keep a crash path for zero gain.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteVectorRanking_NullsCosineAndKeepsStrengthAndLegs(double cosine)
    {
        var fts = new[]
        {
            Candidate("ok", -2.0, "ok.md"),
            Candidate("bad", -2.5, "bad.md"),
        };
        var vector = new[]
        {
            Candidate("bad", cosine, "bad.md"),
            Candidate("ok", 0.5, "ok.md"),
        };

        var wired = ReciprocalRankFusion.FuseWithEvidence(
            [new NamedWeightedCandidates(fts, 1.0, "fts"), new NamedWeightedCandidates(vector, 1.0, "vector")],
            K, 0, Limit);

        wired.Results.Select(result => result.Hash).ShouldContain("bad");
        var bad = wired.EvidenceByHash["bad"];
        bad.Cosine.ShouldBeNull();
        bad.FusionStrength.ShouldBe(wired.EvidenceByHash["ok"].FusionStrength, Tolerance);
        bad.Legs.ShouldBe([new LegRank("fts", 2), new LegRank("vector", 1)]);
    }

    /// <summary>
    ///     The null rule is non-finite-only: finite boundary cosines (-1, 0, +1, all reachable
    ///     via SimFromDistance) attach verbatim — a negative cosine is legitimate, not a hazard.
    /// </summary>
    [Fact]
    public void FiniteBoundaryCosines_AttachVerbatim()
    {
        var fts = new[]
        {
            Candidate("neg", -1.0, "neg.md"),
            Candidate("zero", -2.0, "zero.md"),
            Candidate("one", -3.0, "one.md"),
        };
        var vector = new[]
        {
            Candidate("neg", -1.0, "neg.md"),
            Candidate("zero", 0.0, "zero.md"),
            Candidate("one", 1.0, "one.md"),
        };

        var wired = ReciprocalRankFusion.FuseWithEvidence(
            [new NamedWeightedCandidates(fts, 1.0, "fts"), new NamedWeightedCandidates(vector, 1.0, "vector")],
            K, 0, Limit);

        wired.EvidenceByHash["neg"].Cosine.ShouldBe(-1.0);
        wired.EvidenceByHash["zero"].Cosine.ShouldBe(0.0);
        wired.EvidenceByHash["one"].Cosine.ShouldBe(1.0);
    }

    /// <summary>
    ///     M4iii health: negative BM25 magnitudes flow through — every served hash keeps evidence,
    ///     vector cosines stay exact, and the FTS-only hash keeps a defined strength with null Cosine.
    /// </summary>
    [Fact]
    public void NegativeBm25Rankings_FlowThroughAndNullNothing()
    {
        var fts = new[]
        {
            Candidate("best", -9.0, "best.md"),
            Candidate("mid", -5.0, "mid.md"),
            Candidate("fts-only", -0.5, "only.md"),
        };
        var vector = new[]
        {
            Candidate("mid", 0.6, "mid.md"),
            Candidate("best", 0.4, "best.md"),
        };

        var wired = ReciprocalRankFusion.FuseWithEvidence(
            [new NamedWeightedCandidates(fts, 1.0, "fts"), new NamedWeightedCandidates(vector, 1.0, "vector")],
            K, 0, Limit);

        wired.Results.Count.ShouldBe(3);
        foreach (var result in wired.Results)
        {
            wired.EvidenceByHash.ShouldContainKey(result.Hash);
        }

        wired.EvidenceByHash["best"].Cosine.ShouldBe(0.4);
        wired.EvidenceByHash["mid"].Cosine.ShouldBe(0.6);
        var only = wired.EvidenceByHash["fts-only"];
        only.Cosine.ShouldBeNull();
        only.FusionStrength.ShouldBeGreaterThan(0.0);
        only.Legs.ShouldBe([new LegRank("fts", 3)]);
        wired.Stats.ShouldNotBeNull();
    }

    /// <summary>
    ///     Fuse never reads FTS Rankings: identical ranks with healthy-negative, wild, and even
    ///     unphysical-positive magnitudes produce identical hashes, scores, evidence, and stats.
    /// </summary>
    [Fact]
    public void FtsRankingMagnitude_NeverPerturbsEvidence()
    {
        var vector = new[]
        {
            Candidate("b", 0.8, "b.md"),
            Candidate("a", 0.7, "a.md"),
        };
        FuseWithEvidenceResult Run(IReadOnlyList<MemorySearchResult> fts) =>
            ReciprocalRankFusion.FuseWithEvidence(
                [new NamedWeightedCandidates(fts, 1.0, "fts"), new NamedWeightedCandidates(vector, 1.0, "vector")],
                K, 0, Limit);

        var baseline = Run(FtsScaled(1.0));
        foreach (var other in new[] { Run(FtsScaled(100.0)), Run(FtsScaled(-1.0)) })
        {
            other.Results.Select(result => result.Hash).ShouldBe(baseline.Results.Select(result => result.Hash));
            other.Results.Select(result => result.Ranking).ShouldBe(baseline.Results.Select(result => result.Ranking));
            foreach (var hash in new[] { "a", "b" })
            {
                other.EvidenceByHash[hash].FusionStrength.ShouldBe(baseline.EvidenceByHash[hash].FusionStrength);
                other.EvidenceByHash[hash].Legs.ShouldBe(baseline.EvidenceByHash[hash].Legs);
                other.EvidenceByHash[hash].Cosine.ShouldBe(baseline.EvidenceByHash[hash].Cosine);
            }

            StatsShouldMatch(other.Stats, baseline.Stats);
        }

        IReadOnlyList<MemorySearchResult> FtsScaled(double scale) => new[]
        {
            Candidate("a", -2.0 * scale, "a.md"),
            Candidate("b", -2.5 * scale, "b.md"),
        };
    }

    private static void StatsShouldMatch(FusionStats? actual, FusionStats? expected)
    {
        var match = actual.ShouldNotBeNull();
        var want = expected.ShouldNotBeNull();
        match.MaxPossible.ShouldBe(want.MaxPossible, Tolerance);
        match.ParticipatingLegs.ShouldBe(want.ParticipatingLegs);
        match.TopMargin.ShouldBe(want.TopMargin);
        match.TopVsMedian.ShouldBe(want.TopVsMedian);
    }
}
