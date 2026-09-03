using AiRaccoon.Core.Memory.Fusion;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Fusion;

/// <summary>
///     The Stage-1 pure evidence calculator: raw RRF sums, strength normalization, and margin
///     stats over the §3 formulas. Every expectation here is a property of the arithmetic —
///     no scorer, floor, weight, or default is contacted.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class FusionEvidenceTests
{
    private const int K = 60;

    private const double Tolerance = 1e-9;

    private static NamedWeightedResults Leg(string name, double weight, params string[] hashes) =>
        new(hashes, weight, name);

    /// <summary>
    ///     G2 golden: every literal below was derived off-implementation (Python Fractions over
    ///     the §3 formulas: raw(h) = Σ w/(k+rank), maxPossible = 2/61, strength = raw/maxPossible,
    ///     margins on pre-max raws) — never by calling FromRaws, so a tautological
    ///     reimplementation cannot pass. The 1e-9 tolerance (not exact equality) is deliberate:
    ///     IEEE-754 summation order may differ in the last ulps, so this pins the math, not the
    ///     bit pattern.
    /// </summary>
    [Fact]
    public void FromRaws_TwoLegK60Fixture_MatchesHandComputedGoldens()
    {
        var legs = new[]
        {
            Leg("fts", 1.0, "a", "b", "c"),
            Leg("vector", 1.0, "b", "c"),
            Leg("skipped", 1.0),
            Leg("zero", 0.0, "a"),
        };

        var result = FusionEvidence.FromRaws(legs, K);

        var stats = result.Stats.ShouldNotBeNull();
        stats.MaxPossible.ShouldBe(0.03278688524590164, Tolerance);
        stats.ParticipatingLegs.ShouldBe(["fts", "vector", "zero"]);
        result.Evidence.Select(evidence => evidence.Hash).ShouldBe(["b", "c", "a"]);
        var byHash = result.Evidence.ToDictionary(evidence => evidence.Hash);
        byHash["b"].FusionStrength.ShouldBe(0.9919354838709677, Tolerance);
        byHash["c"].FusionStrength.ShouldBe(0.9760624679979518, Tolerance);
        byHash["a"].FusionStrength.ShouldBe(0.5, Tolerance);
        byHash["b"].Legs.ShouldBe([new LegRank("fts", 2), new LegRank("vector", 1)]);
        byHash["a"].Legs.ShouldBe([new LegRank("fts", 1), new LegRank("zero", 1)]);
        stats.TopMargin.ShouldNotBeNull();
        stats.TopMargin.Value.ShouldBe(0.01600206478255259, Tolerance);
        stats.TopVsMedian.ShouldNotBeNull();
        stats.TopVsMedian.Value.ShouldBe(0.01600206478255259, Tolerance);
    }

    /// <summary>An empty leg holds no opinion: it contributes no candidates and no maxPossible.</summary>
    [Fact]
    public void FromRaws_EmptyLeg_ContributesNeitherCandidatesNorMaxPossible()
    {
        var withEmpty = FusionEvidence.FromRaws(
            [Leg("fts", 1.0, "a", "b"), Leg("vector", 1.0)], K);
        var without = FusionEvidence.FromRaws([Leg("fts", 1.0, "a", "b")], K);

        var withStats = withEmpty.Stats.ShouldNotBeNull();
        var withoutStats = without.Stats.ShouldNotBeNull();
        withStats.MaxPossible.ShouldBe(withoutStats.MaxPossible, Tolerance);
        withStats.ParticipatingLegs.ShouldBe(["fts"]);
        withEmpty.Evidence.Select(evidence => evidence.Hash)
            .ShouldBe(without.Evidence.Select(evidence => evidence.Hash));
    }

    /// <summary>A weight-0 non-empty leg is neutral: the sums gain exactly 0.0 per occurrence.</summary>
    [Fact]
    public void FromRaws_WeightZeroLeg_LeavesEveryStrengthUnchanged()
    {
        var baseline = FusionEvidence.FromRaws(
            [Leg("fts", 1.0, "a", "b"), Leg("vector", 1.0, "b", "a")], K);
        var withZero = FusionEvidence.FromRaws(
            [Leg("fts", 1.0, "a", "b"), Leg("vector", 1.0, "b", "a"), Leg("zero", 0.0, "a", "b")], K);

        withZero.Evidence.Select(evidence => evidence.FusionStrength)
            .ShouldBe(baseline.Evidence.Select(evidence => evidence.FusionStrength));
        withZero.Stats.ShouldNotBeNull().ParticipatingLegs.ShouldBe(["fts", "vector", "zero"]);
    }

    /// <summary>One result defines its own strength (raw == max) but no margin exists yet.</summary>
    [Fact]
    public void FromRaws_SingleResult_DefinesStrengthButNullsBothMargins()
    {
        var result = FusionEvidence.FromRaws([Leg("fts", 1.0, "only")], K);

        var evidence = result.Evidence.ShouldHaveSingleItem();
        evidence.FusionStrength.ShouldBe(1.0, Tolerance);
        evidence.Legs.ShouldBe([new LegRank("fts", 1)]);
        var stats = result.Stats.ShouldNotBeNull();
        stats.TopMargin.ShouldBeNull();
        stats.TopVsMedian.ShouldBeNull();
        stats.MaxPossible.ShouldBe(1.0 / 61, Tolerance);
    }

    /// <summary>Tied hashes share one raw sum, so the top margin is exactly zero — no special-casing.</summary>
    [Fact]
    public void FromRaws_TiedHashes_ShareOneRawAndReportZeroTopMargin()
    {
        var result = FusionEvidence.FromRaws(
            [Leg("fts", 1.0, "a", "b"), Leg("vector", 1.0, "b", "a")], K);

        var strengths = result.Evidence.Select(evidence => evidence.FusionStrength).ToList();
        strengths.Count.ShouldBe(2);
        strengths[0].ShouldBe(strengths[1], Tolerance);
        var stats = result.Stats.ShouldNotBeNull();
        stats.TopMargin.ShouldNotBeNull();
        stats.TopMargin.Value.ShouldBe(0.0, Tolerance);
    }

    /// <summary>S7: all-weights-zero normalizes nothing — evidence null, stats null.</summary>
    [Fact]
    public void FromRaws_AllWeightsZero_ReturnsNoEvidenceAndNoStats()
    {
        var result = FusionEvidence.FromRaws(
            [Leg("fts", 0.0, "a"), Leg("vector", 0.0, "a", "b")], K);

        result.Evidence.ShouldBeEmpty();
        result.Stats.ShouldBeNull();
    }

    /// <summary>S7: a non-positive top raw is degenerate — strengths may exist, but margins cannot.</summary>
    [Fact]
    public void FromRaws_NonPositiveTopRaw_ReturnsNullStats()
    {
        var result = FusionEvidence.FromRaws([Leg("fts", -1.0, "a", "b")], K);

        result.Evidence.ShouldNotBeEmpty();
        result.Stats.ShouldBeNull();
    }

    /// <summary>
    ///     M4ii: a non-finite weight-derived raw nulls that hash's evidence only — the surviving
    ///     hash keeps its evidence and the call returns normally.
    /// </summary>
    [Fact]
    public void FromRaws_NonFiniteWeight_ExcludesTheAffectedHashAndContinues()
    {
        var result = FusionEvidence.FromRaws(
            [Leg("fts", 1.0, "ok"), Leg("broken", double.PositiveInfinity, "boom")], K);

        var evidence = result.Evidence.ShouldHaveSingleItem();
        evidence.Hash.ShouldBe("ok");
        var stats = result.Stats.ShouldNotBeNull();
        stats.TopMargin.ShouldBeNull();
        stats.TopVsMedian.ShouldBeNull();
    }

    /// <summary>M4ii NaN variant: nothing survives normalization, but the call still returns.</summary>
    [Fact]
    public void FromRaws_NaNWeight_ReturnsEmptyWithoutThrowing()
    {
        var result = FusionEvidence.FromRaws(
            [Leg("fts", 1.0, "ok"), Leg("broken", double.NaN, "boom")], K);

        result.Evidence.ShouldBeEmpty();
        result.Stats.ShouldBeNull();
    }

    /// <summary>
    ///     S1 golden: an even-count population medians as the average of the two middle raws.
    ///     Literals derived off-implementation like the G2 test (median = (1/62 + 1/63)/2,
    ///     topMargin = 63/124, topVsMedian = 7999/15624); same IEEE-754 tolerance rationale.
    /// </summary>
    [Fact]
    public void FromRaws_EvenCountPopulation_AveragesTheTwoMiddleRaws()
    {
        var result = FusionEvidence.FromRaws(
            [Leg("fts", 1.0, "a", "b", "c", "d"), Leg("vector", 1.0, "a")], K);

        result.Evidence.Select(evidence => evidence.Hash).ShouldBe(["a", "b", "c", "d"]);
        var byHash = result.Evidence.ToDictionary(evidence => evidence.Hash);
        byHash["a"].FusionStrength.ShouldBe(1.0, Tolerance);
        byHash["b"].FusionStrength.ShouldBe(0.49193548387096775, Tolerance);
        byHash["c"].FusionStrength.ShouldBe(0.48412698412698413, Tolerance);
        byHash["d"].FusionStrength.ShouldBe(0.4765625, Tolerance);
        var stats = result.Stats.ShouldNotBeNull();
        stats.MaxPossible.ShouldBe(0.03278688524590164, Tolerance);
        stats.TopMargin.ShouldNotBeNull();
        stats.TopMargin.Value.ShouldBe(0.5080645161290323, Tolerance);
        stats.TopVsMedian.ShouldNotBeNull();
        stats.TopVsMedian.Value.ShouldBe(0.511968766001024, Tolerance);
    }

    /// <summary>No participating leg means no distribution — null stats, no crash.</summary>
    [Fact]
    public void FromRaws_NoParticipatingLeg_ReturnsNoEvidenceAndNoStats()
    {
        var result = FusionEvidence.FromRaws([Leg("fts", 1.0), Leg("vector", 1.0)], K);

        result.Evidence.ShouldBeEmpty();
        result.Stats.ShouldBeNull();
    }
}
