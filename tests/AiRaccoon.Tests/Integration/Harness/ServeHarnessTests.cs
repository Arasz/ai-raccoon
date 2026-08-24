using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Harness;

/// <summary>
///     Pins the line wait: it scans every captured line, not just the first, and every way it can
///     give up names what it waited for and hands back the captured streams.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ServeHarnessTests
{
    private static readonly TimeSpan ShortDeadline = TimeSpan.FromMilliseconds(200);
    private const string Url = "http://127.0.0.1:5150/mcp";

    [Fact]
    public async Task WaitForLineAsync_FindsTheUrl_WhenItIsTheOnlyLine()
    {
        var stdout = new LockingWriter();
        stdout.WriteLine(Url);
        await using var harness = Harness(stdout, new LockingWriter(), Task.FromResult(ExitCode.Success));

        var line = await harness.WaitForUrlAsync(TestContext.Current.CancellationToken);

        line.ShouldBe(Url);
    }

    [Fact]
    public async Task WaitForLineAsync_FindsTheUrl_WhenOutputPrecedesIt()
    {
        var stdout = new LockingWriter();
        stdout.WriteLine("warming up the bank");
        stdout.WriteLine(Url);
        await using var harness = Harness(stdout, new LockingWriter(), Task.FromResult(ExitCode.Success));

        var line = await harness.WaitForUrlAsync(TestContext.Current.CancellationToken);

        line.ShouldBe(Url);
    }

    [Fact]
    public async Task WaitForLineAsync_ReportsAnEarlyExit_RatherThanBurningTheDeadline()
    {
        var stderr = new LockingWriter();
        stderr.WriteLine("port 5150 is in use");
        await using var harness = Harness(new LockingWriter(), stderr, Task.FromResult(ExitCode.PortInUse));

        var error = await Should.ThrowAsync<InvalidOperationException>(
            async () => await harness.WaitForUrlAsync(TestContext.Current.CancellationToken));

        error.Message.ShouldContain("a URL line");
        error.Message.ShouldContain(ExitCode.PortInUse.ToString());
        error.Message.ShouldContain("port 5150 is in use");
    }

    [Fact]
    public async Task WaitForLineAsync_TimesOutNamingTheWait_AndDumpingStderr()
    {
        var stderr = new LockingWriter();
        stderr.WriteLine("still booting");
        var pending = new TaskCompletionSource<int>();
        var harness = Harness(new LockingWriter(), stderr, pending.Task);
        try
        {
            var error = await Should.ThrowAsync<TimeoutException>(
                async () => await harness.WaitForUrlAsync(TestContext.Current.CancellationToken));

            error.Message.ShouldContain("a URL line");
            error.Message.ShouldContain("still booting");
        }
        finally
        {
            pending.SetResult(ExitCode.Success);
            await harness.DisposeAsync();
        }
    }

    private static ServeHarness Harness(LockingWriter stdout, LockingWriter stderr, Task<int> exit) =>
        ServeHarness.ForCapturedOutput(stdout, stderr, exit, ShortDeadline);
}
