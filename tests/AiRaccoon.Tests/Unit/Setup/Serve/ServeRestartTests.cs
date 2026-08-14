using System.Net;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     `serve --restart` acceptance (ADR-0022): plain-serve behaviour when nothing is listening,
///     a real cycle when a server is, and a loud non-zero exit for every way the cycle can fail —
///     never a silent attach to the server it was asked to replace.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ServeRestartTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-serve-restart");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task NothingListening_ServesLikePlainServe()
    {
        using var env = await AcquireCleanEnvAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var url = await WaitForUrlAsync(run);

        url.ShouldBe($"http://127.0.0.1:{port}/mcp");
        run.Stderr.ShouldNotContain("   at ");
        (await run.StopAsync()).ShouldBe(ExitCode.Success);
    }

    [Fact]
    public async Task AnExistingServer_IsCycled_AndTheRestartOwnsThePort()
    {
        using var env = await AcquireCleanEnvAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var old = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        await WaitForUrlAsync(old);

        await using var restarted = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);
        var url = await WaitForUrlAsync(restarted);

        // The old server really exited — its own run completed, it was not merely bypassed.
        var oldExit = await old.Exit.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        oldExit.ShouldBe(ExitCode.Success);
        url.ShouldBe($"http://127.0.0.1:{port}/mcp");
        restarted.Stderr.ShouldNotContain("attached");
        // And the port answers for the restarted process, not a survivor.
        (await ProbeAsync(port)).ShouldBeTrue();
        (await restarted.StopAsync()).ShouldBe(ExitCode.Success);
    }

    [Fact]
    public async Task AServerThatRefusesOurToken_ExitsRestartFailed_AndNeverAttaches()
    {
        using var env = await AcquireCleanEnvAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        // Our data root has a token; the listener rejects it, i.e. it serves a different root.
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        lease.ReleaseForBind();
        await using var fake = await FakeRaccoon.StartAsync(port, HttpStatusCode.Unauthorized,
            TestContext.Current.CancellationToken);
        await using var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        exit.ShouldBe(ExitCode.RestartFailed);
        run.Stdout.ShouldBeEmpty();
        run.Stderr.ShouldContain("restart");
        run.Stderr.ShouldNotContain("   at ");
        fake.ShutdownRequests.ShouldBe(1);
    }

    [Fact]
    public async Task AServerTooOldToBeCycled_ExitsRestartFailed()
    {
        using var env = await AcquireCleanEnvAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        lease.ReleaseForBind();
        await using var fake = await FakeRaccoon.StartAsync(port, HttpStatusCode.NotFound,
            TestContext.Current.CancellationToken);
        await using var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        exit.ShouldBe(ExitCode.RestartFailed);
        run.Stderr.ShouldContain("restart");
        run.Stdout.ShouldBeEmpty();
    }

    [Fact]
    public async Task AServerWeHoldNoTokenFor_ExitsRestartFailed_WithoutAskingItToStop()
    {
        using var env = await AcquireCleanEnvAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var fake = await FakeRaccoon.StartAsync(port, HttpStatusCode.Accepted,
            TestContext.Current.CancellationToken);
        await using var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        exit.ShouldBe(ExitCode.RestartFailed);
        // No token to present, so nothing was asked to stop: an unauthenticated shutdown is not attempted.
        fake.ShutdownRequests.ShouldBe(0);
        run.Stderr.ShouldContain(new McpTokenFile(_dataRoot).Path);
    }

    [Fact]
    public async Task AListenerThatWillNotIdentify_ReportsPortInUse_WithoutAskingItToStop()
    {
        using var env = await AcquireCleanEnvAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        // Speaks JSON-RPC on /mcp, so the probe recognizes it, but /observability names someone else.
        lease.ReleaseForBind();
        await using var fake = await FakeRaccoon.StartAsync(port, HttpStatusCode.Accepted,
            TestContext.Current.CancellationToken, name: "not-a-raccoon");
        await using var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        // Nothing took the port: the same listener held it throughout.
        run.Stderr.ShouldContain("does not identify as an ai-raccoon");
        run.Stderr.ShouldNotContain("took the port");
        exit.ShouldBe(ExitCode.PortInUse);
        fake.ShutdownRequests.ShouldBe(0);
        run.Stdout.ShouldBeEmpty();
    }

    [Fact]
    public async Task AServerThatReportsNoVersion_IsStillNamedInTheRefusal()
    {
        using var env = await AcquireCleanEnvAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        // A pre-ADR-0022 server: identifies as an ai-raccoon, reports no version, has no /shutdown.
        lease.ReleaseForBind();
        await using var fake = await FakeRaccoon.StartAsync(port, HttpStatusCode.NotFound,
            TestContext.Current.CancellationToken, version: null);
        await using var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        exit.ShouldBe(ExitCode.RestartFailed);
        run.Stderr.ShouldContain("(version not reported)");
        run.Stderr.ShouldNotContain("the ai-raccoon  ");
    }

    [Fact]
    public async Task AForeignListener_StillReportsPortInUse()
    {
        using var env = await AcquireCleanEnvAsync();
        using var holder = LoopbackPort.Occupy();
        await using var run = Start(["--data-root", _dataRoot, "serve", "--port", holder.Port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        // Not an ai-raccoon, so it is never asked to stop; the unchanged in-use line is the answer.
        exit.ShouldBe(ExitCode.PortInUse);
        run.Stderr.ShouldContain("in use");
    }

    [Fact]
    public async Task WithoutRestart_AnExistingServerIsStillAttachedTo()
    {
        using var env = await AcquireCleanEnvAsync();
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var old = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        await WaitForUrlAsync(old);

        await using var second = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        var exit = await second.Exit.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        exit.ShouldBe(ExitCode.Success);
        second.Stderr.ShouldContain("attached");
        old.Exit.IsCompleted.ShouldBeFalse();
        (await old.StopAsync()).ShouldBe(ExitCode.Success);
    }

    private static Task<bool> ProbeAsync(int port) => TestData.CreateServerProbe().RespondsAsync(port, TestContext.Current.CancellationToken);

    private static ServeHarness Start(string[] args) => ServeHarness.Start(args, TimeSpan.FromSeconds(120));

    private static Task<string> WaitForUrlAsync(ServeHarness run) =>
        run.WaitForUrlAsync(TestContext.Current.CancellationToken);

    private static async Task<IDisposable> AcquireCleanEnvAsync()
    {
        await TestData.EnvVarGate.WaitAsync(TestContext.Current.CancellationToken);
        var original = Environment.GetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName);
        Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, null);
        return new EnvRestore(original);
    }


    private sealed class EnvRestore(string? original) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, original);
            TestData.EnvVarGate.Release();
        }
    }
}
