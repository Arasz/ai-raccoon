using FluentValidation;

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

    public sealed class Validator : AbstractValidator<MemoryWriteRequest>
    {
        public Validator()
        {
            RuleFor(x => x.ProjectId).NotNull().NotEmpty();
            RuleFor(x => x.Content).NotNull().NotEmpty();
        }
    }
}
