using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Memory;

public sealed record MemoryWriteRequest
{
    public MemoryWriteRequest(
        string projectId,
        string content,
        string? context = null,
        string? agentId = null,
        string? workspaceId = null)
    {
        Guard.IsNotNullOrWhiteSpace(projectId, nameof(projectId));
        Guard.IsNotNullOrWhiteSpace(content, nameof(content));

        ProjectId = projectId;
        Content = content;
        Context = context;
        AgentId = agentId;
        WorkspaceId = workspaceId;
    }

    public string ProjectId { get; }

    public string Content { get; }

    public string? Context { get; }

    public string? AgentId { get; }

    public string? WorkspaceId { get; }
}
