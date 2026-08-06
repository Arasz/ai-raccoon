using AiRaccoon.Setup.Cli;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Cli;

/// <summary>
///     Shell-glob expansion diagnostic: an unquoted '*' target (e.g. `watch enable * true`)
///     is expanded by the shell into the entries of the current directory before the CLI sees
///     it, producing a wall of "Unrecognized command or argument" errors. The hint must fire
///     on that signature and teach the quoted form.
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
        var parsed = CliArgs.TryParse(["watch", "enable", "CLAUDE.md", "README.md", "docs", "true"]);

        var hint = CliRendering.GlobExpansionHint(parsed, CwdEntries);

        hint.ShouldNotBeNull();
        hint.ShouldContain("ai-raccoon watch enable '*' true");
    }

    [Fact]
    public void GlobExpansionHint_WatchScopeAddGlobbedArgs_UsesPathAsTrailingValue()
    {
        var parsed = CliArgs.TryParse(["watch", "scope", "add", "CLAUDE.md", "README.md", "docs", "/tmp/x"]);

        var hint = CliRendering.GlobExpansionHint(parsed, CwdEntries);

        hint.ShouldNotBeNull();
        hint.ShouldContain("ai-raccoon watch scope add '*' /tmp/x");
    }

    [Fact]
    public void GlobExpansionHint_TwoFilesAndValueToken_StillFiresViaTypedArgument()
    {
        var parsed = CliArgs.TryParse(["watch", "enable", "CLAUDE.md", "README.md", "true"]);

        var hint = CliRendering.GlobExpansionHint(parsed, CwdEntries);

        hint.ShouldNotBeNull();
        hint.ShouldContain("ai-raccoon watch enable '*' true");
    }

    [Fact]
    public void GlobExpansionHint_WatchConcurrencyInt32Value_StillFiresViaTypedArgument()
    {
        var parsed = CliArgs.TryParse(["watch", "concurrency", "CLAUDE.md", "README.md", "/tmp/x"]);

        var hint = CliRendering.GlobExpansionHint(parsed, CwdEntries);

        hint.ShouldNotBeNull();
        hint.ShouldContain("ai-raccoon watch concurrency '*' /tmp/x");
    }

    [Fact]
    public void GlobExpansionHint_TokensNotInCurrentDirectory_ReturnsNull()
    {
        var parsed = CliArgs.TryParse(["watch", "enable", "foo", "bar", "baz"]);

        CliRendering.GlobExpansionHint(parsed, CwdEntries).ShouldBeNull();
    }

    [Fact]
    public void GlobExpansionHint_UnknownCommand_ReturnsNull()
    {
        var parsed = CliArgs.TryParse(["watch", "bogus"]);

        CliRendering.GlobExpansionHint(parsed, CwdEntries).ShouldBeNull();
    }

    [Fact]
    public void GlobExpansionHint_CleanParse_ReturnsNull()
    {
        var parsed = CliArgs.TryParse(["watch", "enable", "myproject", "true"]);

        CliRendering.GlobExpansionHint(parsed, CwdEntries).ShouldBeNull();
    }

    [Fact]
    public void GlobExpansionHint_UnknownOption_ReturnsNull()
    {
        var parsed = CliArgs.TryParse(["--bogus"]);

        CliRendering.GlobExpansionHint(parsed, CwdEntries).ShouldBeNull();
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
        var parsed = CliArgs.TryParse(["watch", "enable", .. names, "true"]);

        var hint = CliRendering.GlobExpansionHint(parsed);

        hint.ShouldNotBeNull();
    }

    [Fact]
    public void Render_GlobExpansion_AppendsHintAfterParseErrors()
    {
        var parsed = CliArgs.TryParse(["watch", "enable", "CLAUDE.md", "README.md", "docs", "true"]);
        var stderr = new StringWriter();

        var exit = CliArgs.Render(parsed, stderr, CwdEntries);

        exit.ShouldBe(1);
        var output = stderr.ToString();
        output.ShouldContain("Unrecognized command or argument 'docs'.");
        output.ShouldContain("ai-raccoon watch enable '*' true");
    }
}
