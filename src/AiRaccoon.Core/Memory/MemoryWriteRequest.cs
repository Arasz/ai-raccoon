using FluentValidation;

namespace AiRaccoon.Core.Memory;

public sealed record MemoryWriteRequest(
    string ProjectId,
    string Content,
    string? Context = null,
    string? AgentId = null,
    string? WorkspaceId = null)
{
    public sealed class Validator : AbstractValidator<MemoryWriteRequest>
    {
        public Validator()
        {
            RuleFor(x => x.ProjectId).NotNull().NotEmpty();
            RuleFor(x => x.Content).NotNull().NotEmpty();
        }
    }
}
