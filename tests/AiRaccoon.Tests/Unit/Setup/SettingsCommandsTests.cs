using System.Globalization;
using AiRaccoon.Access;
using AiRaccoon.Core.Degradation;
using AiRaccoon;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
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
    /// <summary>Calls the component method directly — no dispatcher, that is the seam under test.</summary>
    private static Task<(int Exit, string Out, string Err)> Run(string[] args, FakeConfigStore store) =>
        CliRun.RunAsync(args, (parsed, streams, ct) =>
        {
            var commands = new SettingsCommands();
            return parsed.CommandPath switch
            {
                ["access", "default", "show"] => commands.AccessDefaultShowAsync(store, streams, ct),
                ["access", "list"] => commands.AccessListAsync(store, streams, ct),
                ["model", "set", "local"] => commands.ModelSetLocalAsync(parsed.ParsedCliArgs, store, streams, ct),
                ["model", "show"] => commands.ModelShowAsync(store, streams, ct),
                ["retrieval", "alpha", "set"] => commands.RetrievalAlphaSetAsync(parsed.ParsedCliArgs, store, streams, ct),
                ["retrieval", "alpha", "show"] => commands.RetrievalAlphaShowAsync(store, streams, ct),
                ["sweep", "enable"] => commands.SweepEnabledSetAsync(true, store, streams, ct),
                ["sweep", "disable"] => commands.SweepEnabledSetAsync(false, store, streams, ct),
                ["sweep", "interval-hours"] => commands.SweepIntervalHoursSetAsync(parsed.ParsedCliArgs, store, streams, ct),
                ["sweep", "threshold", "set"] => commands.SweepThresholdSetAsync(parsed.ParsedCliArgs, store, streams, ct),
                ["sweep", "show"] => commands.SweepShowAsync(store, streams, ct),
                ["noise", "enable"] => commands.NoiseEnabledSetAsync(true, store, streams, ct),
                ["noise", "disable"] => commands.NoiseEnabledSetAsync(false, store, streams, ct),
                ["noise", "show"] => commands.NoiseShowAsync(store, streams, ct),
                _ => throw new InvalidOperationException($"unhandled: {string.Join(' ', parsed.CommandPath)}")
            };
        });

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

        // WP11: validation failures return a named code, so a script can tell a typo from a
        // broken bank key (which is ExitCode.FailedToResolveEncryptionKey = 1).
        exit.ShouldBe(ExitCode.InvalidArgument);
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

    [Fact]
    public async Task NoiseShow_NoRow_PrintsEnabledByDefault()
    {
        var (exit, stdout, _) = await Run(["noise", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("enabled: True");
    }

    /// <summary>The kill switch round-trips through the same parse SqliteMemoryStore.WriteAsync reads it with.</summary>
    [Fact]
    public async Task NoiseDisableThenEnable_RoundTripsThroughCliShowAndNoiseConfigKeys()
    {
        var store = new FakeConfigStore();

        var (disableExit, _, _) = await Run(["noise", "disable"], store);
        var (_, disabledOut, _) = await Run(["noise", "show"], store);

        disableExit.ShouldBe(0);
        disabledOut.ShouldContain("enabled: False");
        NoiseConfigKeys.ParseEnabled(store.Settings[NoiseConfigKeys.EnabledGlobal]).ShouldBeFalse();

        var (enableExit, _, _) = await Run(["noise", "enable"], store);
        var (_, enabledOut, _) = await Run(["noise", "show"], store);

        enableExit.ShouldBe(0);
        enabledOut.ShouldContain("enabled: True");
        NoiseConfigKeys.ParseEnabled(store.Settings[NoiseConfigKeys.EnabledGlobal]).ShouldBeTrue();
    }
}
