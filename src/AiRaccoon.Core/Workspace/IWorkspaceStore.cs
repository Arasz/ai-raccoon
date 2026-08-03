using AiRaccoon.Core.Workspace;

namespace AiRaccoon.Core.Workspace;

/// <summary>
/// Persists workspace lifecycle records so started work is traceable: BeginAsync records an
/// Active row (created_at), CloseAsync marks it finished (status + closed_at). A workspace that
/// was begun but never closed is recoverable — the record survives a crash (review feedback).
/// </summary>
public interface IWorkspaceStore
{
    Task BeginAsync(string projectId, string workspaceId, DateTimeOffset startedAt,
        CancellationToken cancellationToken = default);

    Task CloseAsync(string projectId, string workspaceId, WorkspaceStatus status, DateTimeOffset closedAt,
        CancellationToken cancellationToken = default);
}
