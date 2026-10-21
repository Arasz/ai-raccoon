using FluentValidation;
using System.Text.Json.Serialization;

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
    /// <summary>Effective write scope derived from context and workspace; a workspace wins.</summary>
    [JsonIgnore]
    public MemoryWriteScope Scope => !string.IsNullOrWhiteSpace(WorkspaceId)
        ? MemoryWriteScope.Workspace
        : Context == ContextNaming.SharedContext ? MemoryWriteScope.Shared : MemoryWriteScope.Project;

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
