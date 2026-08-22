using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.TestHelpers;

/// <summary>
///     Pins the defects the hand-rolled Console redirect blocks carried: a restore that an throwing
///     action could skip, a snapshot that races a still-writing background thread, and a capture
///     that could not be nested inside the gate four of its call sites already hold.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
[Collection(ConsoleCaptureCollection.Name)]
public sealed class ConsoleCaptureTests
{
    [Fact]
    public void Run_ReturnsWhatTheActionWroteToEachStream()
    {
        var (stdout, stderr) = ConsoleCapture.Run(() =>
        {
            Console.Write("to stdout");
            Console.Error.Write("to stderr");
        });

        stdout.ShouldBe("to stdout");
        stderr.ShouldBe("to stderr");
    }

    [Fact]
    public async Task RunAsync_ReturnsWhatTheActionWroteToEachStream()
    {
        var (stdout, stderr) = await ConsoleCapture.RunAsync(async () =>
        {
            await Task.Yield();
            Console.Write("async stdout");
            Console.Error.Write("async stderr");
        });

        stdout.ShouldBe("async stdout");
        stderr.ShouldBe("async stderr");
    }

    [Fact]
    public void Run_WhenTheActionThrows_StillRestoresBothStreams()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;

        Should.Throw<InvalidOperationException>(() => ConsoleCapture.Run(
            () => throw new InvalidOperationException("boom")));

        Console.Out.ShouldBeSameAs(originalOut, "a throwing action stranded Console.Out");
        Console.Error.ShouldBeSameAs(originalError, "a throwing action stranded Console.Error");
    }

    [Fact]
    public async Task RunAsync_WhenTheActionThrows_StillRestoresBothStreams()
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;

        await Should.ThrowAsync<InvalidOperationException>(() => ConsoleCapture.RunAsync(
            () => throw new InvalidOperationException("boom")));

        Console.Out.ShouldBeSameAs(originalOut, "a throwing action stranded Console.Out");
        Console.Error.ShouldBeSameAs(originalError, "a throwing action stranded Console.Error");
    }

    [Fact]
    public async Task RunAsync_WhileTheEnvVarGateIsHeld_StillCompletes()
    {
        // Four call sites redirect Console while already holding TestData.EnvVarGate, and a
        // SemaphoreSlim(1, 1) is not reentrant: a capture that took the gate itself would wedge them.
        await TestData.EnvVarGate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var capture = ConsoleCapture.RunAsync(() =>
            {
                Console.Write("written under the gate");
                return Task.CompletedTask;
            });

            // Bounded on purpose: a capture that waited on the held gate must surface here as a
            // TimeoutException rather than hang the assembly.
            var (stdout, _) = await capture.WaitAsync(TestContext.Current.CancellationToken);

            stdout.ShouldBe("written under the gate");
        }
        finally
        {
            TestData.EnvVarGate.Release();
        }
    }

    [Fact]
    public async Task Snapshot_WhileAThreadHoldingTheWriterKeepsWriting_DoesNotThrow()
    {
        // A logger that cached Console.Out keeps writing to the captured writer after the action
        // returns; StringWriter.ToString() reads the StringBuilder unsynchronised and tears.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            using var stop = new CancellationTokenSource();
            Task? chatter = null;
            try
            {
                await ConsoleCapture.RunAsync(async () =>
                {
                    var cached = Console.Out;
                    chatter = Task.Run(() =>
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            cached.Write("chatter");
                        }
                    }, CancellationToken.None);
                    await Task.Delay(1, TestContext.Current.CancellationToken);
                });
            }
            finally
            {
                await stop.CancelAsync();
                if (chatter is not null)
                {
                    await chatter;
                }
            }
        }
    }
}
