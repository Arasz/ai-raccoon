using System.Reflection;
using AiRaccoon.Core.Memory;
using AiRaccoon.Tools;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Mcp;

/// <summary>
///     ADR-0096 (amending ADR-0047): the fused ranking is max-normalized, so the floor is a
///     fraction of the top hit — and it now ships ON at 0.6 with limit 8, because the default
///     path's measured shape was 20 unfiltered hits whose tail was noise. Callers that need
///     full recall opt out explicitly per call; the parameter name still carries what it filters.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SearchScoreFloorContractTests
{
    [Fact]
    public void SearchQuery_DefaultsTheRelativeFloor_ToNoiseCut()
    {
        new SearchQuery("acme", "search").MinRelativeScore.ShouldBe(0.6);
    }

    [Fact]
    public void SearchQuery_DefaultsTheLimit_ToEight()
    {
        new SearchQuery("acme", "search").Limit.ShouldBe(8);
    }

    [Fact]
    public void MemorySearchTool_DefaultsTheRelativeFloor_ToNoiseCut()
    {
        var parameter = typeof(MemoryTools)
            .GetMethod(nameof(MemoryTools.Search), BindingFlags.Public | BindingFlags.Instance)
            .ShouldNotBeNull()
            .GetParameters()
            .SingleOrDefault(p => p.Name == "minRelativeScore");

        parameter.ShouldNotBeNull(
            "the MCP parameter must be named for what it filters — a fraction of the top hit, not an absolute score");
        parameter.HasDefaultValue.ShouldBeTrue();
        parameter.DefaultValue.ShouldBe(0.6);
    }
}
