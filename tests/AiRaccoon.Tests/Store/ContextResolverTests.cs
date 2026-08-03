using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Store;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ContextResolverTests
{
    [Fact]
    public void Resolve_WithExplicitContext_UsesItOverWorkspaceAndProject()
    {
        var request = new MemoryWriteRequest("acme", "note", context: "docs:api", workspaceId: "ws-1");

        ContextResolver.Resolve(request).ShouldBe("docs:api");
    }

    [Fact]
    public void Resolve_WithWorkspaceId_UsesWorkspaceContext()
    {
        var request = new MemoryWriteRequest("acme", "note", workspaceId: "ws-1");

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
        var request = new MemoryWriteRequest("acme", "note", context: "shared");

        ContextResolver.Resolve(request).ShouldBe("shared");
    }
}
