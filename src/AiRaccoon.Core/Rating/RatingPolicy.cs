using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Rating;

/// <summary>Half-life decay on age plus an access-count multiplier; see spec OQ-6.</summary>
public static class RatingPolicy
{
    public const double DefaultBaseScore = 0.5;
    public const double DefaultHalfLifeDays = 30;
    public const double DefaultAccessMultiplier = 0.1;

    public static double Rating(
        double baseScore,
        int accessCount,
        double ageDays,
        double halfLifeDays,
        double accessMultiplier = DefaultAccessMultiplier)
    {
        Guard.IsGreaterThanOrEqualTo(accessCount, 0);
        Guard.IsGreaterThanOrEqualTo(ageDays, 0);
        Guard.IsGreaterThan(halfLifeDays, 0);

        return baseScore * Math.Pow(0.5, ageDays / halfLifeDays) * (1 + accessCount * accessMultiplier);
    }
}
