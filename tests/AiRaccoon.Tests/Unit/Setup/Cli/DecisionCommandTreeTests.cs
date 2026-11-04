using AiRaccoon.Core.Decisions;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Cli;

/// <summary>`decisions export|preview` selectors: one of --tasks, --all or --task-id, refused at parse time when mixed.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class DecisionCommandTreeTests
{
    private const string Project = "0b7c2b0e-6a8e-4f7e-9d1a-2f3c4d5e6f70";

    public static TheoryData<string[]> MixedSelectors() => new()
    {
        new[] { "--all", "--tasks", "2" },
        new[] { "--tasks", "2", "--task-id", "a" },
        new[] { "--all", "--task-id", "a" },
        new[] { "--tasks", "0" },
        new[] { "--tasks", "1001" }
    };

    [Theory]
    [MemberData(nameof(MixedSelectors))]
    public void MixedOrOutOfRangeSelectors_AreParseErrors(string[] selection)
    {
        foreach (var verb in new[] { "export", "preview" })
        {
            CliArgs.TryParse(["decisions", verb, "--project-id", Project, .. selection], out var parsed);

            parsed!.Errors.ShouldNotBeEmpty(string.Join(' ', selection));
            parsed.FailureCode.ShouldBe(ErrorCode.Usage.InvalidValue);
        }
    }

    [Fact]
    public void Selector_DefaultsToTheBackendDefault_AndMapsEachFlag()
    {
        Selector([]).ShouldBeNull();
        Selector(["--tasks", "3"]).ShouldBe(new DecisionTaskSelector(last: 3));
        Selector(["--all"]).ShouldBe(new DecisionTaskSelector(all: true));
        var named = Selector(["--task-id", "a", "--task-id", "b"]).ShouldNotBeNull();
        named.TaskIds.ShouldBe(["a", "b"]);
        named.Last.ShouldBeNull();
    }

    [Fact]
    public void ProjectId_IsRequired()
    {
        CliArgs.TryParse(["decisions", "export"], out var parsed);

        parsed!.Errors.ShouldNotBeEmpty();
    }

    [Fact]
    public void ExportHelp_SaysTheOutputIsPlaintext()
    {
        var export = CliCommandTree.BuildFullRootCommand().Subcommands.Single(c => c.Name == "decisions")
            .Subcommands.Single(c => c.Name == "export");

        export.Description.ShouldNotBeNull().ShouldContain("plaintext");
    }

    private static DecisionTaskSelector? Selector(string[] selection)
    {
        CliArgs.TryParse(["decisions", "export", "--project-id", Project, .. selection], out var parsed);
        parsed!.Errors.ShouldBeEmpty();
        return DecisionCommands.Selector(parsed.ParsedCliArgs);
    }
}
