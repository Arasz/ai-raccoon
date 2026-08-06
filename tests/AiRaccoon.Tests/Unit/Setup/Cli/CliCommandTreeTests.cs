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
            .ShouldBe(["access", "model", "retrieval", "sweep", "sync", "watch", "encryption", "extract", "serve"]);
        CliCommandTree.Verbs.ShouldBe(["access", "model", "retrieval", "sweep", "sync", "watch", "encryption", "extract", "serve"]);
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
    public void LaunchRoot_ExposesLaunchOptionsAndNoVerbs()
    {
        var root = CliCommandTree.BuildLaunchRootCommand();

        var options = root.Children.OfType<Option>().Select(o => o.Name).ToArray();
        options.ShouldContain("--transport");
        options.ShouldContain("--data-root");
        options.ShouldContain("--install-scope");
        root.Children.OfType<Command>().ShouldBeEmpty();
    }
}
