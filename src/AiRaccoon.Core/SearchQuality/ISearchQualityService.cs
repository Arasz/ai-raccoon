using AiRaccoon.Core.Memory.Fusion;

namespace AiRaccoon.Core.SearchQuality;

/// <summary>
///     Records memory_search quality signals: search event, follow-through (did the agent use the
///     result?), and usefulness grade. All three signals land on the same row, keyed by
///     <see cref="RecordSearchAsync" />'s <c>correlationId</c>.
///     See docs/plans/2026-08-11-search-quality-metric-plan.md.
/// </summary>
public interface ISearchQualityService
{
    /// <summary>
    ///     Creates a quality record for a memory_search call. Called alongside (not inside) the
    ///     search — fire-and-forget from the tool layer.
    ///     <paramref name="evidence" /> is the P4 sidecar joined to the served rows in served
    ///     order (P6a threading; P6b persists it). Null means absent-evidence: the row writes
    ///     exactly as before. Core stays JSON-free; serialization is P6b's.
    /// </summary>
    Task RecordSearchAsync(
        string correlationId,
        string query,
        string? scope,
        string? projectId,
        string? sessionId,
        int resultCount,
        IReadOnlyList<string> topSourceFiles,
        CancellationToken ct = default,
        IReadOnlyList<RetrievalEvidence>? evidence = null);

    /// <summary>
    ///     <see cref="RecordSearchAsync" />, best-effort: swallows and logs any failure instead of
    ///     throwing. The tool layer calls this, not the throwing form, so a quality-recording
    ///     failure never fails the search it describes.
    /// </summary>
    Task RecordSearchSafeAsync(
        string correlationId,
        string query,
        string? scope,
        string? projectId,
        int resultCount,
        IReadOnlyList<string> topSourceFiles,
        CancellationToken ct = default,
        IReadOnlyList<RetrievalEvidence>? evidence = null);

    /// <summary>
    ///     Records that the agent read a file that appeared in the search results.
    ///     Updates the existing row by <paramref name="correlationId" />.
    /// </summary>
    Task RecordFollowThroughAsync(
        string correlationId,
        string filePath,
        CancellationToken ct = default);

    /// <summary>
    ///     Records a human usefulness grade (1-5) for the search.
    ///     Updates the existing row by <paramref name="correlationId" />.
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

    /// <summary>Removes telemetry rows older than the retention window. Returns the count removed.</summary>
    Task<int> PurgeOlderThanAsync(long nowUnixSeconds, int retentionDays,
        CancellationToken cancellationToken = default);
}
