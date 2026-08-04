using FluentValidation;

namespace AiRaccoon.Core.Memory;

public sealed record MemoryWriteRequest(
    string ProjectId,
    string Content,
    string? Context = null,
    string? AgentId = null,
    string? WorkspaceId = null,
    string? SourceFile = null,
    string? Section = null)
{
    public sealed class Validator : AbstractValidator<MemoryWriteRequest>
    {
        public Validator()
        {
            RuleFor(x => x.ProjectId).NotNull().NotEmpty();
            RuleFor(x => x.Content).NotNull().NotEmpty();
            RuleFor(x => x.SourceFile).MaximumLength(1024);
            RuleFor(x => x.Section).MaximumLength(256);
        }
    }
}
