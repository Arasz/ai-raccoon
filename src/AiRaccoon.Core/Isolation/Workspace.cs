using AiRaccoon.Core.Memory;
using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Isolation;

public sealed record Workspace
{
    private const int MaxLabelLength = 200;

    public Workspace(string id, string projectId, WorkspaceStatus status = WorkspaceStatus.Active,
        string? agentId = null, string? name = null)
    {
        Guard.IsNotNullOrWhiteSpace(id);
        Guard.IsNotNullOrWhiteSpace(projectId);
        GuardLabel(agentId, nameof(agentId));
        GuardLabel(name, nameof(name));

        Id = id;
        ProjectId = projectId;
        Status = status;
        AgentId = agentId;
        Name = name;
    }

    public string Id { get; }

    public string ProjectId { get; }

    public WorkspaceStatus Status { get; }

    /// <summary>Provenance only, not an identity — see docs/work/2026-08-09-memory-as-a-communication-layer-feedback.md §D2.</summary>
    public string? AgentId { get; }

    public string? Name { get; }

    public string Context => ContextNaming.WorkspaceContext(Id);

    /// <summary>The one legal transition into Closed (WP5b/A-F7): throws from any non-Active
    /// source, so a workspace can never be closed-into twice or reopened through this path.</summary>
    public Workspace Consolidate() => TransitionTo(WorkspaceStatus.Closed);

    /// <summary>The one legal transition into Discarded (WP5b/A-F7): throws from any non-Active
    /// source, mirroring <see cref="Consolidate" />.</summary>
    public Workspace Discard() => TransitionTo(WorkspaceStatus.Discarded);

    private Workspace TransitionTo(WorkspaceStatus terminal)
    {
        if (Status != WorkspaceStatus.Active)
        {
            throw new InvalidOperationException(
                $"Workspace '{Id}' cannot transition to '{terminal}' from status '{Status}'; only an Active workspace can.");
        }

        return new Workspace(Id, ProjectId, terminal, AgentId, Name);
    }

    /// <summary>Free text from any caller lands in a TEXT column: bounded length, no control characters.</summary>
    private static void GuardLabel(string? value, string name)
    {
        if (value is null)
        {
            return;
        }

        Guard.HasSizeLessThanOrEqualTo(value, MaxLabelLength, name);
        Guard.IsFalse(value.Any(char.IsControl), name, "must not contain control characters");
    }
}
