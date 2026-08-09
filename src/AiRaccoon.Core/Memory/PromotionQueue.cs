namespace AiRaccoon.Core.Memory;

/// <summary>One candidate waiting in the propose tier (the promote-from-queue unit).</summary>
public sealed record QueueCandidate(
    string Hash,
    string Path,
    string Value,
    string? SourceFile,
    double Score,
    IReadOnlyList<string> Reasons);

/// <summary>One queued row as persisted, with its proposal timestamps.</summary>
public sealed record PromotionQueueRow(
    string ProjectId,
    string Hash,
    string Path,
    string Value,
    string? SourceFile,
    double Score,
    IReadOnlyList<string> Reasons,
    long CreatedAt,
    long UpdatedAt);

/// <summary>Queue occupancy and wait — the response-meta source. OldestWaitSeconds is the single
/// stalest row's age: an average can hide it once enough fresh rows join the same queue.</summary>
public sealed record PromotionQueueStats(
    int TotalCount,
    double? AvgWaitSeconds,
    IReadOnlyDictionary<string, int> PerProject,
    double? OldestWaitSeconds = null);

/// <summary>The row an eviction removed (for logging and the response).</summary>
public sealed record EvictedRow(string ProjectId, string Hash, double Score, string Reason);

/// <summary>Outcome of persisting candidates into the propose tier.</summary>
public sealed record ProposeOutcome(int Upserted, IReadOnlyList<EvictedRow> Evicted);

/// <summary>Outcome of promoting from the queue: shared hashes, duplicates skipped, what still waits.</summary>
public sealed record PromoteOutcome(
    IReadOnlyList<string> PromotedHashes,
    int SkippedDuplicates,
    IReadOnlyDictionary<string, int> RemainingByProject);

