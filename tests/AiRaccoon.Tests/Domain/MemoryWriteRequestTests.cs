using AiRaccoon.Core.Memory;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Domain;

public class MemoryWriteRequestTests
{
    [Fact]
    public void Constructor_WithValidValues_KeepsThem()
    {
        var request = new MemoryWriteRequest(
            "acme", "remember this", context: "docs:api", agentId: "agent-1", workspaceId: "ws-1");

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
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Constructor_WithBlankProjectId_Throws(string? projectId)
    {
        Should.Throw<ArgumentException>(() => new MemoryWriteRequest(projectId!, "content"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Constructor_WithBlankContent_Throws(string? content)
    {
        Should.Throw<ArgumentException>(() => new MemoryWriteRequest("acme", content!));
    }
}
