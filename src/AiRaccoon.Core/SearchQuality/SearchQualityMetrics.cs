namespace AiRaccoon.Core.SearchQuality;

/// <summary>
///     Aggregated search-quality metrics over a time window. Returned by
///     <see cref="ISearchQualityService.GetMetricsAsync" />.
///     See docs/plans/2026-08-11-search-quality-metric-plan.md.
/// </summary>
public sealed record SearchQualityMetrics(
    int TotalSearches,
    int FollowThroughSearches,
    int GradedSearches,
    double AverageGrade,
    double FollowThroughRate,
    double Coverage,
    int SearchesPerDay);
