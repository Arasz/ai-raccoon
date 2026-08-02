using AiRaccon.Core.Common;
using Shouldly;
using Xunit;

namespace AiRaccon.Tests.Domain;

public class ContextNamingTests
{
    [Theory]
    [InlineData("acme")]
    [InlineData("acme/backend")]
    public void ProjectContext_PrefixesProjectId(string projectId)
    {
        ContextNaming.ProjectContext(projectId).ShouldBe($"project:{projectId}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ProjectContext_WithBlankProjectId_Throws(string projectId)
    {
        Should.Throw<ArgumentException>(() => ContextNaming.ProjectContext(projectId));
    }

    [Fact]
    public void ProjectContext_WithNullProjectId_Throws()
    {
        Should.Throw<ArgumentException>(() => ContextNaming.ProjectContext(null!));
    }

    [Theory]
    [InlineData("ws-42")]
    [InlineData("ws-1")]
    public void WorkspaceContext_PrefixesWorkspaceId(string workspaceId)
    {
        ContextNaming.WorkspaceContext(workspaceId).ShouldBe($"workspace:{workspaceId}");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void WorkspaceContext_WithBlankWorkspaceId_Throws(string workspaceId)
    {
        Should.Throw<ArgumentException>(() => ContextNaming.WorkspaceContext(workspaceId));
    }

    [Fact]
    public void WorkspaceContext_WithNullWorkspaceId_Throws()
    {
        Should.Throw<ArgumentException>(() => ContextNaming.WorkspaceContext(null!));
    }
}
