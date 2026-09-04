using System.Text.Json;
using AiRaccoon.Core.Projects;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Projects;

/// <summary>ADR-0099: the public binary ships no machine-local ids — Default is empty by design. Machine ids live on only as explicit test-fixture data, never as production content.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectIdAliasMapTests
{
    // Explicit fixture: machine ids as TEST DATA (allowed). Production Default must never contain them (AC1).
    private static ProjectIdAliasMap FixtureMap() => new(
        [new ProjectIdAliasEntry("job-search-ai-assistant", "jsaa"), new ProjectIdAliasEntry("AI-RACCOON", "ai-raccoon")],
        ["jsaa", "ai-badger", "ai-raccoon", "hermes-default", "deepseek-harness", "arasz-home-page", "vue-kanban", "dotnet-ignore", "interview-tasks"],
        ["qa-noise-project", "manual-sweep"]);

    [Fact]
    public void Default_IsEmpty_ByDesign()
    {
        ProjectIdAliasMap.Default.IsEmpty.ShouldBeTrue();
        ProjectIdAliasMap.Default.Aliases.ShouldBeEmpty();
        ProjectIdAliasMap.Default.Canonicals.ShouldBeEmpty();
        ProjectIdAliasMap.Default.Dropped.ShouldBeEmpty();
        ProjectIdAliasMap.Empty.IsEmpty.ShouldBeTrue();
    }

    [Theory]
    [InlineData("job-search-ai-assistant", "jsaa")]
    [InlineData("AI-RACCOON", "ai-raccoon")]
    public void TryResolve_OfAKnownLoser_ReturnsTheCanonicalWinner(string alias, string canonical)
    {
        FixtureMap().TryResolve(alias, out var winner).ShouldBeTrue();

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
        FixtureMap().TryResolve(canonical, out var winner).ShouldBeTrue();

        winner.ShouldBe(canonical);
    }

    [Fact]
    public void TryResolve_OfATrueTypo_ReturnsFalse()
    {
        FixtureMap().TryResolve("jsaaa", out var winner).ShouldBeFalse();

        winner.ShouldBeNull();
    }

    [Theory]
    [InlineData("qa-noise-project")]
    [InlineData("manual-sweep")]
    public void TryResolve_OfADropCandidate_ReturnsFalse_ItIsDeletedNeverFolded(string dropped)
    {
        FixtureMap().TryResolve(dropped, out var winner).ShouldBeFalse();

        winner.ShouldBeNull();
        FixtureMap().IsDropped(dropped).ShouldBeTrue();
    }

    [Fact]
    public void Default_TryResolve_KnownLoserStrings_PassThrough()
    {
        // Steady-state pass-through pin (MUST-1): with Empty default, former loser strings are unknown.
        ProjectIdAliasMap.Default.TryResolve("job-search-ai-assistant", out var winner).ShouldBeFalse();
        winner.ShouldBeNull();
        ProjectIdAliasMap.Default.IsDropped("qa-noise-project").ShouldBeFalse();
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
        var json = FixtureMap().ToJson();

        var reloaded = ProjectIdAliasMap.FromJson(json);

        reloaded.TryResolve("job-search-ai-assistant", out var winner).ShouldBeTrue();
        winner.ShouldBe("jsaa");
        reloaded.IsDropped("manual-sweep").ShouldBeTrue();
        reloaded.TryResolve("jsaaa", out _).ShouldBeFalse();
    }

    [Fact]
    public void EmptyTemplateJson_ParsesToEmpty()
    {
        var template = ProjectIdAliasMap.Empty.ToJson(indented: true);

        var reloaded = ProjectIdAliasMap.FromJson(template);

        reloaded.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void LoadFromFile_RoundTrips_AFileWrittenByToJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alias-map-{Guid.CreateVersion7():N}.json");
        try
        {
            File.WriteAllText(path, FixtureMap().ToJson(indented: true));

            var reloaded = ProjectIdAliasMap.LoadFromFile(path);

            reloaded.TryResolve("job-search-ai-assistant", out var winner).ShouldBeTrue();
            winner.ShouldBe("jsaa");
            reloaded.IsDropped("manual-sweep").ShouldBeTrue();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void LoadFromFile_OfAMissingFile_ThrowsFileNotFound_NamingThePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alias-map-missing-{Guid.CreateVersion7():N}.json");

        var ex = Should.Throw<FileNotFoundException>(() => ProjectIdAliasMap.LoadFromFile(path));

        ex.Message.ShouldContain(path);
    }

    [Fact]
    public void LoadFromFile_OfBadJson_ThrowsJsonException_NamingThePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"alias-map-bad-{Guid.CreateVersion7():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");

            var ex = Should.Throw<JsonException>(() => ProjectIdAliasMap.LoadFromFile(path));

            ex.Message.ShouldContain(path);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
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
        FixtureMap().Fold(input).ShouldBe(expected);
    }

    [Theory]
    [InlineData("job-search-ai-assistant", "job-search-ai-assistant")]
    [InlineData("AI-RACCOON", "AI-RACCOON")]
    [InlineData("qa-noise-project", "qa-noise-project")]
    public void DefaultFold_PassesThrough_FormerLoserStrings(string input, string expected)
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
