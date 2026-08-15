using AiRaccoon.Core.Metrics;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Metrics;

/// <summary>
///     ADR-0056 (G2): a percentile gate must assert against values computed independently of the
///     product code — asserting against <c>TestData.Percentile</c> would be two untested
///     implementations agreeing, which proves nothing (TestData.cs:185-196 has no test of its own).
///     The expectations below are computed by hand in the comment on <see cref="Samples" />.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class StatisticsTests
{
    // 21 samples (1..21), handed to Percentile in descending order to exercise the sort.
    // Nearest-rank method: index = ceil(quantile * n) - 1 (0-based) into the ascending sort.
    //   p50: ceil(0.50 * 21) - 1 = ceil(10.5) - 1 = 11 - 1 = 10 -> sorted[10] = 11
    //   p95: ceil(0.95 * 21) - 1 = ceil(19.95) - 1 = 20 - 1 = 19 -> sorted[19] = 20
    //   p99: ceil(0.99 * 21) - 1 = ceil(20.79) - 1 = 21 - 1 = 20 -> sorted[20] = 21
    // n = 21 is deliberate: quantile * n is non-integer at all three points, so Math.Ceiling and
    // Math.Floor disagree here (Floor would give 10, 19, 20 instead) — a perturbation is visible.
    private static readonly IReadOnlyList<double> Samples =
        [.. Enumerable.Range(1, 21).Reverse().Select(i => (double)i)];

    [Fact]
    public void Percentile_P50_MatchesHandComputedNearestRank()
    {
        Statistics.Percentile(Samples, 0.50).ShouldBe(11.0);
    }

    [Fact]
    public void Percentile_P95_MatchesHandComputedNearestRank()
    {
        Statistics.Percentile(Samples, 0.95).ShouldBe(20.0);
    }

    [Fact]
    public void Percentile_P99_MatchesHandComputedNearestRank()
    {
        Statistics.Percentile(Samples, 0.99).ShouldBe(21.0);
    }

    [Fact]
    public void Min_ReturnsTheSmallestSample()
    {
        Statistics.Min(Samples).ShouldBe(1.0);
    }

    [Fact]
    public void Max_ReturnsTheLargestSample()
    {
        Statistics.Max(Samples).ShouldBe(21.0);
    }

    [Fact]
    public void Mean_ReturnsTheAverage()
    {
        // Sum of 1..21 = 21 * 22 / 2 = 231; mean = 231 / 21 = 11.
        Statistics.Mean(Samples).ShouldBe(11.0);
    }
}
