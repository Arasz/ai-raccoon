using AiRaccoon.Core.Memory.Fusion;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Fusion;

/// <summary>
///     Confidence-weighted RRF's per-leg multiplier (issue #706, docs/plans/2026-08-17-issue-close-357-367.md
///     WP5): a leg whose own top scores barely separate is expressing no strong opinion and should not
///     outweigh a leg that clearly prefers its rank-1 candidate. Scores are passed best-first, exactly as
///     every RRF leg's candidates already are (<c>ModalityCandidates.ByBm25</c>/<c>ByCosine</c>), so the
///     function never has to know whether it is reading bm25 (lower-is-better) or cosine (higher-is-better).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class LegConfidenceTests
{
    /// <summary>A leg whose rank-1..rank-k scores are identical has no opinion: the floor multiplier.</summary>
    [Fact]
    public void Weight_FlatLeg_ScalesToTheMinimumMultiplier()
    {
        double[] scores = [1.0, 1.0, 1.0, 1.0, 1.0];

        var weight = LegConfidence.Weight(scores, k: 5, baseWeight: 2.0);

        weight.ShouldBe(2.0 * LegConfidence.MinWeight, 0.0001);
    }

    /// <summary>A leg with one dominant rank-1 hit and a much lower tail is decisive: scaled well above neutral.</summary>
    [Fact]
    public void Weight_OneDominantHit_ScalesAboveTheFlatLegMultiplier()
    {
        double[] scores = [10.0, 2.0, 2.0, 2.0, 2.0];

        var weight = LegConfidence.Weight(scores, k: 5, baseWeight: 2.0);

        // top=10, rank-k=2, scale=10, gap=8 -> normalizedGap=0.8 -> multiplier=0.5+0.8*1.5=1.7.
        weight.ShouldBe(2.0 * 1.7, 0.0001);
    }

    /// <summary>A leg that never ran carries no opinion to scale — the base weight passes through untouched.</summary>
    [Fact]
    public void Weight_EmptyLeg_ReturnsTheBaseWeightUnscaled()
    {
        var weight = LegConfidence.Weight([], k: 5, baseWeight: 3.0);

        weight.ShouldBe(3.0);
    }

    /// <summary>One candidate has no rank-k partner to compare against, so it is also neutral, not a crash.</summary>
    [Fact]
    public void Weight_SingleCandidateLeg_ReturnsTheBaseWeightUnscaled()
    {
        var weight = LegConfidence.Weight([5.0], k: 5, baseWeight: 3.0);

        weight.ShouldBe(3.0);
    }

    /// <summary>
    ///     A bm25-shaped leg (values negative, best = most negative, already sorted best-first) must
    ///     still land the ceiling multiplier when its rank-1 stands far apart from its flat tail —
    ///     confirms sign never has to be special-cased by the caller.
    /// </summary>
    [Fact]
    public void Weight_NegativeBm25StyleScores_StillScalesAboveNeutral()
    {
        double[] scores = [-40.0, -10.0, -10.0, -10.0, -10.0];

        var weight = LegConfidence.Weight(scores, k: 5, baseWeight: 1.0);

        // top=-40, rank-k=-10, scale=40, gap=30 -> normalizedGap=0.75 -> multiplier=0.5+0.75*1.5=1.625.
        weight.ShouldBe(1.625, 0.0001);
    }

    /// <summary>An opposite-signed rank-k score can push the raw ratio past 1.0; the multiplier still never exceeds the ceiling.</summary>
    [Fact]
    public void Weight_GapRatioExceedsOne_ClampsAtTheMaximumMultiplier()
    {
        double[] scores = [5.0, -5.0];

        var weight = LegConfidence.Weight(scores, k: 2, baseWeight: 1.0);

        weight.ShouldBe(LegConfidence.MaxWeight, 0.0001);
    }

    /// <summary>Fewer candidates than k falls back to the leg's last candidate as the rank-k score, instead of throwing.</summary>
    [Fact]
    public void Weight_FewerCandidatesThanK_UsesTheLastCandidateAsRankK()
    {
        double[] scores = [8.0, -8.0, -8.0];

        var weight = LegConfidence.Weight(scores, k: 5, baseWeight: 1.0);

        // Only 3 candidates for k=5, so rank-k falls back to index 2 (the last): top=8, rank-k=-8,
        // scale=8, gap=16 -> raw ratio 2.0, clamped to 1.0 -> multiplier=MaxWeight.
        weight.ShouldBe(LegConfidence.MaxWeight, 0.0001);
    }

    [Fact]
    public void Weight_NonPositiveK_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => LegConfidence.Weight([1.0, 2.0], k: 0, baseWeight: 1.0));
    }

    /// <summary>Measurement sweeps (issue #706) vary bounds without touching the shipped defaults.</summary>
    [Fact]
    public void Weight_CustomBounds_ScalesWithinTheProvidedRangeInsteadOfTheDefaults()
    {
        double[] scores = [10.0, 0.0, 0.0, 0.0, 0.0];

        var weight = LegConfidence.Weight(scores, k: 5, baseWeight: 1.0, minWeight: 0.3, maxWeight: 3.0);

        // normalizedGap=1.0 (a dominant rank-1 over a zero tail) -> multiplier = maxWeight exactly.
        weight.ShouldBe(3.0, 0.0001);
    }

    [Fact]
    public void Weight_OmittedBounds_DefaultsToTheClassConstants()
    {
        double[] scores = [1.0, 1.0, 1.0, 1.0, 1.0];

        var weight = LegConfidence.Weight(scores, k: 5, baseWeight: 1.0);

        weight.ShouldBe(LegConfidence.MinWeight, 0.0001);
    }
}
