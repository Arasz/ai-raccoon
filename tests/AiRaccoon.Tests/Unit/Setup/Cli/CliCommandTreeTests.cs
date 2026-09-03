using System.CommandLine;
using AiRaccoon.Setup.Cli;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Cli;

/// <summary>
///     Pins the CLI surface contract of the extracted command-tree component: the full
///     root exposes exactly the verb families, the launch root exposes the launch options
///     and no verbs (the verb-less re-parse fallback in CliArgs.Parse depends on it).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class CliCommandTreeTests
{
    /// <summary>
    ///     AC1 — `ai-raccoon settings performance --help` lists the three set verbs (one per
    ///     settings-backed knob: buffer capacity, flush interval, retention) plus list. Fixes the defect
    ///     the 1.20.0 manual checklist found (#352): metrics was the only settings-backed subsystem with
    ///     no CLI family at all.
    /// </summary>
    [Fact]
    public void PerformanceCommand_ExposesSetVerbsAndList()
    {
        var performance = SettingsFamily("performance");

        performance.Children.OfType<Command>().Select(c => c.Name)
            .ShouldBe(["buffer-capacity", "flush-interval", "retention", "list"]);
        var list = performance.Children.OfType<Command>().Single(c => c.Name == "list");
        list.Aliases.ShouldContain("show");
    }

    private static Command SettingsFamily(string name) =>
        CliCommandTree.BuildFullRootCommand().Children.OfType<Command>().Single(c => c.Name == "settings")
            .Children.OfType<Command>().Single(c => c.Name == name);

    /// <summary>
    ///     `repair reingest --help` must describe requesting the server, not a manual fallback.
    ///     `--apply` only ever requests the server apply it; the help text must say that, and
    ///     must not advertise a CLI-only manual fallback as the normal case.
    /// </summary>
    [Fact]
    public void RepairReingestCommand_DescribesRequestingTheServer_NotAManualFallback()
    {
        var reingest = CommandAt("repair").Children.OfType<Command>().Single(c => c.Name == "reingest");
        var description = reingest.Description.ShouldNotBeNull();

        description.ShouldContain("server");
        description.ShouldContain("--apply");
        description.ShouldNotContain("memory_embed_pending");
        description.ShouldNotContain("no server running");
    }

    [Fact]
    public void RepairApplyOptions_DescribeRequestingTheServer_NotWritingLocally()
    {
        var repair = CommandAt("repair");
        var reingestApply = repair.Children.OfType<Command>().Single(c => c.Name == "reingest")
            .Children.OfType<Option>().Single(o => o.Name == "--apply");
        var chunkIndexApply = repair.Children.OfType<Command>().Single(c => c.Name == "chunk-index")
            .Children.OfType<Option>().Single(o => o.Name == "--apply");
        var projectIdsApply = repair.Children.OfType<Command>().Single(c => c.Name == "project-ids")
            .Children.OfType<Option>().Single(o => o.Name == "--apply");

        reingestApply.Description.ShouldNotBeNull().ShouldContain("server");
        chunkIndexApply.Description.ShouldNotBeNull().ShouldContain("server");
        projectIdsApply.Description.ShouldNotBeNull().ShouldContain("server");
    }

    [Fact]
    public void RepairAndDoctorHelpText_IsSelfContainedAndShort()
    {
        var repair = CommandAt("repair");
        var chunkIndex = repair.Children.OfType<Command>().Single(c => c.Name == "chunk-index");
        var reingest = repair.Children.OfType<Command>().Single(c => c.Name == "reingest");
        var projectIds = repair.Children.OfType<Command>().Single(c => c.Name == "project-ids");
        var doctor = CommandAt("doctor");

        foreach (var description in new[] { repair.Description, chunkIndex.Description, reingest.Description, projectIds.Description, doctor.Description })
        {
            var value = description!;
            value.ShouldNotContain("ADR-");
            value.ShouldNotContain("GH #");
            value.ShouldNotContain("memory_embed_pending");
            value.Length.ShouldBeLessThan(220);
        }

        chunkIndex.Description!.ShouldContain("Use this when");
        reingest.Description!.ShouldContain("Use this when");
        projectIds.Description!.ShouldContain("Use this when");
        doctor.Description!.ShouldContain("without changing the bank");
    }

    [Fact]
    public void ServeCommand_ExposesServeOptions()
    {
        var root = CliCommandTree.BuildFullRootCommand();

        var serve = root.Children.OfType<Command>().Single(c => c.Name == "serve");
        serve.Children.OfType<Option>().ShouldContain(CliCommandTree.ServePortOption);
        serve.Children.OfType<Option>().ShouldContain(CliCommandTree.ServeIdleTimeoutOption);
        serve.Children.OfType<Option>().ShouldContain(CliCommandTree.ServeMcpEntryOption);
        serve.Children.OfType<Option>().ShouldContain(CliCommandTree.ServeFormatOption);
    }

    [Fact]
    public void ServeCommand_ExposesObservabilitySubcommand()
    {
        var root = CliCommandTree.BuildFullRootCommand();

        var serve = root.Children.OfType<Command>().Single(c => c.Name == "serve");
        var observability = serve.Children.OfType<Command>().SingleOrDefault(c => c.Name == "observability");
        observability.ShouldNotBeNull();
        observability.Children.OfType<Option>().ShouldContain(CliCommandTree.ObservabilityPortOption);
    }

    [Fact]
    public void ServeCommand_DeclaresAnAction_SoBareServeParses()
    {
        var root = CliCommandTree.BuildFullRootCommand();

        var serve = root.Children.OfType<Command>().Single(c => c.Name == "serve");
        serve.Action.ShouldNotBeNull();
    }

    [Fact]
    public void LaunchRoot_ExposesLaunchOptionsAndNoVerbs()
    {
        var root = CliCommandTree.BuildLaunchRootCommand();

        var options = root.Children.OfType<Option>().Select(o => o.Name).ToArray();
        options.ShouldContain("--transport");
        options.ShouldContain("--data-root");
        options.ShouldContain("--install-scope");
        root.Children.OfType<Command>().ShouldBeEmpty();
    }

    [Fact]
    public void LaunchRoot_ExposesTheSharedLaunchPortOption()
    {
        var root = CliCommandTree.BuildLaunchRootCommand();

        root.Children.OfType<Option>().ShouldContain(CliCommandTree.LaunchPortOption);
    }

    /// <summary>
    ///     "revert to default" is spelled reset/unset/remove across model/encryption/sync; every
    ///     spelling must work under every one of the three verbs without changing the canonical name.
    /// </summary>
    [Theory]
    [InlineData("settings model", "unset", "reset")]
    [InlineData("settings model", "remove", "reset")]
    [InlineData("encryption", "reset", "unset")]
    [InlineData("encryption", "remove", "unset")]
    [InlineData("settings sync", "reset", "remove")]
    [InlineData("settings sync", "unset", "remove")]
    public void RevertToDefaultAlias_ResolvesToTheCanonicalCommand(string verbPath, string alias, string canonicalName)
    {
        var root = CliCommandTree.BuildFullRootCommand();

        var parseResult = root.Parse([.. verbPath.Split(' '), alias]);

        parseResult.CommandResult.Command.Name.ShouldBe(canonicalName);
    }

    [Theory]
    [InlineData("settings model", "reset", new[] { "unset", "remove" })]
    [InlineData("encryption", "unset", new[] { "reset", "remove" })]
    [InlineData("settings sync", "remove", new[] { "reset", "unset" })]
    public void RevertToDefaultCommand_KeepsItsNameAndGainsTheOtherSpellingsAsAliases(string verbPath, string canonicalName, string[] expectedAliases)
    {
        var canonical = CommandAt(verbPath).Children.OfType<Command>().Single(c => c.Name == canonicalName);

        canonical.Name.ShouldBe(canonicalName);
        foreach (var alias in expectedAliases)
        {
            canonical.Aliases.ShouldContain(alias);
        }
    }

    /// <summary>Walks a space-separated command path from the root.</summary>
    private static Command CommandAt(string path)
    {
        var current = (Command)CliCommandTree.BuildFullRootCommand();
        foreach (var segment in path.Split(' '))
        {
            current = current.Children.OfType<Command>().Single(c => c.Name == segment);
        }

        return current;
    }

    /// <summary>
    ///     "show current config" is spelled list in some verb groups and show in others; each
    ///     group's canonical verb gains the other spelling as an alias.
    /// </summary>
    [Theory]
    [InlineData("settings access", "list", "show")]
    [InlineData("settings watch", "list", "show")]
    [InlineData("settings maintenance", "list", "show")]
    [InlineData("settings performance", "list", "show")]
    [InlineData("settings extract", "list", "show")]
    [InlineData("settings model", "show", "list")]
    [InlineData("settings sweep", "show", "list")]
    [InlineData("settings sync", "show", "list")]
    [InlineData("encryption", "show", "list")]
    [InlineData("settings noise", "show", "list")]
    public void ShowConfigAlias_ResolvesToTheCanonicalCommand(string verbPath, string canonicalName, string alias)
    {
        var root = CliCommandTree.BuildFullRootCommand();

        var parseResult = root.Parse([.. verbPath.Split(' '), alias]);

        parseResult.CommandResult.Command.Name.ShouldBe(canonicalName);
    }

    [Theory]
    [InlineData("settings access", "list", "show")]
    [InlineData("settings watch", "list", "show")]
    [InlineData("settings maintenance", "list", "show")]
    [InlineData("settings performance", "list", "show")]
    [InlineData("settings extract", "list", "show")]
    [InlineData("settings model", "show", "list")]
    [InlineData("settings sweep", "show", "list")]
    [InlineData("settings sync", "show", "list")]
    [InlineData("encryption", "show", "list")]
    [InlineData("settings noise", "show", "list")]
    public void ShowConfigCommand_KeepsItsNameAndGainsTheOtherSpellingAsAlias(string verbPath, string canonicalName, string expectedAlias)
    {
        var canonical = CommandAt(verbPath).Children.OfType<Command>().Single(c => c.Name == canonicalName);

        canonical.Name.ShouldBe(canonicalName);
        canonical.Aliases.ShouldContain(expectedAlias);
    }
}
