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
    [Fact]
    public void Root_ExposesAllVerbFamilies()
    {
        var root = CliCommandTree.BuildFullRootCommand();

        root.Children.OfType<Command>().Select(c => c.Name)
            .ShouldBe(["access", "model", "retrieval", "sweep", "noise", "queryguard", "sync", "ingest", "watch", "encryption", "extract", "maintenance", "performance", "serve"]);
        CliCommandTree.Verbs.ShouldBe(["access", "model", "retrieval", "sweep", "noise", "queryguard", "sync", "ingest", "watch", "encryption", "extract", "maintenance", "performance", "serve"]);
    }

    /// <summary>
    ///     AC1 — `ai-raccoon performance --help` lists the three set verbs (one per settings-backed
    ///     knob: buffer capacity, flush interval, retention) plus list. Fixes the defect the 1.20.0
    ///     manual checklist found (#352): metrics was the only settings-backed subsystem with no CLI
    ///     family at all.
    /// </summary>
    [Fact]
    public void PerformanceCommand_ExposesSetVerbsAndList()
    {
        var root = CliCommandTree.BuildFullRootCommand();

        var performance = root.Children.OfType<Command>().Single(c => c.Name == "performance");
        performance.Children.OfType<Command>().Select(c => c.Name)
            .ShouldBe(["buffer-capacity", "flush-interval", "retention", "list"]);
        var list = performance.Children.OfType<Command>().Single(c => c.Name == "list");
        list.Aliases.ShouldContain("show");
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
    [InlineData("model", "unset", "reset")]
    [InlineData("model", "remove", "reset")]
    [InlineData("encryption", "reset", "unset")]
    [InlineData("encryption", "remove", "unset")]
    [InlineData("sync", "reset", "remove")]
    [InlineData("sync", "unset", "remove")]
    public void RevertToDefaultAlias_ResolvesToTheCanonicalCommand(string verb, string alias, string canonicalName)
    {
        var root = CliCommandTree.BuildFullRootCommand();

        var parseResult = root.Parse([verb, alias]);

        parseResult.CommandResult.Command.Name.ShouldBe(canonicalName);
    }

    [Theory]
    [InlineData("model", "reset", new[] { "unset", "remove" })]
    [InlineData("encryption", "unset", new[] { "reset", "remove" })]
    [InlineData("sync", "remove", new[] { "reset", "unset" })]
    public void RevertToDefaultCommand_KeepsItsNameAndGainsTheOtherSpellingsAsAliases(string verb, string canonicalName, string[] expectedAliases)
    {
        var root = CliCommandTree.BuildFullRootCommand();
        var group = root.Children.OfType<Command>().Single(c => c.Name == verb);
        var canonical = group.Children.OfType<Command>().Single(c => c.Name == canonicalName);

        canonical.Name.ShouldBe(canonicalName);
        foreach (var alias in expectedAliases)
        {
            canonical.Aliases.ShouldContain(alias);
        }
    }

    /// <summary>
    ///     "show current config" is spelled list in some verb groups and show in others; each
    ///     group's canonical verb gains the other spelling as an alias.
    /// </summary>
    [Theory]
    [InlineData("access", "list", "show")]
    [InlineData("watch", "list", "show")]
    [InlineData("maintenance", "list", "show")]
    [InlineData("performance", "list", "show")]
    [InlineData("extract", "list", "show")]
    [InlineData("model", "show", "list")]
    [InlineData("sweep", "show", "list")]
    [InlineData("sync", "show", "list")]
    [InlineData("encryption", "show", "list")]
    [InlineData("noise", "show", "list")]
    public void ShowConfigAlias_ResolvesToTheCanonicalCommand(string verb, string canonicalName, string alias)
    {
        var root = CliCommandTree.BuildFullRootCommand();

        var parseResult = root.Parse([verb, alias]);

        parseResult.CommandResult.Command.Name.ShouldBe(canonicalName);
    }

    [Theory]
    [InlineData("access", "list", "show")]
    [InlineData("watch", "list", "show")]
    [InlineData("maintenance", "list", "show")]
    [InlineData("performance", "list", "show")]
    [InlineData("extract", "list", "show")]
    [InlineData("model", "show", "list")]
    [InlineData("sweep", "show", "list")]
    [InlineData("sync", "show", "list")]
    [InlineData("encryption", "show", "list")]
    [InlineData("noise", "show", "list")]
    public void ShowConfigCommand_KeepsItsNameAndGainsTheOtherSpellingAsAlias(string verb, string canonicalName, string expectedAlias)
    {
        var root = CliCommandTree.BuildFullRootCommand();
        var group = root.Children.OfType<Command>().Single(c => c.Name == verb);
        var canonical = group.Children.OfType<Command>().Single(c => c.Name == canonicalName);

        canonical.Name.ShouldBe(canonicalName);
        canonical.Aliases.ShouldContain(expectedAlias);
    }
}
