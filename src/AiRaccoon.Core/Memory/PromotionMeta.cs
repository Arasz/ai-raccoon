namespace AiRaccoon.Core.Memory;

/// <summary>What is waiting for the agent right now; always present (zero is informative, never absent).</summary>
public sealed record PromotionMeta(
    int WaitingPromotionsCount,
    double? PromotionsWaitTimeSeconds,
    IReadOnlyDictionary<string, int>? WaitingByProject)
{
    /// <summary>Per-project queue capacity/eviction pressure; present only once at least one project has queued rows.</summary>
    public IReadOnlyDictionary<string, PromotionCapacityInfo>? CapacityByProject { get; init; }
}
