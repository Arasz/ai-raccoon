using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Render;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Cli;

/// <summary>
///     Shell-glob expansion diagnostic: an unquoted '*' target (e.g. `watch enable * true`)
///     is expanded by the shell into cwd entries before the CLI sees it, producing a wall of
///     parse errors. The hint must fire on that signature and teach the quoted form.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class CliGlobExpansionHintTests
{
    private static readonly IReadOnlySet<string> CwdEntries = new HashSet<string>(StringComparer.Ordinal)
    {
        "CLAUDE.md", "README.md", "docs", "tests"
    };

    [Fact]
    public void GlobExpansionHint_WatchEnableGlobbedArgs_ReconstructsQuotedCommand()
    {
        CliArgs.TryParse(["settings", "watch", "enable", "CLAUDE.md", "README.md", "docs", "true"], out var parsed);

        var hint = CliRendering.GlobExpansionHint(parsed!, CwdEntries);

        hint.ShouldNotBeNull();
        hint.ShouldContain("ai-raccoon settings watch enable '*' true");
    }

    [Fact]
    public void GlobExpansionHint_WatchScopeAddGlobbedArgs_UsesPathAsTrailingValue()
    {
        CliArgs.TryParse(["settings", "ingest", "scope", "add", "CLAUDE.md", "README.md", "docs", "/tmp/x"], out var parsed);

        var hint = CliRendering.GlobExpansionHint(parsed!, CwdEntries);

        hint.ShouldNotBeNull();
        hint.ShouldContain("ai-raccoon settings ingest scope add '*' /tmp/x");
    }

    [Fact]
    public void GlobExpansionHint_TwoFilesAndValueToken_StillFiresViaTypedArgument()
    {
        CliArgs.TryParse(["settings", "watch", "enable", "CLAUDE.md", "README.md", "true"], out var parsed);

        var hint = CliRendering.GlobExpansionHint(parsed!, CwdEntries);

        hint.ShouldNotBeNull();
        hint.ShouldContain("ai-raccoon settings watch enable '*' true");
    }

    [Fact]
    public void GlobExpansionHint_WatchConcurrencyInt32Value_StillFiresViaTypedArgument()
    {
        CliArgs.TryParse(["settings", "watch", "concurrency", "CLAUDE.md", "README.md", "/tmp/x"], out var parsed);

        var hint = CliRendering.GlobExpansionHint(parsed!, CwdEntries);

        hint.ShouldNotBeNull();
        hint.ShouldContain("ai-raccoon settings watch concurrency '*' /tmp/x");
    }

    [Fact]
    public void GlobExpansionHint_TokensNotInCurrentDirectory_ReturnsNull()
    {
        CliArgs.TryParse(["settings", "watch", "enable", "foo", "bar", "baz"], out var parsed);

        CliRendering.GlobExpansionHint(parsed!, CwdEntries).ShouldBeNull();
    }

    [Fact]
    public void GlobExpansionHint_UnknownCommand_ReturnsNull()
    {
        CliArgs.TryParse(["settings", "watch", "bogus"], out var parsed);

        CliRendering.GlobExpansionHint(parsed!, CwdEntries).ShouldBeNull();
    }

    [Fact]
    public void GlobExpansionHint_CleanParse_ReturnsNull()
    {
        CliArgs.TryParse(["settings", "watch", "enable", "myproject", "true"], out var parsed);

        CliRendering.GlobExpansionHint(parsed!, CwdEntries).ShouldBeNull();
    }

    [Fact]
    public void GlobExpansionHint_UnknownOption_ReturnsNull()
    {
        CliArgs.TryParse(["--bogus"], out var parsed);

        CliRendering.GlobExpansionHint(parsed!, CwdEntries).ShouldBeNull();
    }

    [Fact]
    public void GlobExpansionHint_RealCurrentDirectory_DetectsExpansion()
    {
        var names = Directory.GetFileSystemEntries(".")
            .Select(Path.GetFileName)
            .Where(n => n is not null && !n.Contains('\''))
            .Select(n => n!)
            .Take(3)
            .ToArray();
        names.Length.ShouldBeGreaterThanOrEqualTo(3);
        CliArgs.TryParse(["settings", "watch", "enable", .. names, "true"], out var parsed);

        var hint = CliRendering.GlobExpansionHint(parsed!);

        hint.ShouldNotBeNull();
    }

    [Fact]
    public void Render_GlobExpansion_AppendsHintAfterParseErrors()
    {
        CliArgs.TryParse(["settings", "watch", "enable", "CLAUDE.md", "README.md", "docs", "true"], out var parsed);
        var stderr = new StringWriter();

        CliRendering.Render(parsed!, new StandardStreams(TextReader.Null, TextWriter.Null, stderr), CwdEntries);

        var output = stderr.ToString();
        output.ShouldContain("Unrecognized command or argument 'docs'.");
        output.ShouldContain("ai-raccoon settings watch enable '*' true");
    }
}
