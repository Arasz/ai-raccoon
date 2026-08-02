using AiRaccoon.Core.Workspace;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Domain;

public class WorkspaceTests
{
    [Fact]
    public void Constructor_WithValidValues_KeepsThem()
    {
        var workspace = new Workspace("ws-1", "acme");

        workspace.Id.ShouldBe("ws-1");
        workspace.ProjectId.ShouldBe("acme");
        workspace.Status.ShouldBe(WorkspaceStatus.Active);
    }

    [Fact]
    public void Constructor_WithExplicitStatus_KeepsIt()
    {
        var workspace = new Workspace("ws-1", "acme", WorkspaceStatus.Consolidating);

        workspace.Status.ShouldBe(WorkspaceStatus.Consolidating);
    }

    [Fact]
    public void Context_IsDerivedFromId()
    {
        var workspace = new Workspace("ws-1", "acme");

        workspace.Context.ShouldBe("workspace:ws-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Constructor_WithBlankId_Throws(string? id)
    {
        Should.Throw<ArgumentException>(() => new Workspace(id!, "acme"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Constructor_WithBlankProjectId_Throws(string? projectId)
    {
        Should.Throw<ArgumentException>(() => new Workspace("ws-1", projectId!));
    }
}
