namespace AiRaccoon.Core.Memory;

/// <summary>What is waiting for the asking project right now; always present (zero is informative, never absent).</summary>
public sealed record PromotionMeta(
    int WaitingPromotionsCount,
    double? PromotionsWaitTimeSeconds)
{
    /// <summary>The asking project's queue capacity/eviction pressure; present only once it has queued rows.</summary>
    public PromotionCapacityInfo? Capacity { get; init; }

    /// <summary>
    ///     The project's single stalest queued row's age — nothing else drains a propose-only queue, and
    ///     an average wait can hide one very old row once enough fresh rows join the same queue.
    /// </summary>
    public double? OldestWaitSeconds { get; init; }

    /// <summary>Correlation id linking this search response to its quality record; set only on memory_search.</summary>
    public string? CorrelationId { get; init; }
}
