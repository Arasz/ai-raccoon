namespace AiRaccoon.Core.Memory;

/// <summary>The reasons a row reaches the promotion queue. One place, so the wire strings agree.</summary>
public static class PromotionReasons
{
    /// <summary>
    ///     The agent named `shared` on a write. The strongest promotion signal available — an explicit
    ///     request, not a scorer's inference (docs/adr/0067).
    /// </summary>
    public const string AgentRequestedShare = "agent-requested-share";
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
    ///     Above the scorer's range, so an explicit request outranks every inference. Eviction is
    ///     deliberately left untouched: a request that cannot fit shows up in the queue's own metrics
    ///     rather than being silently dropped, and that is reversible if it proves wrong.
    /// </summary>
    public const double AgentRequestedScore = 1.0;

    public async Task<MemoryEntry> WriteAsync(MemoryWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Context != ContextNaming.SharedContext)
        {
            return await store.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // Naming `shared` asks for promotion; it does not perform one. The row lands in the caller's
        // own project — searchable immediately, never lost — and the request goes to the queue the
        // shared tier exists to have.
        var entry = await store
            .WriteAsync(request with { Context = ContextNaming.ProjectContext(request.ProjectId) },
                cancellationToken)
            .ConfigureAwait(false);
        if (!entry.Stored)
        {
            return entry;
        }

        await queue.ProposeAsync(request.ProjectId,
                [
                    new QueueCandidate(entry.Hash, entry.Path, entry.Value, request.SourceFile,
                        AgentRequestedScore, [PromotionReasons.AgentRequestedShare])
                ],
                cancellationToken)
            .ConfigureAwait(false);

        return entry with { Reason = $"queued-for-promotion: {PromotionReasons.AgentRequestedShare}" };
    }
}
