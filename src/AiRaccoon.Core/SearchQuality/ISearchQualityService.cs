namespace AiRaccoon.Core.SearchQuality;

/// <summary>
///     Records memory_search quality signals: search event, follow-through (did the agent use the
///     result?), and usefulness grade. All three signals land on the same row, keyed by
///     <see cref="RecordSearchAsync"/>'s <c>correlationId</c>.
///     See docs/plans/2026-08-11-search-quality-metric-plan.md.
/// </summary>
public interface ISearchQualityService
{
    /// <summary>
    ///     Creates a quality record for a memory_search call. Called alongside (not inside) the
    ///     search — fire-and-forget from the tool layer.
    /// </summary>
    Task RecordSearchAsync(
        string correlationId,
        string query,
        string? scope,
        string? projectId,
        string? sessionId,
        int resultCount,
        IReadOnlyList<string> topSourceFiles,
        CancellationToken ct = default);

    /// <summary>
    ///     Records that the agent read a file that appeared in the search results.
    ///     Updates the existing row by <paramref name="correlationId"/>.
    /// </summary>
    Task RecordFollowThroughAsync(
        string correlationId,
        string filePath,
        CancellationToken ct = default);

    /// <summary>
    ///     Records a human usefulness grade (1-5) for the search.
    ///     Updates the existing row by <paramref name="correlationId"/>.
    /// </summary>
    Task RecordGradeAsync(
        string projectId,
        string correlationId,
        int grade,
        string? note,
        CancellationToken ct = default);

    /// <summary>
    ///     Aggregates quality metrics for a project over a time window.
    /// </summary>
    Task<SearchQualityMetrics> GetMetricsAsync(
        string? projectId,
        DateTimeOffset from,
        CancellationToken ct = default);
}
