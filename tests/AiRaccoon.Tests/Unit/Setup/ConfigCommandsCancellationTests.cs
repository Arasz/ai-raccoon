using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     F37 / ruling K4: Ctrl-C during a one-shot CLI command is a cancellation, not a bad argument.
///     The bare catch-all reported the measured interrupt as InvalidArgument (15) with
///     "The operation was canceled."; the caller now gets 130 and a line saying nothing changed.
///     Driven with an already-cancelled token and a store that observes it, never a real SIGINT.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ConfigCommandsCancellationTests
{
    [Fact]
    public async Task CallerCancellation_Exits130_AndSaysTheCommandChangedNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var commands = TestData.CreateConfigCommands(new CancelledStore(), settings: new SettingsCommands());

        var (exit, outp, err) = await CliRun.RunAsync(["settings", "sweep", "show"],
            (parsed, streams, _) => commands.RunAsync(parsed, streams, cts.Token));

        exit.ShouldBe(ExitCode.Interrupted);
        exit.ShouldBe(130);
        outp.ShouldBeEmpty();
        err.ShouldContain("changed nothing");
    }

    /// <summary>Positive control for the filter: an OperationCanceledException whose caller token is
    /// live is not a Ctrl-C and keeps the existing catch-all shape.</summary>
    [Fact]
    public async Task CancellationWithoutACancelledCallerToken_StillTakesTheCatchAll()
    {
        var commands = TestData.CreateConfigCommands(new CancelledStore(), settings: new SettingsCommands());

        var (exit, _, _) = await CliRun.RunAsync(["settings", "sweep", "show"], commands);

        exit.ShouldBe(ExitCode.InvalidArgument);
    }

    /// <summary>Stands in for the auto-start acquire: it observes the caller's token exactly where the
    /// measured interrupt landed, before any write was delivered.</summary>
    private sealed class CancelledStore : FakeMemoryStore
    {
        public override Task<string?> GetSettingAsync(string key, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException("simulated acquire cancellation");
        }
    }
}