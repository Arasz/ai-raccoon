namespace AiRaccoon.Core.Watch;

/// <summary>
///     The result of one <see cref="IWatchService.AddAsync" /> call: watches pruned because the new
///     registration contains them (empty when nothing was pruned), and the covering watch's path
///     when the add was an idempotent no-op (exact literal-path re-add) — never both non-empty.
/// </summary>
public sealed record WatchAddOutcome(IReadOnlyList<string> Pruned, string? AbsorbedBy);

/// <summary>
///     Watch service port (S2): the surface the MCP tools call and the pipeline implements.
///     Core types only — infrastructure-free.
/// </summary>
public interface IWatchService
{
    /// <summary>
    ///     Registers a watch. No-overlapping-watches (docs/work/2026-08-21-code-search-implementation-plan.md
    ///     §2.2/§5.5): a path contained by an existing watch is rejected with
    ///     <see cref="WatchOverlapException" /> naming the covering watch (nothing written); adding
    ///     a broader watch prunes every watch it contains (reported in the result); an exact
    ///     literal-path re-add is an idempotent no-op reporting <see cref="WatchAddOutcome.AbsorbedBy" />.
    /// </summary>
    Task<WatchAddOutcome> AddAsync(string projectId, string path, CancellationToken cancellationToken = default);

    Task RemoveAsync(string projectId, string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WatchStatus>> StatusAsync(string projectId, CancellationToken cancellationToken = default);

    Task<bool> IsEnabledAsync(string projectId, CancellationToken cancellationToken = default);

    Task<bool> IsPathAllowedAsync(string projectId, string path, CancellationToken cancellationToken = default);
}
