using AiRaccoon.Core.Common;

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
        Guard.GreaterThanOrEqualTo(accessCount, 0, nameof(accessCount));
        Guard.GreaterThanOrEqualTo(ageDays, 0, nameof(ageDays));
        Guard.GreaterThan(halfLifeDays, 0, nameof(halfLifeDays));

        return baseScore * Math.Pow(0.5, ageDays / halfLifeDays) * (1 + accessCount * accessMultiplier);
    }
}
