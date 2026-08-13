namespace AiRaccoon.Core.Memory;

/// <summary>
///     One candidate waiting in the propose tier (the promote-from-queue unit). ScorerVersion
///     names the <see cref="PromotionScorer.Version" /> that produced Score — 0 is a legitimate default
///     for callers that never versioned a score, not an unset marker.
/// </summary>
public sealed record QueueCandidate(
    string Hash,
    string Path,
    string Value,
    string? SourceFile,
    double Score,
    IReadOnlyList<string> Reasons,
    int ScorerVersion = 0);

/// <summary>
///     One queued row as persisted, with its proposal timestamps and the scorer version that
///     produced Score (ADR-0018) — the auto-clear compares this against the current scorer's version.
/// </summary>
public sealed record PromotionQueueRow(
    string ProjectId,
    string Hash,
    string Path,
    string Value,
    string? SourceFile,
    double Score,
    IReadOnlyList<string> Reasons,
    long CreatedAt,
    long UpdatedAt,
    int ScorerVersion = 0);

/// <summary>
///     Queue occupancy and wait — the response-meta source. OldestWaitSeconds is the single
///     stalest row's age: an average can hide it once enough fresh rows join the same queue.
/// </summary>
public sealed record PromotionQueueStats(
    int TotalCount,
    double? AvgWaitSeconds,
    IReadOnlyDictionary<string, int> PerProject,
    double? OldestWaitSeconds = null);

/// <summary>
///     Queue occupancy for one project (or the whole bank when unscoped) — the response-meta
///     source. OccupyingProjects is the projects with queued rows, which the capacity split divides by.
/// </summary>
public sealed record PromotionWaitStats(
    int WaitingCount,
    double? AvgWaitSeconds,
    double? OldestWaitSeconds,
    int OccupyingProjects);

/// <summary>The row an eviction removed (for logging and the response).</summary>
public sealed record EvictedRow(string ProjectId, string Hash, double Score, string Reason);

/// <summary>Outcome of persisting candidates into the propose tier.</summary>
public sealed record ProposeOutcome(int Upserted, IReadOnlyList<EvictedRow> Evicted);

/// <summary>One candidate that could not be promoted: claimed from the queue but never shared.</summary>
public sealed record PromoteFailure(string ProjectId, string Hash, string Reason);

/// <summary>
///     Outcome of promoting from the queue: shared hashes, duplicates skipped, chunks absorbed
///     into already-represented files (or lost insert races), what still waits. Invariant: claimed =
///     PromotedHashes.Count + Absorbed + SkippedDuplicates + Failures.Count.
/// </summary>
public sealed record PromoteOutcome(
    IReadOnlyList<string> PromotedHashes,
    int SkippedDuplicates,
    IReadOnlyDictionary<string, int> RemainingByProject,
    int Absorbed = 0)
{
    public IReadOnlyList<PromoteFailure> Failures { get; init; } = [];
}
