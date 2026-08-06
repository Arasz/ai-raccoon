using System.Globalization;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Commands;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Direct tests of the extracted SettingsCommands component (access/model/retrieval/sweep
///     verb families). The full behavior contract stays in the ConfigCommands* test files
///     through the dispatcher; these pin the component seam itself.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class SettingsCommandsTests
{
    private static async Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store)
    {
        CliArgs.TryParse(args, out var parsed);
        parsed.Errors.ShouldBeEmpty();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var commands = new SettingsCommands();
        var exit = parsed.CommandPath switch
        {
            ["access", "default", "show"] => await commands.AccessDefaultShowAsync(store, stdout, TestContext.Current.CancellationToken),
            ["access", "list"] => await commands.AccessListAsync(store, stdout, TestContext.Current.CancellationToken),
            ["model", "set", "local"] => await commands.ModelSetLocalAsync(parsed.ParseResult, store, stdout, TestContext.Current.CancellationToken),
            ["model", "show"] => await commands.ModelShowAsync(store, stdout, TestContext.Current.CancellationToken),
            ["retrieval", "alpha", "set"] => await commands.RetrievalAlphaSetAsync(parsed.ParseResult, store, stdout, stderr, TestContext.Current.CancellationToken),
            ["retrieval", "alpha", "show"] => await commands.RetrievalAlphaShowAsync(store, stdout, TestContext.Current.CancellationToken),
            ["sweep", "show"] => await commands.SweepShowAsync(store, stdout, TestContext.Current.CancellationToken),
            _ => throw new InvalidOperationException($"unhandled: {string.Join(' ', parsed.CommandPath)}")
        };
        return (exit, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public async Task AccessDefaultShow_NoRow_PrintsRw()
    {
        var (exit, stdout, _) = await Run(["access", "default", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("rw");
    }

    [Fact]
    public async Task AccessList_GlobalAndProjectRows_Sorted()
    {
        var store = new FakeConfigStore
        {
            Settings =
            {
                ["access.mode.global"] = "rw",
                ["access.mode.project:p2"] = "ro",
                ["access.mode.project:p1"] = "full"
            }
        };

        var (exit, stdout, _) = await Run(["access", "list"], store);

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe(
            "default: rw\n" +
            "p1: full\n" +
            "p2: ro");
    }

    [Fact]
    public async Task RetrievalAlphaSet_NonNumeric_ReturnsError()
    {
        var (exit, _, err) = await Run(["retrieval", "alpha", "set", "bogus"], new FakeConfigStore());

        exit.ShouldBe(1);
        err.ShouldContain("invalid alpha");
    }

    [Fact]
    public async Task RetrievalAlphaShow_NoRow_PrintsDefaultInvariantCulture()
    {
        var (exit, stdout, _) = await Run(["retrieval", "alpha", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe(0.5.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ModelShow_NoProvider_PrintsFts5Only()
    {
        var (exit, stdout, _) = await Run(["model", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.ShouldContain("provider: (none");
    }

    [Fact]
    public async Task SweepShow_NoRow_PrintsDefault()
    {
        var (exit, stdout, _) = await Run(["sweep", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("0.3");
    }
}
