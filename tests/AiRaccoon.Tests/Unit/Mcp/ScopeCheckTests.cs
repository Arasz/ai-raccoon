using AiRaccoon.Core.Projects;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ScopeCheckTests
{
    private readonly IScopeCheck _scope = new ScopeCheck(new ToolNameScope(), new SharedProjectScope());

    [Theory]
    [InlineData("memory_write")]
    [InlineData("memory_share")]
    [InlineData("memory_share_extract")]
    [InlineData("memory_sweep")]
    [InlineData("memory_set_ttl")]
    [InlineData("code_get")]
    [InlineData("memory_delete_context")]
    [InlineData("memory_workspace_create")]
    [InlineData("memory_workspace_future_operation")]
    [InlineData("memory_promotion_list")]
    [InlineData("memory_promotion_future_operation")]
    public void Check_OrdinaryOnlyToolsRefuseSharedAndAllowOrdinaryProjects(string toolName)
    {
        var result = _scope.Check("shared", toolName);
        result.IsAllowed.ShouldBeFalse();
        _scope.Check("acme", toolName).IsAllowed.ShouldBeTrue();
        _scope.Check("Shared", toolName).IsAllowed.ShouldBeTrue();
    }

    [Theory]
    [InlineData("memory_search")]
    [InlineData("memory_get")]
    [InlineData("memory_list")]
    [InlineData("memory_stats")]
    [InlineData("memory_watch_add")]
    [InlineData("memory_watch_remove")]
    [InlineData("memory_watch_status")]
    [InlineData("memory_watch_list")]
    [InlineData("unknown")]
    [InlineData("Memory_write")]
    [InlineData("Memory_workspace_create")]
    [InlineData("Memory_promotion_list")]
    [InlineData("memory_workspace")]
    [InlineData("memory_promotion")]
    [InlineData("memory_write_extra")]
    public void Check_OtherToolNamesAllowShared(string toolName)
    {
        var result = _scope.Check("shared", toolName);
        result.IsAllowed.ShouldBeTrue();
    }
}
