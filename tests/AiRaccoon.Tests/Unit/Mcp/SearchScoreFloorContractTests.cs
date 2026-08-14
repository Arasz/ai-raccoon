using System.Reflection;
using AiRaccoon.Core.Memory;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     ADR-0047: the fused ranking is max-normalized, so the floor is a fraction of the top hit —
///     it must not ship as an opt-out default that silently truncates a caller's requested limit.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SearchScoreFloorContractTests
{
    [Fact]
    public void SearchQuery_DefaultsTheRelativeFloor_ToOff()
    {
        new SearchQuery("acme", "search").MinRelativeScore.ShouldBe(0.0);
    }

    [Fact]
    public void MemorySearchTool_DefaultsTheRelativeFloor_ToOff()
    {
        var parameter = typeof(MemoryTools)
            .GetMethod(nameof(MemoryTools.Search), BindingFlags.Public | BindingFlags.Instance)
            .ShouldNotBeNull()
            .GetParameters()
            .SingleOrDefault(p => p.Name == "minRelativeScore");

        parameter.ShouldNotBeNull(
            "the MCP parameter must be named for what it filters — a fraction of the top hit, not an absolute score");
        parameter.HasDefaultValue.ShouldBeTrue();
        parameter.DefaultValue.ShouldBe(0.0);
    }
}
