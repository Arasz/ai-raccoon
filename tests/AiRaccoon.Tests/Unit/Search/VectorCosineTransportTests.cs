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
///     P3 contract pin + rule matrix for the S1 cosine transport (plan §§2-4, normative §9 M3/M4):
///     evidence Cosine is the vector leg's raw content cosine
///     (<see cref="MemorySearchResult.ContentCosine" />), never the alpha-fused
///     <see cref="MemorySearchResult.Ranking" /> that orders candidates. (a) BuildDualVectorResults
///     carries both scores verbatim, and ContentCosine (not Ranking) survives ByCosine into
///     FuseWithEvidence's "vector" leg evidence. (b) Non-finite rule matrix at the consumption
///     point. (c) Negative BM25 is health: Fuse never reads any Ranking but the "vector" leg's.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class VectorCosineTransportTests
{
    private const int K = 60;

    private const int Limit = 10;

    private const double Tolerance = 1e-9;

    private static MemorySearchResult Candidate(string hash, double ranking, string path, double? contentCosine = null) =>
        new(hash, ranking, path, "snippet", ContentCosine: contentCosine);

    private static SqliteMemoryStore.VectorRow Row(string hash) =>
        new SqliteMemoryStore.VectorRow { Hash = hash, Path = $"{hash}.md", Value = $"value {hash}" };

    private static SqliteMemoryStore.SearchRow FtsRow(string hash, double ranking) =>
        new SqliteMemoryStore.SearchRow { Hash = hash, Ranking = ranking, Path = $"{hash}.md", Value = $"value {hash}" };

    /// <summary>
    ///     Builders carry both scores verbatim and independently: the alpha-fused score (zero and
    ///     negative are legitimate per SimFromDistance) reaches Ranking, and the row's own raw
    ///     content cosine reaches ContentCosine — the two differ whenever structure fusion blended
    ///     in a non-zero or absent structure term. FTS results carry no ContentCosine: raw
    ///     BM25 magnitudes (routinely negative) reach Ranking untouched.
    /// </summary>
    [Fact]
    public void Builders_CarryScoresVerbatim_IncludingNegativeAndZero()
    {
        var built = SqliteMemoryStore.BuildDualVectorResults(
        [
            new SqliteMemoryStore.RankedVectorRow(Row("h1"), 0.95, 0.8),
            new SqliteMemoryStore.RankedVectorRow(Row("h2"), 0.0, 0.0),
            new SqliteMemoryStore.RankedVectorRow(Row("h3"), -0.4, -0.6),
        ]);

        built.Select(candidate => candidate.Hash).ShouldBe(["h1", "h2", "h3"]);
        built.Select(candidate => candidate.Ranking).ShouldBe([0.95, 0.0, -0.4]);
        built.Select(candidate => candidate.ContentCosine).ShouldBe([0.8, 0.0, -0.6]);

        var fts = SqliteMemoryStore.BuildFtsResults([FtsRow("h1", -9.0), FtsRow("h2", -0.25)]);

        fts.Select(candidate => candidate.Ranking).ShouldBe([-9.0, -0.25]);
        fts.ShouldAllBe(candidate => candidate.ContentCosine == null);
    }

    /// <summary>
    ///     Gate: the vector leg's raw content cosine — not the alpha-fused Ranking that orders
    ///     it — is what survives BuildDualVectorResults and ByCosine into evidence Cosine. h1 has a
    ///     participating structure hit (fused 0.7, content 0.9 — the positive control: a structure
    ///     vector must not perturb the reported cosine); h2/h3 have none (fused = 0.5 × content
    ///     exactly, the measured red state) — both must report their content cosine, not the fused
    ///     score. The fused Ranking is unchanged by this fix: it still drives BuildDualVectorResults
    ///     ordering and the served result count/shape below, pinned alongside the cosine assertions.
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

        // The content-only cosines fed into StructureFusion.Rank above — the ground truth a
        // consumer reading evidence Cosine as "content similarity" expects back verbatim.
        var contentCosineByHash = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["h1"] = 0.9,
            ["h2"] = 0.2,
            ["h3"] = -0.3,
        };
        // h2/h3 (no structure hit) reproduce the review's measured red state exactly: fused is
        // half of content, because alpha defaults to 0.5 and an absent structure sim scores 0.
        (scoreByHash["h2"] / contentCosineByHash["h2"]).ShouldBe(0.5, Tolerance);
        (scoreByHash["h3"] / contentCosineByHash["h3"]).ShouldBe(0.5, Tolerance);

        var rowsByHash = new Dictionary<string, SqliteMemoryStore.VectorRow>(StringComparer.Ordinal)
        {
            ["h1"] = Row("h1"),
            ["h2"] = Row("h2"),
            ["h3"] = Row("h3"),
        };
        var built = SqliteMemoryStore.BuildDualVectorResults(
            [.. fused.Select(rank => new SqliteMemoryStore.RankedVectorRow(rowsByHash[rank.Hash], rank.Score, contentCosineByHash[rank.Hash]))]);
        foreach (var candidate in built)
        {
            candidate.Ranking.ShouldBe(scoreByHash[candidate.Hash], "the fused score still orders candidates, unchanged by this fix");
            candidate.ContentCosine.ShouldBe(contentCosineByHash[candidate.Hash]);
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
            candidate.ContentCosine.ShouldBe(contentCosineByHash[candidate.Hash]);
        }

        var fts = SqliteMemoryStore.BuildFtsResults([FtsRow("h3", -9.0), FtsRow("h2", -5.0), FtsRow("h1", -1.0)]);
        var wired = ReciprocalRankFusion.FuseWithEvidence(
            [new NamedWeightedCandidates(fts, 1.0, "fts"), new NamedWeightedCandidates(vectorCandidates, 1.0, "vector")],
            K, 0, Limit);

        wired.Results.Count.ShouldBe(3, "the fused Ranking/RRF math is untouched by this fix — same served count");
        foreach (var hash in new[] { "h1", "h2", "h3" })
        {
            wired.EvidenceByHash[hash].Cosine.ShouldBe(contentCosineByHash[hash],
                "evidence Cosine is the row's own content cosine, never the alpha-fused score");
        }
    }

    /// <summary>
    ///     Positive control for the gate above: genuinely similar vectors — a near-duplicate entry
    ///     embedding and its query — derive their content cosine from their own coordinates (the
    ///     dot product of L2-normalized vectors, the vec0 cosine metric), and that magnitude
    ///     survives distance → SimFromDistance → BuildDualVectorResults → ByCosine →
    ///     FuseWithEvidence verbatim as evidence Cosine. A null, constant, or halved score cannot
    ///     satisfy this together with the gate above: a similar pair must read high (~1).
    /// </summary>
    [Fact]
    public void GenuinelySimilarVectors_ReportsHighContentCosineThroughTheTransport()
    {
        var query = L2([1.0, 0.4, -0.2]);
        var entry = L2([1.0, 0.4005, -0.1997]);
        var contentCosine = query.Zip(entry, (left, right) => left * right).Sum();
        contentCosine.ShouldBeGreaterThan(0.999,
            "the fixture vectors must be genuinely similar, or this positive control proves nothing");

        // The store's own derivation: vec0 reports cosine distance, SimFromDistance maps it back.
        var distance = 1.0 - contentCosine;
        StructureFusion.SimFromDistance(distance).ShouldBe(contentCosine, Tolerance);

        // The measured red state for a row with no structure vector: fused is exactly half the
        // content cosine — far from the similarity a reader of evidence Cosine must see.
        var fusedScore = StructureFusion.Rank([new VectorHit("near", contentCosine)], [], 0.5, 10).Single().Score;
        fusedScore.ShouldBe(contentCosine / 2, Tolerance);

        var built = SqliteMemoryStore.BuildDualVectorResults(
        [
            new SqliteMemoryStore.RankedVectorRow(
                new SqliteMemoryStore.VectorRow { Hash = "near", Path = "near.md", Value = "value near", Distance = distance },
                fusedScore,
                StructureFusion.SimFromDistance(distance))
        ]);

        var searchResults = new SearchResults();
        searchResults.AddResults(
            new VectorSearchResult(built, TimeSpan.Zero),
            new FtsSearchResult([], TimeSpan.Zero));
        var wired = ReciprocalRankFusion.FuseWithEvidence(
            [new NamedWeightedCandidates(ModalityCandidates.ByCosine(searchResults), 1.0, "vector")],
            K, 0, Limit);

        var reported = wired.EvidenceByHash["near"].Cosine.ShouldNotBeNull();
        reported.ShouldBe(contentCosine, Tolerance, "the transport preserves the real content cosine end to end");
        reported.ShouldBeGreaterThan(0.99, "a genuinely similar vector reports a high cosine");
    }

    private static double[] L2(double[] vector)
    {
        var norm = Math.Sqrt(vector.Sum(component => component * component));
        return [.. vector.Select(component => component / norm)];
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
        var nearMiss = new[] { Candidate("a", 0.42, "a.md", contentCosine: 0.42) };

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
    ///     Non-finite rule matrix: NaN and ±Inf ContentCosine values all null Cosine while strength,
    ///     legs, and serving survive (fail-open). DECISION (confirm-and-keep, not narrow): P2's
    ///     IsFinite extension to Inf stays — System.Text.Json rejects ALL non-finite doubles (M2
    ///     premise), so Inf would crash the S3/quality JSON write exactly like NaN; a content cosine
    ///     is bounded in [-1,1] (SimFromDistance maps [0,2] there), so ±Inf is corruption, never
    ///     signal; narrowing would keep a crash path for zero gain.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteContentCosine_NullsCosineAndKeepsStrengthAndLegs(double cosine)
    {
        var fts = new[]
        {
            Candidate("ok", -2.0, "ok.md"),
            Candidate("bad", -2.5, "bad.md"),
        };
        var vector = new[]
        {
            Candidate("bad", 0.0, "bad.md", contentCosine: cosine),
            Candidate("ok", 0.0, "ok.md", contentCosine: 0.5),
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
            Candidate("neg", 0.0, "neg.md", contentCosine: -1.0),
            Candidate("zero", 0.0, "zero.md", contentCosine: 0.0),
            Candidate("one", 0.0, "one.md", contentCosine: 1.0),
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
            Candidate("mid", 0.0, "mid.md", contentCosine: 0.6),
            Candidate("best", 0.0, "best.md", contentCosine: 0.4),
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
            Candidate("b", 0.0, "b.md", contentCosine: 0.8),
            Candidate("a", 0.0, "a.md", contentCosine: 0.7),
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
