using AiRaccoon.Core.Isolation;

namespace AiRaccoon.Infrastructure.Workspace;

public interface IWorkspaceService
{
    Task<Core.Isolation.Workspace> BeginAsync(string projectId, string? agentId = null, string? name = null,
        CancellationToken cancellationToken = default);

    Task<WorkspaceOutbox> GetStatusAsync(string projectId, string workspaceId,
        CancellationToken cancellationToken = default);

    Task<ConsolidationResult> ConsolidateAsync(
        string projectId, string workspaceId, IReadOnlyList<string> keep, CancellationToken cancellationToken = default);

    Task<int> DiscardAsync(string projectId, string workspaceId,
        CancellationToken cancellationToken = default);
}
