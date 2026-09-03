using AiRaccoon.Core.Projects;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>Air-merge P1: the durable loser-to-winner map from the plan's canonical-wins table — jsaa and casing folds resolve, drop-candidates never fold, true typos stay unknown.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectIdAliasMapTests
{
    [Theory]
    [InlineData("job-search-ai-assistant", "jsaa")]
    [InlineData("AI-RACCOON", "ai-raccoon")]
    public void TryResolve_OfAKnownLoser_ReturnsTheCanonicalWinner(string alias, string canonical)
    {
        ProjectIdAliasMap.Default.TryResolve(alias, out var winner).ShouldBeTrue();

        winner.ShouldBe(canonical);
    }

    [Theory]
    [InlineData("jsaa")]
    [InlineData("ai-badger")]
    [InlineData("ai-raccoon")]
    [InlineData("hermes-default")]
    [InlineData("deepseek-harness")]
    [InlineData("arasz-home-page")]
    [InlineData("vue-kanban")]
    [InlineData("dotnet-ignore")]
    [InlineData("interview-tasks")]
    public void TryResolve_OfACanonicalWinner_ResolvesToItself(string canonical)
    {
        ProjectIdAliasMap.Default.TryResolve(canonical, out var winner).ShouldBeTrue();

        winner.ShouldBe(canonical);
    }

    [Fact]
    public void TryResolve_OfATrueTypo_ReturnsFalse()
    {
        ProjectIdAliasMap.Default.TryResolve("jsaaa", out var winner).ShouldBeFalse();

        winner.ShouldBeNull();
    }

    [Theory]
    [InlineData("qa-noise-project")]
    [InlineData("manual-sweep")]
    public void TryResolve_OfADropCandidate_ReturnsFalse_ItIsDeletedNeverFolded(string dropped)
    {
        ProjectIdAliasMap.Default.TryResolve(dropped, out var winner).ShouldBeFalse();

        winner.ShouldBeNull();
        ProjectIdAliasMap.Default.IsDropped(dropped).ShouldBeTrue();
    }

    [Fact]
    public void CustomMap_ResolvesAGuidLoser_ByExactEntry()
    {
        var guidLoser = Guid.CreateVersion7().ToString("D");
        var map = new ProjectIdAliasMap([new ProjectIdAliasEntry(guidLoser, "jsaa")], [], []);

        map.TryResolve(guidLoser, out var winner).ShouldBeTrue();

        winner.ShouldBe("jsaa");
    }

    [Fact]
    public void JsonRoundTrip_PreservesAliasesCanonicalsAndDrops()
    {
        var json = ProjectIdAliasMap.Default.ToJson();

        var reloaded = ProjectIdAliasMap.FromJson(json);

        reloaded.TryResolve("job-search-ai-assistant", out var winner).ShouldBeTrue();
        winner.ShouldBe("jsaa");
        reloaded.IsDropped("manual-sweep").ShouldBeTrue();
        reloaded.TryResolve("jsaaa", out _).ShouldBeFalse();
    }
}
