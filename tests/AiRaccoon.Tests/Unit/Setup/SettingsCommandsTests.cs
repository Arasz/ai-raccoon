using System.Globalization;
using AiRaccoon.Access;
using AiRaccoon.Core.Degradation;
using AiRaccoon;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Core.Memory.QueryGuard;
using AiRaccoon.Setup.Cli;
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
                ["settings", "access", "default", "show"] => commands.AccessDefaultShowAsync(store, streams, ct),
                ["settings", "access", "list"] => commands.AccessListAsync(store, streams, ct),
                ["model", "set", "local"] => commands.ModelSetLocalAsync(parsed.ParsedCliArgs, store, streams, ct),
                ["settings", "model", "show"] => commands.ModelShowAsync(store, streams, ct),
                ["settings", "retrieval", "alpha", "set"] => commands.RetrievalAlphaSetAsync(parsed.ParsedCliArgs, store, streams, ct),
                ["settings", "retrieval", "alpha", "show"] => commands.RetrievalAlphaShowAsync(store, streams, ct),
                ["settings", "sweep", "enable"] => commands.SweepEnabledSetAsync(true, store, streams, ct),
                ["settings", "sweep", "disable"] => commands.SweepEnabledSetAsync(false, store, streams, ct),
                ["settings", "sweep", "interval-hours"] => commands.SweepIntervalHoursSetAsync(parsed.ParsedCliArgs, store, streams, ct),
                ["settings", "sweep", "threshold", "set"] => commands.SweepThresholdSetAsync(parsed.ParsedCliArgs, store, streams, ct),
                ["settings", "sweep", "show"] => commands.SweepShowAsync(store, streams, ct),
                ["settings", "noise", "enable"] => commands.NoiseEnabledSetAsync(true, store, streams, ct),
                ["settings", "noise", "disable"] => commands.NoiseEnabledSetAsync(false, store, streams, ct),
                ["settings", "noise", "show"] => commands.NoiseShowAsync(store, streams, ct),
                ["settings", "queryguard", "enable"] => commands.QueryGuardEnabledSetAsync(true, store, streams, ct),
                ["settings", "queryguard", "disable"] => commands.QueryGuardEnabledSetAsync(false, store, streams, ct),
                ["settings", "queryguard", "shadow", "enable"] => commands.QueryGuardShadowSetAsync(true, store, streams, ct),
                ["settings", "queryguard", "shadow", "disable"] => commands.QueryGuardShadowSetAsync(false, store, streams, ct),
                ["settings", "queryguard", "show"] => commands.QueryGuardShowAsync(store, streams, ct),
                ["settings", "queryguard", "structural", "enable"] => commands.QueryGuardStructuralSetAsync(true, store, streams, ct),
                ["settings", "queryguard", "structural", "disable"] => commands.QueryGuardStructuralSetAsync(false, store, streams, ct),
                ["settings", "queryguard", "structural", "threshold", "set"] =>
                    commands.QueryGuardStructuralThresholdSetAsync(parsed.ParsedCliArgs, store, streams, ct),
                _ => throw new InvalidOperationException($"unhandled: {string.Join(' ', parsed.CommandPath)}")
            };
        });

    [Fact]
    public async Task AccessDefaultShow_NoRow_PrintsRw()
    {
        var (exit, stdout, _) = await Run(["settings", "access", "default", "show"], new FakeConfigStore());

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

        var (exit, stdout, _) = await Run(["settings", "access", "list"], store);

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe(
            "default: rw\n" +
            "p1: full\n" +
            "p2: ro");
    }

    [Fact]
    public async Task RetrievalAlphaSet_NonNumeric_ReturnsError()
    {
        var (exit, _, err) = await Run(["settings", "retrieval", "alpha", "set", "bogus"], new FakeConfigStore());

        // WP11: validation failures return a named code, so a script can tell a typo from a
        // broken bank key (which is ExitCode.FailedToResolveEncryptionKey = 1).
        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("invalid alpha");
    }

    [Fact]
    public async Task RetrievalAlphaShow_NoRow_PrintsDefaultInvariantCulture()
    {
        var (exit, stdout, _) = await Run(["settings", "retrieval", "alpha", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe(0.5.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task ModelShow_NoProvider_PrintsFts5Only()
    {
        var (exit, stdout, _) = await Run(["settings", "model", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.ShouldContain("provider: (none");
    }

    [Fact]
    public async Task SweepShow_NoRow_PrintsTheDefaultPolicy()
    {
        var (exit, stdout, _) = await Run(["settings", "sweep", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("enabled: True  interval: 24 h  threshold: 0.3");
    }

    [Fact]
    public async Task SweepThresholdSet_RoundTripsThroughCliShowAndForgettingPolicyService()
    {
        var store = new FakeConfigStore();

        var (setExit, _, _) = await Run(["settings", "sweep", "threshold", "set", "0.55"], store);
        var (showExit, showOut, _) = await Run(["settings", "sweep", "show"], store);

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

        var (disableExit, _, _) = await Run(["settings", "sweep", "disable"], store);
        var (_, disabledOut, _) = await Run(["settings", "sweep", "show"], store);

        disableExit.ShouldBe(0);
        disabledOut.ShouldContain("enabled: False");
        SweepConfigKeys.ParseEnabled(store.Settings[SweepConfigKeys.EnabledGlobal]).ShouldBeFalse();

        var (enableExit, _, _) = await Run(["settings", "sweep", "enable"], store);
        var (_, enabledOut, _) = await Run(["settings", "sweep", "show"], store);

        enableExit.ShouldBe(0);
        enabledOut.ShouldContain("enabled: True");
        SweepConfigKeys.ParseEnabled(store.Settings[SweepConfigKeys.EnabledGlobal]).ShouldBeTrue();
    }

    [Fact]
    public async Task SweepIntervalHours_RoundTripsThroughCliShowAndSweepConfigKeys()
    {
        var store = new FakeConfigStore();

        var (setExit, _, _) = await Run(["settings", "sweep", "interval-hours", "8760"], store);
        var (_, showOut, _) = await Run(["settings", "sweep", "show"], store);

        setExit.ShouldBe(0);
        showOut.ShouldContain("interval: 8760 h");
        SweepConfigKeys.ParseIntervalHours(store.Settings[SweepConfigKeys.IntervalHoursGlobal]).ShouldBe(8760);
    }

    [Fact]
    public async Task NoiseShow_NoRow_PrintsEnabledByDefault()
    {
        var (exit, stdout, _) = await Run(["settings", "noise", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("enabled: True");
    }

    /// <summary>The kill switch round-trips through the same parse SqliteMemoryStore.WriteAsync reads it with.</summary>
    [Fact]
    public async Task NoiseDisableThenEnable_RoundTripsThroughCliShowAndNoiseConfigKeys()
    {
        var store = new FakeConfigStore();

        var (disableExit, _, _) = await Run(["settings", "noise", "disable"], store);
        var (_, disabledOut, _) = await Run(["settings", "noise", "show"], store);

        disableExit.ShouldBe(0);
        disabledOut.ShouldContain("enabled: False");
        NoiseConfigKeys.ParseEnabled(store.Settings[NoiseConfigKeys.EnabledGlobal]).ShouldBeFalse();

        var (enableExit, _, _) = await Run(["settings", "noise", "enable"], store);
        var (_, enabledOut, _) = await Run(["settings", "noise", "show"], store);

        enableExit.ShouldBe(0);
        enabledOut.ShouldContain("enabled: True");
        NoiseConfigKeys.ParseEnabled(store.Settings[NoiseConfigKeys.EnabledGlobal]).ShouldBeTrue();
    }

    [Fact]
    public async Task QueryGuardShow_NoRow_PrintsEnabledByDefault_ShadowOffByDefault()
    {
        var (exit, stdout, _) = await Run(["settings", "queryguard", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.Trim().ShouldBe("enabled: True  shadow: False  structural: False  " +
                               $"threshold: {QueryGuardConfigKeys.DefaultStructuralThreshold.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>The kill switch round-trips through the same parse MemoryTools.Search reads it with.</summary>
    [Fact]
    public async Task QueryGuardDisableThenEnable_RoundTripsThroughCliShowAndQueryGuardConfigKeys()
    {
        var store = new FakeConfigStore();

        var (disableExit, _, _) = await Run(["settings", "queryguard", "disable"], store);
        var (_, disabledOut, _) = await Run(["settings", "queryguard", "show"], store);

        disableExit.ShouldBe(0);
        disabledOut.ShouldContain("enabled: False");
        QueryGuardConfigKeys.ParseEnabled(store.Settings[QueryGuardConfigKeys.EnabledGlobal]).ShouldBeFalse();

        var (enableExit, _, _) = await Run(["settings", "queryguard", "enable"], store);
        var (_, enabledOut, _) = await Run(["settings", "queryguard", "show"], store);

        enableExit.ShouldBe(0);
        enabledOut.ShouldContain("enabled: True");
        QueryGuardConfigKeys.ParseEnabled(store.Settings[QueryGuardConfigKeys.EnabledGlobal]).ShouldBeTrue();
    }

    /// <summary>
    ///     ADR-0041 ships the structural detector default-off. Without a verb to arm it the setting
    ///     is unreachable through any supported path, so the feature ships dark.
    /// </summary>
    [Fact]
    public async Task QueryGuardStructuralEnableThenDisable_RoundTripsThroughCliShowAndQueryGuardConfigKeys()
    {
        var store = new FakeConfigStore();

        var (enableExit, _, _) = await Run(["settings", "queryguard", "structural", "enable"], store);
        var (_, enabledOut, _) = await Run(["settings", "queryguard", "show"], store);

        enableExit.ShouldBe(0);
        enabledOut.ShouldContain("structural: True");
        QueryGuardConfigKeys.ParseStructuralEnabled(store.Settings[QueryGuardConfigKeys.StructuralEnabledGlobal])
            .ShouldBeTrue();

        var (disableExit, _, _) = await Run(["settings", "queryguard", "structural", "disable"], store);
        var (_, disabledOut, _) = await Run(["settings", "queryguard", "show"], store);

        disableExit.ShouldBe(0);
        disabledOut.ShouldContain("structural: False");
        QueryGuardConfigKeys.ParseStructuralEnabled(store.Settings[QueryGuardConfigKeys.StructuralEnabledGlobal])
            .ShouldBeFalse();
    }

    [Fact]
    public async Task QueryGuardStructuralThresholdSet_RoundTripsThroughCliShowAndQueryGuardConfigKeys()
    {
        var store = new FakeConfigStore();

        var (setExit, _, _) = await Run(["settings", "queryguard", "structural", "threshold", "set", "0.75"], store);
        var (_, showOut, _) = await Run(["settings", "queryguard", "show"], store);

        setExit.ShouldBe(0);
        showOut.ShouldContain("threshold: 0.75");
        QueryGuardConfigKeys.ParseStructuralThreshold(store.Settings[QueryGuardConfigKeys.StructuralThresholdGlobal])
            .ShouldBe(0.75);
    }

    [Fact]
    public async Task QueryGuardStructuralThresholdSet_OutOfRange_ReturnsInvalidArgument()
    {
        var (exit, _, err) = await Run(["settings", "queryguard", "structural", "threshold", "set", "1.5"], new FakeConfigStore());

        exit.ShouldBe(ExitCode.InvalidArgument);
        err.ShouldContain("invalid threshold");
    }

    [Fact]
    public async Task QueryGuardShow_NoRow_PrintsStructuralDefaults()
    {
        var (exit, stdout, _) = await Run(["settings", "queryguard", "show"], new FakeConfigStore());

        exit.ShouldBe(0);
        stdout.ShouldContain("structural: False");
        stdout.ShouldContain($"threshold: {QueryGuardConfigKeys.DefaultStructuralThreshold.ToString(CultureInfo.InvariantCulture)}");
    }

    [Fact]
    public async Task QueryGuardShadowEnableThenDisable_RoundTripsThroughCliShowAndQueryGuardConfigKeys()
    {
        var store = new FakeConfigStore();

        var (enableExit, _, _) = await Run(["settings", "queryguard", "shadow", "enable"], store);
        var (_, enabledOut, _) = await Run(["settings", "queryguard", "show"], store);

        enableExit.ShouldBe(0);
        enabledOut.ShouldContain("shadow: True");
        QueryGuardConfigKeys.ParseShadow(store.Settings[QueryGuardConfigKeys.ShadowGlobal]).ShouldBeTrue();

        var (disableExit, _, _) = await Run(["settings", "queryguard", "shadow", "disable"], store);
        var (_, disabledOut, _) = await Run(["settings", "queryguard", "show"], store);

        disableExit.ShouldBe(0);
        disabledOut.ShouldContain("shadow: False");
        QueryGuardConfigKeys.ParseShadow(store.Settings[QueryGuardConfigKeys.ShadowGlobal]).ShouldBeFalse();
    }
}
