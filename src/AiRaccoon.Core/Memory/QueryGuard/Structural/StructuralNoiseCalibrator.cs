using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Memory.QueryGuard.Structural;

/// <summary>
///     Out-of-fold threshold calibration (docs/adr/0041): ported from the research record's own
///     method (`gen2/lofo2.py` <c>thr_at</c>) — sort a sample of clean-content scores descending
///     and take the score at the target false-positive-rate percentile. The record found the
///     operating threshold does not transfer across content distributions (F8), so
///     <paramref name="calibrationScores" /> must come from the deployment's own query stream, not
///     from this repository's research corpus.
/// </summary>
public static class StructuralNoiseCalibrator
{
    public static double Threshold(IReadOnlyList<double> calibrationScores, double targetFpr)
    {
        Guard.HasSizeGreaterThan(calibrationScores, 0);
        Guard.IsBetweenOrEqualTo(targetFpr, 0.0, 1.0);

        var sorted = calibrationScores.OrderDescending().ToArray();
        var k = (int)Math.Floor(targetFpr * sorted.Length);
        return k <= 0 ? sorted[0] + 1e-9 : sorted[k - 1] + 1e-12;
    }
}
