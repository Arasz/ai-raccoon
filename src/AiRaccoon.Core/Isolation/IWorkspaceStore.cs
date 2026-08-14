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
}
