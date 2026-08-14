using System.Globalization;
using AiRaccoon.Access;
using AiRaccoon.Core.Degradation;
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
        parsed!.Errors.ShouldBeEmpty();

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var streams = new StandardStreams(TextReader.Null, stdout, stderr);
        var commands = new SettingsCommands();
        var exit = parsed.CommandPath switch
        {
            ["access", "default", "show"] => await commands.AccessDefaultShowAsync(store, streams, TestContext.Current.CancellationToken),
            ["access", "list"] => await commands.AccessListAsync(store, streams, TestContext.Current.CancellationToken),
            ["model", "set", "local"] => await commands.ModelSetLocalAsync(parsed.ParsedCliArgs, store, streams, TestContext.Current.CancellationToken),
            ["model", "show"] => await commands.ModelShowAsync(store, streams, TestContext.Current.CancellationToken),
            ["retrieval", "alpha", "set"] => await commands.RetrievalAlphaSetAsync(parsed.ParsedCliArgs, store, streams, TestContext.Current.CancellationToken),
            ["retrieval", "alpha", "show"] => await commands.RetrievalAlphaShowAsync(store, streams, TestContext.Current.CancellationToken),
            ["sweep", "enable"] => await commands.SweepEnabledSetAsync(true, store, streams, TestContext.Current.CancellationToken),
            ["sweep", "disable"] => await commands.SweepEnabledSetAsync(false, store, streams, TestContext.Current.CancellationToken),
            ["sweep", "interval-hours"] => await commands.SweepIntervalHoursSetAsync(parsed.ParsedCliArgs, store, streams, TestContext.Current.CancellationToken),
            ["sweep", "threshold", "set"] => await commands.SweepThresholdSetAsync(parsed.ParsedCliArgs, store, streams, TestContext.Current.CancellationToken),
            ["sweep", "show"] => await commands.SweepShowAsync(store, streams, TestContext.Current.CancellationToken),
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
    public async Task SweepShow_NoRow_PrintsTheDefaultPolicy()
    {
        var (exit, stdout, _) = await Run(["sweep", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("enabled: True  interval: 24 h  threshold: 0.3");
    }

    [Fact]
    public async Task SweepThresholdSet_RoundTripsThroughCliShowAndForgettingPolicyService()
    {
        var store = new FakeConfigStore();

        var (setExit, _, _) = await Run(["sweep", "threshold", "set", "0.55"], store);
        var (showExit, showOut, _) = await Run(["sweep", "show"], store);

        setExit.ShouldBe(0);
        showExit.ShouldBe(0);
        showOut.Trim().ShouldBe("enabled: True  interval: 24 h  threshold: 0.55");

        var policy = new ForgettingPolicyService(store, new MemoryAccessGuard(store));
        var threshold = await policy.GetSweepThresholdAsync(TestContext.Current.CancellationToken);
        threshold.ShouldBe(0.55);
    }

    /// <summary>The kill switch round-trips through the same parse the reaper reads it with.</summary>
    [Fact]
    public async Task SweepDisableThenEnable_RoundTripsThroughCliShowAndSweepConfigKeys()
    {
        var store = new FakeConfigStore();

        var (disableExit, _, _) = await Run(["sweep", "disable"], store);
        var (_, disabledOut, _) = await Run(["sweep", "show"], store);

        disableExit.ShouldBe(0);
        disabledOut.ShouldContain("enabled: False");
        SweepConfigKeys.ParseEnabled(store.Settings[SweepConfigKeys.EnabledGlobal]).ShouldBeFalse();

        var (enableExit, _, _) = await Run(["sweep", "enable"], store);
        var (_, enabledOut, _) = await Run(["sweep", "show"], store);

        enableExit.ShouldBe(0);
        enabledOut.ShouldContain("enabled: True");
        SweepConfigKeys.ParseEnabled(store.Settings[SweepConfigKeys.EnabledGlobal]).ShouldBeTrue();
    }

    [Fact]
    public async Task SweepIntervalHours_RoundTripsThroughCliShowAndSweepConfigKeys()
    {
        var store = new FakeConfigStore();

        var (setExit, _, _) = await Run(["sweep", "interval-hours", "8760"], store);
        var (_, showOut, _) = await Run(["sweep", "show"], store);

        setExit.ShouldBe(0);
        showOut.ShouldContain("interval: 8760 h");
        SweepConfigKeys.ParseIntervalHours(store.Settings[SweepConfigKeys.IntervalHoursGlobal]).ShouldBe(8760);
    }
}
