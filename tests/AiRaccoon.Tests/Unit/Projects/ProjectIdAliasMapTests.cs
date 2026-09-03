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

    /// <summary>
    ///     P3/P4 choke helper: guid D-form first, then the alias winner — everything else
    ///     (canonicals, true typos, drop-candidates) comes back untouched. The guard refuses typos
    ///     and Fold never invents a mapping, so a typo must survive Fold verbatim. Mixed-case legs
    ///     pin the Ordinal non-goal (d-425 SHOULD-5): JSAA is NOT jsaa — only an explicit entry folds.
    ///     Ledger — skip-alias-leg : --filter Fold_MapsKnownLosers_AndLeavesEverythingElseAlone :
    ///     jsaa/AI-RACCOON/typo/drop/mixed-case InlineData legs.
    /// </summary>
    [Theory]
    [InlineData("job-search-ai-assistant", "jsaa")]
    [InlineData("AI-RACCOON", "ai-raccoon")]
    [InlineData("jsaa", "jsaa")]
    [InlineData("jsaaa", "jsaaa")]
    [InlineData("qa-noise-project", "qa-noise-project")]
    [InlineData("JSAA", "JSAA")]
    [InlineData("Job-Search-Ai-Assistant", "Job-Search-Ai-Assistant")]
    public void Fold_MapsKnownLosers_AndLeavesEverythingElseAlone(string input, string expected)
    {
        ProjectIdAliasMap.Default.Fold(input).ShouldBe(expected);
    }

    /// <summary>
    ///     A guid spelling folds to the D-form even when the map knows nothing about it — same
    ///     single spelling the gate has always canonicalized to (ADR-0089 decision 2).
    ///     Ledger — skip-guid-leg : --filter Fold_OfAGuidSpelling_ReturnsTheDForm : braced-upper guid.
    /// </summary>
    [Fact]
    public void Fold_OfAGuidSpelling_ReturnsTheDForm()
    {
        var canonical = Guid.CreateVersion7().ToString("D");

        ProjectIdAliasMap.Default.Fold($"{{{canonical.ToUpperInvariant()}}}").ShouldBe(canonical);
    }

    /// <summary>
    ///     The key factories derive prefixes from <c>ScopeProject(string.Empty)</c> — Fold must pass
    ///     the empty derivation input through instead of throwing.
    ///     Ledger — throw-on-blank : --filter Fold_OfABlankInput_ReturnsItUnchanged : empty string.
    /// </summary>
    [Fact]
    public void Fold_OfABlankInput_ReturnsItUnchanged()
    {
        ProjectIdAliasMap.Default.Fold(string.Empty).ShouldBe(string.Empty);
    }
}
