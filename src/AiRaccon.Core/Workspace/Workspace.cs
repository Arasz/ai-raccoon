using AiRaccon.Core.Common;

namespace AiRaccon.Core.Workspace;

public sealed record Workspace
{
    public Workspace(string id, string projectId, WorkspaceStatus status = WorkspaceStatus.Active)
    {
        Guard.NotNullOrWhiteSpace(id, nameof(id));
        Guard.NotNullOrWhiteSpace(projectId, nameof(projectId));

        Id = id;
        ProjectId = projectId;
        Status = status;
    }

    public string Id { get; }

    public string ProjectId { get; }

    public WorkspaceStatus Status { get; }

    public string Context => ContextNaming.WorkspaceContext(Id);
}
