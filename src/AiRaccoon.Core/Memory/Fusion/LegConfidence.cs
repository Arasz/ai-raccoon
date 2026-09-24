using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Memory.Fusion;

/// <summary>
///     Confidence-weighted RRF's per-leg multiplier (issue #706, docs/plans/2026-08-17-issue-close-357-367.md
///     WP5): scales a leg's RRF weight by how decisively its own top scores separate. A leg whose rank-1
///     and rank-k scores are nearly flat is expressing no strong opinion and is scaled toward
///     <see cref="MinWeight" />; a leg with one clearly dominant hit is scaled toward <see cref="MaxWeight" />.
///     Scores must arrive best-first — exactly the order every RRF leg's candidates already carry
///     (<c>ModalityCandidates.ByBm25</c> sorts bm25 ascending, <c>ByCosine</c> sorts cosine descending) — so
///     this function reads only magnitudes and never has to branch on which metric produced them.
/// </summary>
public static class LegConfidence
{
    /// <summary>The multiplier's floor: a flat leg is downweighted, never zeroed out of the fusion.</summary>
    public const double MinWeight = 0.5;

    /// <summary>The multiplier's ceiling: a decisive leg is upweighted, never allowed to fully dominate.</summary>
    public const double MaxWeight = 2.0;

    /// <summary>
    ///     Scales <paramref name="baseWeight" /> by the normalized gap between <paramref name="bestFirstScores" />'s
    ///     first (rank-1) and rank-<paramref name="k" /> entries, clamped to [<see cref="MinWeight" />,
    ///     <see cref="MaxWeight" />]. Fewer than <paramref name="k" /> candidates falls back to the last one as
    ///     rank-k. A leg with zero or one candidate carries no separation to measure, so its base weight passes
    ///     through unscaled.
    /// </summary>
    public static double Weight(IReadOnlyList<double> bestFirstScores, int k, double baseWeight)
    {
        Guard.IsNotNull(bestFirstScores);
        Guard.IsGreaterThan(k, 0);
        Guard.IsGreaterThanOrEqualTo(baseWeight, 0.0);

        if (bestFirstScores.Count < 2)
        {
            return baseWeight;
        }

        var top = bestFirstScores[0];
        var bottom = bestFirstScores[Math.Min(k, bestFirstScores.Count) - 1];
        var scale = Math.Max(Math.Abs(top), Math.Abs(bottom));
        var normalizedGap = scale == 0.0 ? 0.0 : Math.Clamp(Math.Abs(top - bottom) / scale, 0.0, 1.0);

        var multiplier = MinWeight + (normalizedGap * (MaxWeight - MinWeight));
        return baseWeight * multiplier;
    }
}
