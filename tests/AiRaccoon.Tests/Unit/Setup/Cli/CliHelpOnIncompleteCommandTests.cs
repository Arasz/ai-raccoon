using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Render;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Cli;

/// <summary>
///     A recognised-but-incomplete command (missing subcommand or missing argument) shows help for
///     the command it got as far as, instead of the bare parse-error line alone. docs/adr/0060 keeps
///     an unrecognised verb out of this: that case never resolves a CommandPath (IsCommandInput stays
///     false), so it must still print only its error, with no help and no launch.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CliHelpOnIncompleteCommandTests
{
    [Fact]
    public void MissingSubcommand_ShowsHelpForTheResolvedCommand()
    {
        CliArgs.TryParse(["model"], out var parsed);
        var stderr = new StringWriter();

        CliRendering.Render(parsed!, new StandardStreams(TextReader.Null, TextWriter.Null, stderr));

        var output = stderr.ToString();
        output.ShouldContain("Required command was not provided.");
        output.ShouldContain("Usage:");
        output.ShouldContain("set");
        CountOccurrences(output, "Required command was not provided.").ShouldBe(1);
        CountOccurrences(output, "Usage:").ShouldBe(1);
    }

    [Fact]
    public void MissingArgument_ShowsHelpForTheResolvedCommand()
    {
        CliArgs.TryParse(["model", "set", "openai"], out var parsed);
        var stderr = new StringWriter();

        CliRendering.Render(parsed!, new StandardStreams(TextReader.Null, TextWriter.Null, stderr));

        var output = stderr.ToString();
        output.ShouldContain("Required argument missing for command: 'openai'.");
        output.ShouldContain("Usage:");
        output.ShouldContain("model-id");
        CountOccurrences(output, "Required argument missing for command: 'openai'.").ShouldBe(1);
        CountOccurrences(output, "Usage:").ShouldBe(1);
    }

    [Fact]
    public void ExplicitHelp_StillRendersOnce_WithNoParseErrorText()
    {
        CliArgs.TryParse(["model", "--help"], out var parsed);
        var stderr = new StringWriter();

        CliRendering.Render(parsed!, new StandardStreams(TextReader.Null, TextWriter.Null, stderr));

        var output = stderr.ToString();
        output.ShouldNotContain("Required command was not provided.");
        CountOccurrences(output, "Usage:").ShouldBe(1);
    }

    /// <summary>ADR-0060: an unrecognised verb must not receive help — only its error.</summary>
    [Fact]
    public void UnrecognisedVerb_ShowsOnlyTheError_NoHelp()
    {
        CliArgs.TryParse(["frobnicate"], out var parsed);
        var stderr = new StringWriter();

        CliRendering.Render(parsed!, new StandardStreams(TextReader.Null, TextWriter.Null, stderr));

        var output = stderr.ToString();
        output.ShouldContain("Unrecognized command or argument 'frobnicate'.");
        output.ShouldNotContain("Usage:");
        parsed!.IsCommandInput.ShouldBeFalse("an unrecognised verb must never resolve a command path");
    }

    /// <summary>The guard on the guard: a bare invocation carries no parse error, so nothing renders.</summary>
    [Fact]
    public void BareInvocation_PrintsNothing()
    {
        CliArgs.TryParse([], out var parsed);
        var stderr = new StringWriter();

        CliRendering.Render(parsed!, new StandardStreams(TextReader.Null, TextWriter.Null, stderr));

        stderr.ToString().ShouldBeEmpty();
    }

    [Fact]
    public void MissingSubcommand_WritesNothingToStdout()
    {
        CliArgs.TryParse(["model"], out var parsed);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        CliRendering.Render(parsed!, new StandardStreams(TextReader.Null, stdout, stderr));

        stdout.ToString().ShouldBeEmpty();
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var index = haystack.IndexOf(needle, StringComparison.Ordinal); index >= 0;
             index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
