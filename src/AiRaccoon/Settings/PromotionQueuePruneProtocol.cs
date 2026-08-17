namespace AiRaccoon.Settings;

/// <summary>
///     The wire contract of the control-plane promotion-queue-prune resource (ADR-0075 amendment),
///     shared by the endpoint that serves it and the store that calls it so the two halves cannot
///     drift.
/// </summary>
internal static class PromotionQueuePruneProtocol
{
    public const string Path = "/promotion-queue/prune";
}
