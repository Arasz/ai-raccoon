using AiRaccoon.Setup.Cli.Commands;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Setup;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.TestHelpers;

/// <summary>
///     The CLI harness every command test asserts Exit/Out/Err through. A harness that swallowed a
///     non-zero exit or merged the two streams would make those assertions vacuous, so each of
///     those failure modes is pinned here.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CliRunTests
{
    [Fact]
    public async Task NonZeroExit_IsSurfaced()
    {
        var (exit, _, _) = await CliRun.RunAsync(["access", "default", "show"],
            (_, _, _) => Task.FromResult(7));

        exit.ShouldBe(7);
    }

    /// <summary>A non-zero exit must not cost the output the test then asserts on.</summary>
    [Fact]
    public async Task NonZeroExit_StillReturnsBothStreams()
    {
        var (exit, stdout, stderr) = await CliRun.RunAsync(["access", "default", "show"],
            (_, streams, _) =>
            {
                streams.Output.Write("printed");
                streams.Error.Write("failed");
                return Task.FromResult(1);
            });

        exit.ShouldBe(1);
        stdout.ShouldBe("printed");
        stderr.ShouldBe("failed");
    }

    [Fact]
    public async Task StdoutAndStderr_AreCapturedSeparately()
    {
        var (_, stdout, stderr) = await CliRun.RunAsync(["access", "default", "show"],
            (_, streams, _) =>
            {
                streams.Output.Write("out-text");
                streams.Error.Write("err-text");
                return Task.FromResult(0);
            });

        stdout.ShouldBe("out-text");
        stderr.ShouldBe("err-text");
    }

    [Fact]
    public async Task StderrText_DoesNotLeakIntoStdout()
    {
        var (_, stdout, _) = await CliRun.RunAsync(["access", "default", "show"],
            (_, streams, _) =>
            {
                streams.Error.Write("err-only");
                return Task.FromResult(0);
            });

        stdout.ShouldBeEmpty();
    }

    [Fact]
    public async Task StdoutText_DoesNotLeakIntoStderr()
    {
        var (_, _, stderr) = await CliRun.RunAsync(["access", "default", "show"],
            (_, streams, _) =>
            {
                streams.Output.Write("out-only");
                return Task.FromResult(0);
            });

        stderr.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stdin_ReachesTheCommand()
    {
        var (_, stdout, _) = await CliRun.RunAsync(["access", "default", "show"],
            (_, streams, _) =>
            {
                streams.Output.Write(streams.Input.ReadLine());
                return Task.FromResult(0);
            },
            new StringReader("typed-answer\n"));

        stdout.ShouldBe("typed-answer");
    }

    /// <summary>Absent a stdin reader the command still gets a reader, not null.</summary>
    [Fact]
    public async Task NoStdin_GivesAnEmptyReader()
    {
        var (_, stdout, _) = await CliRun.RunAsync(["access", "default", "show"],
            (_, streams, _) =>
            {
                streams.Output.Write(streams.Input.ReadLine() ?? "(none)");
                return Task.FromResult(0);
            });

        stdout.ShouldBe("(none)");
    }

    [Fact]
    public async Task ParsedArgs_AreHandedToTheCommand()
    {
        var (_, stdout, _) = await CliRun.RunAsync(["access", "default", "set", "rw"],
            (parsed, streams, _) =>
            {
                streams.Output.Write(string.Join(' ', parsed.CommandPath));
                return Task.FromResult(0);
            });

        stdout.ShouldBe("access default set");
    }

    /// <summary>The ConfigCommands overload drives the real dispatcher end to end.</summary>
    [Fact]
    public async Task ConfigCommandsOverload_DispatchesTheRealCommand()
    {
        var store = new FakeConfigStore();

        var (exit, stdout, _) = await CliRun.RunAsync(["access", "default", "set", "rw"],
            TestData.CreateConfigCommands(store, settings: new SettingsCommands()));

        exit.ShouldBe(0);
        stdout.ShouldContain("rw");
        store.Settings["access.mode.global"].ShouldBe("rw");
    }
}
