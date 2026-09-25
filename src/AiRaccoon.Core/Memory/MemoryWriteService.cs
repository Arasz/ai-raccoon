namespace AiRaccoon.Core.Memory;

/// <summary>The promotion reason tags and the refusal text a `shared` write reports — one place, so the wire strings agree.</summary>
public static class PromotionReasons
{
    /// <summary>
    ///     The agent named `shared` on a write. The strongest promotion signal available — an explicit
    ///     request, not a scorer's inference (docs/adr/0067).
    /// </summary>
    public const string AgentRequestedShare = "agent-requested-share";

    /// <summary>
    ///     The write asked for promotion, but the queue refused it: the hash was discarded earlier
    ///     (docs/adr/0026) or its value is already shared. The response says so instead of claiming a
    ///     queued review.
    /// </summary>
    public const string AgentRequestedRefused =
        "not-queued: agent-requested-share refused (discarded earlier or already shared)";

    /// <summary>
    ///     The write asked for promotion, and the pass's own capacity eviction removed the row
    ///     before it could be reviewed (docs/adr/0007). The queue did not refuse the request — it
    ///     could not hold it — so the response must not claim a discard or shared-twin refusal.
    /// </summary>
    public const string AgentRequestedEvicted =
        "not-queued: agent-requested-share evicted (queue at capacity)";
}

/// <summary>
///     The write path, composed with the promotion queue (docs/adr/0067). This is a Core service and
///     not a method on <see cref="IMemoryStore" /> for a concrete reason: `PromotionQueueService`
///     already takes `IMemoryStore`, so injecting `IPromotionQueue` into the store would be
///     store → queue → store, a genuine cycle at singleton scope. A third service composing both
///     ports has no cycle.
/// </summary>
public interface IMemoryWriteService
{
    Task<MemoryEntry> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IMemoryWriteService" />
public sealed class MemoryWriteService(IMemoryStore store, IPromotionQueue queue) : IMemoryWriteService
{
    /// <summary>
    ///     Derived from <see cref="PromotionScorer.MaxScore" /> — the scorer's declared output
    ///     ceiling — plus a full point, so an explicit request outranks every inference even if that
    ///     ceiling is retuned later (docs/adr/0067, owner ruling P1.2-b, 2026-09-22). Eviction is
    ///     deliberately left untouched: a request that cannot fit shows up in the queue's own metrics
    ///     rather than being silently dropped, and that is reversible if it proves wrong.
    /// </summary>
    public const double AgentRequestedScore = PromotionScorer.MaxScore + 1.0;

    public async Task<MemoryEntry> WriteAsync(MemoryWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The workspace wins over the shared request (owner ruling K5): a scratch write is not a
        // promotion request, so it never reaches the queue and never gets the shared rewrite.
        if (request.Context != ContextNaming.SharedContext || !string.IsNullOrWhiteSpace(request.WorkspaceId))
        {
            return await store.WriteAsync(request, cancellationToken);
        }

        // Naming `shared` asks for promotion; it does not perform one. The row lands in the caller's
        // own project — searchable immediately, never lost — and the request goes to the queue the
        // shared tier exists to have.
        var entry = await store
            .WriteAsync(request with { Context = ContextNaming.ProjectContext(request.ProjectId) },
                cancellationToken);
        if (!entry.Stored)
        {
            return entry;
        }

        var outcome = await queue.ProposeAsync(request.ProjectId,
                [
                    // K2 (F22): stamped with the current scorer version, not the 0 default — ClearStale
                    // deletes every row on a retired version before the next pass ranks, which would
                    // destroy the explicit request a below-floor note can never re-earn.
                    new QueueCandidate(entry.Hash, entry.Path, entry.Value, request.SourceFile,
                        AgentRequestedScore, [PromotionReasons.AgentRequestedShare], PromotionScorer.Version)
                ],
                cancellationToken);

        // F25: the upsert is refused for a remembered discard or an already-shared value twin, so
        // the queue — not the call — decides which reason is true here. Refused and NotQueued are
        // split because NotQueued is the state (absent from the queue) and a capacity eviction also
        // produces it; only Refused names a rejection of the request itself.
        return entry with
        {
            Reason = outcome.Refused.Contains(entry.Hash, StringComparer.Ordinal)
                ? PromotionReasons.AgentRequestedRefused
                : outcome.NotQueued.Contains(entry.Hash, StringComparer.Ordinal)
                    ? PromotionReasons.AgentRequestedEvicted
                    : $"queued-for-promotion: {PromotionReasons.AgentRequestedShare}"
        };
    }
}
