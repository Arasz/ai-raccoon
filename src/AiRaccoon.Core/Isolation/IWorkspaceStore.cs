namespace AiRaccoon.Core.Isolation;

/// <summary>
///     Persists workspace lifecycle records so started work is traceable: BeginAsync records an
///     Active row (created_at), CloseAsync marks it finished (status + closed_at). A workspace that
///     was begun but never closed is recoverable — the record survives a crash.
/// </summary>
public interface IWorkspaceStore
{
    Task BeginAsync(Workspace workspace, DateTimeOffset startedAt, CancellationToken cancellationToken = default);

    Task CloseAsync(string projectId, string workspaceId, WorkspaceStatus status, DateTimeOffset closedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Returns the row, or throws <see cref="UnknownWorkspaceException" /> unless it is Active for the project.</summary>
    Task<Workspace> RequireActiveAsync(string projectId, string workspaceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Atomically transitions an Active workspace to a terminal status and reports whether it
    ///     actually did — false when the row was already terminal or never existed (WP5b/A-F7): the
    ///     compare-and-swap that closes the TOCTOU window between a caller's own active-check and
    ///     its write, so a concurrent consolidate and discard on the same workspace cannot both
    ///     succeed. <paramref name="status" /> must be a terminal status (Active is rejected).
    ///     Default implementation forwards to <see cref="CloseAsync" /> and reports success
    ///     unconditionally, for stores with no atomic path of their own —
    ///     <c>SqliteWorkspaceStore</c> overrides with the real compare-and-swap.
    /// </summary>
    async Task<bool> TryCloseAsync(string projectId, string workspaceId, WorkspaceStatus status,
        DateTimeOffset closedAt, CancellationToken cancellationToken = default)
    {
        await CloseAsync(projectId, workspaceId, status, closedAt, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
