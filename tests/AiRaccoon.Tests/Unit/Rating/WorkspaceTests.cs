using AiRaccoon.Core.Isolation;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Rating;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
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
        var workspace = new Workspace("ws-1", "acme", WorkspaceStatus.Discarded);

        workspace.Status.ShouldBe(WorkspaceStatus.Discarded);
    }

    [Fact]
    public void Constructor_WithProvenance_KeepsIt()
    {
        var workspace = new Workspace("ws-1", "acme", agentId: "agent-a", name: "refactor the parser");

        workspace.AgentId.ShouldBe("agent-a");
        workspace.Name.ShouldBe("refactor the parser");
    }

    [Fact]
    public void Constructor_WithoutProvenance_LeavesItNull()
    {
        var workspace = new Workspace("ws-1", "acme");

        workspace.AgentId.ShouldBeNull();
        workspace.Name.ShouldBeNull();
    }

    [Fact]
    public void Constructor_WithOverLongAgentId_Throws() => Should.Throw<ArgumentException>(() => new Workspace("ws-1", "acme", agentId: new string('a', 201)));

    [Fact]
    public void Constructor_WithOverLongName_Throws() => Should.Throw<ArgumentException>(() => new Workspace("ws-1", "acme", name: new string('a', 201)));

    [Fact]
    public void Constructor_WithMaxLengthProvenance_IsAccepted()
    {
        var workspace = new Workspace("ws-1", "acme", agentId: new string('a', 200), name: new string('b', 200));

        workspace.AgentId!.Length.ShouldBe(200);
        workspace.Name!.Length.ShouldBe(200);
    }

    [Theory]
    [InlineData("agent\na")]
    [InlineData("agent\ta")]
    [InlineData("agent\0a")]
    public void Constructor_WithControlCharacterInAgentId_Throws(string agentId) => Should.Throw<ArgumentException>(() => new Workspace("ws-1", "acme", agentId: agentId));

    [Theory]
    [InlineData("name\na")]
    [InlineData("name\ta")]
    [InlineData("name\0a")]
    public void Constructor_WithControlCharacterInName_Throws(string name) => Should.Throw<ArgumentException>(() => new Workspace("ws-1", "acme", name: name));

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
    public void Constructor_WithBlankId_Throws(string? id) => Should.Throw<ArgumentException>(() => new Workspace(id!, "acme"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Constructor_WithBlankProjectId_Throws(string? projectId) => Should.Throw<ArgumentException>(() => new Workspace("ws-1", projectId!));

    // ── WP5b/A-F7: real state transitions, so CloseAsync(Active) stops being expressible ──

    [Fact]
    public void Consolidate_OnAnActiveWorkspace_ReturnsAClosedRecord()
    {
        var workspace = new Workspace("ws-1", "acme", agentId: "agent-a", name: "refactor the parser");

        var closed = workspace.Consolidate();

        closed.Status.ShouldBe(WorkspaceStatus.Closed);
        closed.Id.ShouldBe("ws-1");
        closed.ProjectId.ShouldBe("acme");
        closed.AgentId.ShouldBe("agent-a");
        closed.Name.ShouldBe("refactor the parser");
    }

    [Fact]
    public void Discard_OnAnActiveWorkspace_ReturnsADiscardedRecord()
    {
        var workspace = new Workspace("ws-1", "acme");

        var discarded = workspace.Discard();

        discarded.Status.ShouldBe(WorkspaceStatus.Discarded);
    }

    [Theory]
    [InlineData(WorkspaceStatus.Closed)]
    [InlineData(WorkspaceStatus.Discarded)]
    public void Consolidate_OnANonActiveWorkspace_Throws(WorkspaceStatus status)
    {
        var workspace = new Workspace("ws-1", "acme", status);

        Should.Throw<InvalidOperationException>(() => workspace.Consolidate());
    }

    [Theory]
    [InlineData(WorkspaceStatus.Closed)]
    [InlineData(WorkspaceStatus.Discarded)]
    public void Discard_OnANonActiveWorkspace_Throws(WorkspaceStatus status)
    {
        var workspace = new Workspace("ws-1", "acme", status);

        Should.Throw<InvalidOperationException>(() => workspace.Discard());
    }

    /// <summary>The transition is one-way: consolidating an already-consolidated (or discarded)
    /// record is not expressible as a no-op — it is a bug the caller must hear about.</summary>
    [Fact]
    public void Consolidate_TwiceInARow_TheSecondCallThrows()
    {
        var workspace = new Workspace("ws-1", "acme");
        var closed = workspace.Consolidate();

        Should.Throw<InvalidOperationException>(() => closed.Consolidate());
    }
}
