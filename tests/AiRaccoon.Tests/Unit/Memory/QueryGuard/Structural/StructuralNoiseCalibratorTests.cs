using AiRaccoon.Core.Memory.QueryGuard.Structural;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.QueryGuard.Structural;

/// <summary>
///     <see cref="StructuralNoiseCalibrator" /> ports the research record's own out-of-fold
///     calibration method (`gen2/lofo2.py` <c>thr_at</c>): sort a sample of clean-content scores
///     descending, take the score at the target-FPR percentile. Operators use this to (re)derive
///     `queryGuard.structural.threshold.global` from their own query stream (docs/adr/0041) — the
///     shipped default is not meant to be the last word.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class StructuralNoiseCalibratorTests
{
    private static readonly double[] TenScores = [0.1, 0.5, 0.9, 0.3, 0.7, 0.2, 0.6, 0.4, 0.8, 1.0];

    [Fact]
    public void Threshold_MatchesTheReferenceImplementation_AtTwentyPercentTarget()
    {
        // gen2/lofo2.py thr_at(TenScores, 0.2) == 0.900000000001
        StructuralNoiseCalibrator.Threshold(TenScores, 0.2).ShouldBe(0.900000000001, 1e-9);
    }

    [Fact]
    public void Threshold_AtZeroTarget_IsAboveTheHighestScore()
    {
        // gen2/lofo2.py thr_at(TenScores, 0.0) == 1.000000001
        StructuralNoiseCalibrator.Threshold(TenScores, 0.0).ShouldBe(1.000000001, 1e-9);
    }

    [Fact]
    public void Threshold_AtFullTarget_IsAtOrBelowTheLowestScore()
    {
        // gen2/lofo2.py thr_at(TenScores, 1.0) == 0.10000000000100001
        StructuralNoiseCalibrator.Threshold(TenScores, 1.0).ShouldBe(0.10000000000100001, 1e-9);
    }

    [Fact]
    public void Threshold_WithEmptyCalibrationSample_Throws()
    {
        Should.Throw<ArgumentException>(() => StructuralNoiseCalibrator.Threshold([], 0.02));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    public void Threshold_WithTargetFprOutsideZeroToOne_Throws(double target)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => StructuralNoiseCalibrator.Threshold(TenScores, target));
    }
}
