using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class MemoryWriteRequestTests
{
    [Fact]
    public void Constructor_WithValidValues_KeepsThem()
    {
        var request = new MemoryWriteRequest(
            "acme", "remember this", "docs:api", "agent-1", "ws-1");

        request.ProjectId.ShouldBe("acme");
        request.Content.ShouldBe("remember this");
        request.Context.ShouldBe("docs:api");
        request.AgentId.ShouldBe("agent-1");
        request.WorkspaceId.ShouldBe("ws-1");
    }

    [Fact]
    public void Constructor_WithOnlyRequiredFields_DefaultsOptionals()
    {
        var request = new MemoryWriteRequest("acme", "remember this");

        request.Context.ShouldBeNull();
        request.AgentId.ShouldBeNull();
        request.WorkspaceId.ShouldBeNull();
    }

    [Theory]
    [InlineData(null, null, MemoryWriteScope.Project)]
    [InlineData("", null, MemoryWriteScope.Project)]
    [InlineData("docs:api", null, MemoryWriteScope.Project)]
    [InlineData("Shared", null, MemoryWriteScope.Project)]
    [InlineData("shared", null, MemoryWriteScope.Shared)]
    [InlineData("shared", "scratch", MemoryWriteScope.Workspace)]
    [InlineData("docs:api", "scratch", MemoryWriteScope.Workspace)]
    [InlineData("shared", " ", MemoryWriteScope.Shared)]
    public void Scope_DerivesRequestedScopeWithWorkspacePrecedence(string? context, string? workspaceId,
        MemoryWriteScope expected) =>
        new MemoryWriteRequest("acme", "note", Context: context, WorkspaceId: workspaceId)
            .Scope.ShouldBe(expected);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validator_WithBlankProjectId_ReportsCamelCaseProperty(string? projectId)
    {
        var result = new MemoryWriteRequest.Validator()
            .Validate(new MemoryWriteRequest(projectId!, "content"));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "projectId");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validator_WithBlankContent_ReportsCamelCaseProperty(string? content)
    {
        var result = new MemoryWriteRequest.Validator()
            .Validate(new MemoryWriteRequest("acme", content!));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(e => e.PropertyName == "content");
    }

    [Fact]
    public void Validator_WithValidValues_Passes()
    {
        var result = new MemoryWriteRequest.Validator()
            .Validate(new MemoryWriteRequest("acme", "remember this"));

        result.IsValid.ShouldBeTrue();
    }
}
