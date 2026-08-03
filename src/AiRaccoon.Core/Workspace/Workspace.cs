using AiRaccoon.Core.Common;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Workspace;

public sealed record Workspace
{
    public Workspace(string id, string projectId, WorkspaceStatus status = WorkspaceStatus.Active)
    {
        Guard.IsNotNullOrWhiteSpace(id);
        Guard.IsNotNullOrWhiteSpace(projectId);

        Id = id;
        ProjectId = projectId;
        Status = status;
    }

    public string Id { get; }

    public string ProjectId { get; }

    public WorkspaceStatus Status { get; }

    public string Context => ContextNaming.WorkspaceContext(Id);
}
