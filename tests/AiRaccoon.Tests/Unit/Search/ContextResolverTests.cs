using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Search;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ContextResolverTests
{
    /// <summary>K5 (owner ruling, F24): the sandbox has priority — a workspace write with an
    /// explicit context still lands in the workspace, never in the named context.</summary>
    [Fact]
    public void Resolve_WithWorkspaceIdAndExplicitContext_LetsTheWorkspaceWin()
    {
        var request = new MemoryWriteRequest("acme", "note", "docs:api", WorkspaceId: "ws-1");

        ContextResolver.Resolve(request).ShouldBe("workspace:ws-1");
    }

    [Fact]
    public void Resolve_WithWorkspaceId_UsesWorkspaceContext()
    {
        var request = new MemoryWriteRequest("acme", "note", WorkspaceId: "ws-1");

        ContextResolver.Resolve(request).ShouldBe("workspace:ws-1");
    }

    [Fact]
    public void Resolve_WithOnlyProjectId_UsesProjectContext()
    {
        var request = new MemoryWriteRequest("acme", "note");

        ContextResolver.Resolve(request).ShouldBe("project:acme");
    }

    [Fact]
    public void Resolve_WithExplicitSharedContext_UsesSharedContext()
    {
        var request = new MemoryWriteRequest("acme", "note", "shared");

        ContextResolver.Resolve(request).ShouldBe("shared");
    }
}
