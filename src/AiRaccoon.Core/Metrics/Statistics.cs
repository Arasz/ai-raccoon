using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Metrics;

/// <summary>Pure percentile/min/max/mean over a sample list. No I/O, no state (ADR-0056, G2).</summary>
public static class Statistics
{
    /// <summary>Nearest-rank percentile: sorts ascending and takes ceil(quantile * n) - 1.</summary>
    public static double Percentile(IReadOnlyList<double> samples, double quantile)
    {
        Guard.HasSizeGreaterThan(samples, 0);
        Guard.IsBetweenOrEqualTo(quantile, 0.0, 1.0);

        var sorted = samples.OrderBy(v => v).ToList();
        var index = (int)Math.Ceiling(quantile * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    public static double Min(IReadOnlyList<double> samples)
    {
        Guard.HasSizeGreaterThan(samples, 0);
        return samples.Min();
    }

    public static double Max(IReadOnlyList<double> samples)
    {
        Guard.HasSizeGreaterThan(samples, 0);
        return samples.Max();
    }

    public static double Mean(IReadOnlyList<double> samples)
    {
        Guard.HasSizeGreaterThan(samples, 0);
        return samples.Average();
    }
}
