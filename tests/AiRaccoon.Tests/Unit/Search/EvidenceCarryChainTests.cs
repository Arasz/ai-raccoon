using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Fusion;
using AiRaccoon.Infrastructure.Sqlite.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Search;

/// <summary>
///     P4 internal carry chain: the S1 evidence captured by
///     <see cref="ReciprocalRankFusion.FuseWithEvidence" /> rides the
///     Fused → Adjusted → Deferred record chain keyed by hash, surviving the
///     Merger's reorder, consolidation-drop, and floor-drop. The join is always by
///     hash — a positional (index-aligned) sidecar cannot satisfy the reorder test.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EvidenceCarryChainTests
{
    private const int K = 60;

    private const double ShippedLambda = 0.1;

    private const int Limit = 10;

    private static MemorySearchResult Candidate(
        string hash,
        double ranking,
        string? sourceFile = null,
        int chunkIndex = 0) => new(hash, ranking, hash + ".md", "snippet", sourceFile, chunkIndex);

    /// <summary>
    ///     The S1 capture fixture: "lone" fires in both legs at rank 1 (strength 1.0),
    ///     the cluster trio fires FTS-only at ranks 2-4. Fuse order is lone-first;
    ///     affinity (+0.1 per adjacent sibling) promotes the doubly-flanked middle
    ///     chunk past it. Hand-computed, never via the seam: lone raw 2/61; cluster
    ///     raws 1/62, 1/63, 1/64; re-fused single-leg order is identical, so Rank sees
    ///     lone 1.0, c3 61/62, c4 61/63, c5 61/64 and boosts c4 to 61/63+0.2 on top.
    /// </summary>
    private static (IReadOnlyList<MemorySearchResult> Fts, IReadOnlyList<MemorySearchResult> Vector) Legs()
    {
        var lone = Candidate("lone", -1.0);
        var c3 = Candidate("c3", -2.0, "cluster.md", 3);
        var c4 = Candidate("c4", -3.0, "cluster.md", 4);
        var c5 = Candidate("c5", -4.0, "cluster.md", 5);
        var fts = (IReadOnlyList<MemorySearchResult>)[lone, c3, c4, c5, Candidate("f1", -5.0), Candidate("f2", -6.0)];
        var vector = (IReadOnlyList<MemorySearchResult>)[lone with { Ranking = 0.88 }];
        return (fts, vector);
    }

    private static FusedSearchResult Fuse((IReadOnlyList<MemorySearchResult> Fts, IReadOnlyList<MemorySearchResult> Vector) legs)
    {
        var wired = ReciprocalRankFusion.FuseWithEvidence(
            [new NamedWeightedCandidates(legs.Fts, 1.0, "fts"), new NamedWeightedCandidates(legs.Vector, 1.0, "vector")],
            K, 0, int.MaxValue);
        return new FusedSearchResult(wired.Results, TimeSpan.Zero)
        {
            VectorCandidates = legs.Vector,
            FtsCandidates = legs.Fts,
            EvidenceByHash = wired.EvidenceByHash,
            Stats = wired.Stats
        };
    }

    /// <summary>
    ///     S3: affinity reorders between fuse and join, so evidence must be looked up
    ///     by hash. The served top (c4) is third at fuse; attaching evidence by
    ///     position would pin lone's strength-1.0 legs onto c4 and fail the per-row
    ///     identity below.
    /// </summary>
    [Fact]
    public void Carry_ThroughAffinityReorder_EvidenceStaysKeyedByHash()
    {
        var fused = Fuse(Legs());
        fused.Results[0].Hash.ShouldBe("lone");

        var merged = SearchResultMerger.Merge(
            fused.Results, Limit, 0.0, K, ShippedLambda, double.PositiveInfinity, DocScoreFormula.Max);
        merged[0].Hash.ShouldBe("c4", "affinity must genuinely reorder, or this test proves nothing");
        merged.Select(result => result.Hash).ShouldNotBe(fused.Results.Select(result => result.Hash));

        var adjusted = new AdjustedSearchResult(merged, TimeSpan.Zero)
        {
            EvidenceByHash = fused.EvidenceByHash,
            Stats = fused.Stats
        };
        var deferred = new DeferredSearchResult(adjusted.Results, TimeSpan.Zero)
        {
            FusionDiff = null,
            EvidenceByHash = adjusted.EvidenceByHash,
            Stats = adjusted.Stats
        };
        var envelope = new Core.Memory.SearchResults(
            deferred.Results, SearchTimings.Empty, null, deferred.EvidenceByHash, deferred.Stats);

        envelope.EvidenceByHash.ShouldNotBeNull();
        foreach (var row in envelope.Results)
        {
            var evidence = envelope.EvidenceByHash[row.Hash];
            evidence.Hash.ShouldBe(row.Hash);
            evidence.ShouldBe(fused.EvidenceByHash![row.Hash]);
        }

        envelope.Stats.ShouldBe(fused.Stats);
    }

    /// <summary>
    ///     Consolidation drops the weak adjacent sibling (threshold 0.01 vs the
    ///     c3→c4 gap 61/62−61/63 ≈ 0.0156): the dropped hash simply has no row, and
    ///     every surviving row still resolves its own evidence.
    /// </summary>
    [Fact]
    public void Carry_ThroughConsolidationDrop_SurvivorsStillResolve()
    {
        var fused = Fuse(Legs());

        var merged = SearchResultMerger.Merge(
            fused.Results, Limit, 0.0, K, ShippedLambda, 0.01, DocScoreFormula.Max);
        merged.Select(result => result.Hash).ShouldNotContain("c4");
        merged.Count.ShouldBeLessThan(fused.Results.Count);

        var envelope = new Core.Memory.SearchResults(
            merged, SearchTimings.Empty, null, fused.EvidenceByHash, fused.Stats);

        envelope.EvidenceByHash.ShouldNotBeNull();
        foreach (var row in envelope.Results)
        {
            envelope.EvidenceByHash[row.Hash].Hash.ShouldBe(row.Hash);
        }
    }

    /// <summary>
    ///     The Merger floor can drop even the strength-1.0 hash: after the c4 boost
    ///     renormalizes it to exactly 1.0, a 0.99 floor serves c4 alone. Evidence for
    ///     floor-dropped hashes is not required downstream — the join covers the
    ///     returned row only.
    /// </summary>
    [Fact]
    public void Carry_ThroughFloorDrop_ServedRowStillResolves()
    {
        var fused = Fuse(Legs());

        var merged = SearchResultMerger.Merge(
            fused.Results, Limit, 0.99, K, ShippedLambda, double.PositiveInfinity, DocScoreFormula.Max);
        merged.Select(result => result.Hash).ShouldBe(["c4"]);

        var envelope = new Core.Memory.SearchResults(
            merged, SearchTimings.Empty, null, fused.EvidenceByHash, fused.Stats);

        envelope.EvidenceByHash!["c4"].Hash.ShouldBe("c4");
        envelope.EvidenceByHash["c4"].ShouldBe(fused.EvidenceByHash!["c4"]);
    }

    /// <summary>
    ///     G1 at the P4 level: threading the sidecar changes neither order nor scores —
    ///     the wired path serves records equal to the plain Fuse path.
    /// </summary>
    [Fact]
    public void Merge_WithAndWithoutEvidenceWiring_ServesIdenticalRows()
    {
        var (fts, vector) = Legs();
        var plain = ReciprocalRankFusion.Fuse(
            [new WeightedResults(fts, 1.0), new WeightedResults(vector, 1.0)], K, 0, int.MaxValue);
        var fused = Fuse((fts, vector));

        var servedPlain = SearchResultMerger.Merge(
            plain, Limit, 0.0, K, ShippedLambda, double.PositiveInfinity, DocScoreFormula.Max);
        var servedWired = SearchResultMerger.Merge(
            fused.Results, Limit, 0.0, K, ShippedLambda, double.PositiveInfinity, DocScoreFormula.Max);

        servedWired.ShouldBe(servedPlain);
    }

    /// <summary>
    ///     Envelope-shape-unchanged golden: every new sidecar member defaults to null,
    ///     so record construction without evidence is byte-identical to before P4, and
    ///     the Core envelope keeps its positional shape with explicit nulls.
    /// </summary>
    [Fact]
    public void NewSidecarMembers_DefaultToNull()
    {
        var rows = (IReadOnlyList<MemorySearchResult>)[Candidate("h", 1.0)];

        var fused = new FusedSearchResult(rows, TimeSpan.Zero)
        {
            VectorCandidates = [],
            FtsCandidates = []
        };
        fused.EvidenceByHash.ShouldBeNull();
        fused.Stats.ShouldBeNull();

        var adjusted = new AdjustedSearchResult(rows, TimeSpan.Zero);
        adjusted.EvidenceByHash.ShouldBeNull();
        adjusted.Stats.ShouldBeNull();

        var deferred = new DeferredSearchResult(rows, TimeSpan.Zero) { FusionDiff = null };
        deferred.EvidenceByHash.ShouldBeNull();
        deferred.Stats.ShouldBeNull();
        DeferredSearchResult.Empty.EvidenceByHash.ShouldBeNull();
        DeferredSearchResult.Empty.Stats.ShouldBeNull();

        var envelope = new Core.Memory.SearchResults(rows, SearchTimings.Empty);
        envelope.EvidenceByHash.ShouldBeNull();
        envelope.Stats.ShouldBeNull();
        envelope.ShouldBe(new Core.Memory.SearchResults(rows, SearchTimings.Empty, null, null, null));
    }
}
