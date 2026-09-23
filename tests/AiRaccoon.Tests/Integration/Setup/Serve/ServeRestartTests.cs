using System.Net;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.TestHelpers;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     `serve --restart` acceptance (ADR-0022, ADR-0106): plain-serve behaviour when nothing is
///     listening, a real cycle when a <em>proven</em> server is, and a loud non-zero exit for every
///     way the cycle can fail — never a silent attach to the server it was asked to replace, and
///     never the token to a listener that merely claims the name but cannot prove identity.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ServeRestartTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-serve-restart");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task NothingListening_ServesLikePlainServe()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var url = await WaitForUrlAsync(run);

        url.ShouldBe($"http://127.0.0.1:{port}/mcp");
        run.Stderr.ShouldNotContain("   at ");
        (await run.StopAsync()).ShouldBe(ErrorCode.Ok.Success);
    }

    /// <summary>
    ///     The revert gate: a bare `serve --restart` cycles a proven server with no flag at all.
    /// </summary>
    [RetryFact]
    public async Task Restart_Bare_AgainstAProvenServer_CyclesIt()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));

        // A port released for the old server to bind can be taken by any process before the restart
        // claims it. That is RestartLostThePort and nothing else, so it costs a fresh port, not a red.
        await RetryingOnALostPortAsync(async port =>
        {
            await using var old = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
            await WaitForUrlAsync(old);

            await using var restarted = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);
            var url = await WaitForUrlAsync(restarted);

            // The old server really exited — its own run completed, it was not merely bypassed.
            var oldExit = await old.Exit.WaitAsync(TestContext.Current.CancellationToken);
            oldExit.ShouldBe(ErrorCode.Ok.Success);
            url.ShouldBe($"http://127.0.0.1:{port}/mcp");
            restarted.Stderr.ShouldNotContain("attached");
            // And the port answers for the restarted process, not a survivor.
            (await ProbeAsync(port)).ShouldBeTrue();
            (await restarted.StopAsync()).ShouldBe(ErrorCode.Ok.Success);
        });
    }

    /// <summary>
    ///     The join-review gap, now proof-shaped: a listener that claims the ai-raccoon name but cannot
    ///     prove it holds this root's identity key receives nothing but the probe and the challenge —
    ///     no identify read, no shutdown request — and the refusal names the remedy.
    /// </summary>
    [RetryFact]
    public async Task Restart_Bare_AgainstAnUnprovenHolder_RefusesUnproven_SendsNoToken()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        // The holder sits where a real token would be: before the proof gate it received the value.
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        lease.ReleaseForBind();
        await using var fake = await FakeRaccoon.StartAsync(port, HttpStatusCode.Accepted,
            TestContext.Current.CancellationToken);
        await using var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TestContext.Current.CancellationToken);

        fake.ShutdownTokenHeaders.ShouldBeEmpty(
            "restart handed the data root's token to a listener that could not prove identity");
        fake.ShutdownRequests.ShouldBe(0);
        fake.ObservabilityRequests.ShouldBe(0,
            "an unproven listener may receive only the probe and the challenge (ADR-0106 D5), not the identify read");
        exit.ShouldBe(ErrorCode.Server.Unproven);
        run.Stderr.ShouldContain("did not prove");
        run.Stderr.ShouldContain("stop the listener");
        run.Stderr.ShouldNotContain("--attach");
        run.Stdout.ShouldBeEmpty();
    }

    /// <summary>Runs the body on a fresh port while a restart keeps losing the port to another process.</summary>
    private static async Task RetryingOnALostPortAsync(Func<int, Task> body, int attempts = 4)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var lease = LoopbackPort.Reserve();
            var port = lease.Port;
            lease.ReleaseForBind();
            try
            {
                await body(port);
                return;
            }
            catch (ServeExitedException lost) when (lost.ExitCode == ErrorCode.Port.LostDuringRestart)
            {
                if (attempt >= attempts)
                {
                    throw new InvalidOperationException(
                        $"a restart lost the port to another process on all {attempts} attempts", lost);
                }
            }
        }
    }

    [RetryFact]
    public async Task AServerThatRefusesOurToken_ExitsRestartTokenRefused_AndNeverAttaches()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        // Our data root has a token; the listener rejects it, i.e. it serves a different root.
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        lease.ReleaseForBind();
        await using var fake = await StartProvenFakeAsync(port, HttpStatusCode.Unauthorized);
        await using var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TestContext.Current.CancellationToken);

        exit.ShouldBe(ErrorCode.Server.RestartTokenRefused);
        run.Stdout.ShouldBeEmpty();
        run.Stderr.ShouldContain("restart");
        run.Stderr.ShouldNotContain("   at ");
        fake.ShutdownRequests.ShouldBe(1);
        fake.ShutdownTokenHeaders.ShouldNotBeEmpty("the proven listener is the one that may be sent the token");
    }

    [RetryFact]
    public async Task AServerTooOldToBeCycled_ExitsRestartUnsupportedServer()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        lease.ReleaseForBind();
        await using var fake = await StartProvenFakeAsync(port, HttpStatusCode.NotFound);
        await using var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TestContext.Current.CancellationToken);

        exit.ShouldBe(ErrorCode.Server.TooOldToRestart);
        run.Stderr.ShouldContain("restart");
        run.Stdout.ShouldBeEmpty();
    }

    [RetryFact]
    public async Task AServerWeHoldNoTokenFor_ExitsRestartNoToken_WithoutAskingItToStop()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        // The fake mints the identity key (so it can prove) but no token exists in this root.
        await using var fake = await StartProvenFakeAsync(port, HttpStatusCode.Accepted);
        await using var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TestContext.Current.CancellationToken);

        exit.ShouldBe(ErrorCode.Server.NoToken);
        // No token to present, so nothing was asked to stop: an unauthenticated shutdown is not attempted.
        fake.ShutdownRequests.ShouldBe(0);
        run.Stderr.ShouldContain(new McpTokenFile(_dataRoot).Path);
    }

    [RetryFact]
    public async Task AListenerThatWillNotIdentify_ReportsForeignListener_WithoutAskingItToStop()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        // Proves this root's key, but /observability names someone else: the identify read runs only
        // after the proof, and a name mismatch there still stops the restart before the token.
        lease.ReleaseForBind();
        await using var fake = await StartProvenFakeAsync(port, HttpStatusCode.Accepted, name: "not-a-raccoon");
        await using var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TestContext.Current.CancellationToken);

        // Nothing took the port: the same listener held it throughout.
        run.Stderr.ShouldContain("does not identify as an ai-raccoon");
        run.Stderr.ShouldNotContain("took the port");
        exit.ShouldBe(ErrorCode.Port.ForeignListener);
        fake.ShutdownRequests.ShouldBe(0);
        run.Stdout.ShouldBeEmpty();
    }

    [RetryFact]
    public async Task AServerThatReportsNoVersion_IsStillNamedInTheRefusal()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        // A pre-ADR-0022 server: identifies as an ai-raccoon, reports no version, has no /shutdown.
        lease.ReleaseForBind();
        await using var fake = await StartProvenFakeAsync(port, HttpStatusCode.NotFound, version: null);
        await using var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TestContext.Current.CancellationToken);

        exit.ShouldBe(ErrorCode.Server.TooOldToRestart);
        run.Stderr.ShouldContain("(version not reported)");
        run.Stderr.ShouldNotContain("the ai-raccoon  ");
    }

    /// <summary>
    ///     A listener that holds the port and never reads the connection: the probe can only time
    ///     out, deterministically, on any machine. `serve --restart` must not read that silence as
    ///     an empty port, and — once the bind refutes it — must not claim a restart it never
    ///     attempted (ADR-0043).
    /// </summary>
    [RetryFact]
    public async Task AListenerThatNeverAnswersTheProbe_SaysNothingWasAskedToStop()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        // Reserved and never released: connections queue in the backlog and are never answered.
        using var silent = LoopbackPort.Reserve();
        await using var run = Start(["--data-root", _dataRoot, "serve", "--port", silent.Port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TestContext.Current.CancellationToken);

        exit.ShouldBe(ErrorCode.Port.HeldUnanswered);
        run.Stderr.ShouldContain("in use");
        run.Stderr.ShouldContain("no answer");
        run.Stderr.ShouldContain("nothing was asked to stop");
        // Nothing was restarted, so nothing can have been taken from this run.
        run.Stderr.ShouldNotContain("took the port");
        run.Stdout.ShouldBeEmpty();
        run.Stderr.ShouldNotContain("   at ");
    }

    /// <summary>
    ///     A raw listener that accepts the connection and hangs up answers no probe, so `serve`
    ///     cannot say what holds the port — only that the bind proved something does. Before
    ///     ADR-0043 that was the plain in-use line and <see cref="ErrorCode.Port.InUse" />, which
    ///     hid that the restart never happened; now the code says so and stays retryable.
    /// </summary>
    [RetryFact]
    public async Task AForeignListener_ReportsAPortInUseThatNothingWasAskedToStop()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        using var holder = LoopbackPort.Occupy();
        await using var run = Start(["--data-root", _dataRoot, "serve", "--port", holder.Port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TestContext.Current.CancellationToken);

        exit.ShouldBe(ErrorCode.Port.HeldUnanswered);
        run.Stderr.ShouldContain("in use");
        run.Stderr.ShouldContain("nothing was asked to stop");
    }

    /// <summary>
    ///     A plain `serve` (no restart) against a proven server attaches and exits 0, leaving the
    ///     owner serving — the pre-#643 shape, back behind the proof.
    /// </summary>
    [RetryFact]
    public async Task WithoutRestart_OnAProvenListener_AttachesAndExitsZero()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var old = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        await WaitForUrlAsync(old);

        await using var second = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        var exit = await second.Exit.WaitAsync(TestContext.Current.CancellationToken);

        exit.ShouldBe(ErrorCode.Ok.Success);
        second.Stderr.ShouldContain("attached");
        second.Stderr.ShouldContain("proved");
        old.Exit.IsCompleted.ShouldBeFalse();
        (await old.StopAsync()).ShouldBe(ErrorCode.Ok.Success);
    }

    /// <summary>A FakeRaccoon that proves it serves <see cref="_dataRoot"/>'s root, so the restart
    /// reaches the token-read stage instead of refusing it as unproven.</summary>
    private async Task<FakeRaccoon> StartProvenFakeAsync(int port, HttpStatusCode shutdownStatus,
        string name = "ai-raccoon", string? version = "0.0.0-fake")
    {
        var keyFile = new IdentityKeyFile(TestData.CreateInfrastructureOptions(_dataRoot));
        var signer = await keyFile.EnsureAsync(TestContext.Current.CancellationToken)
                     ?? throw new InvalidOperationException("the fixture could not mint its identity key");
        return await FakeRaccoon.StartAsync(port, shutdownStatus, TestContext.Current.CancellationToken, name, version,
            new FakeRaccoonProof { Signer = signer, RootFp = IdentityProof.RootFingerprint(keyFile.StateDirectory) });
    }

    private static Task<bool> ProbeAsync(int port) => TestData.CreateServerProbe().RespondsAsync(port, TestContext.Current.CancellationToken);

    private static ServeHarness Start(string[] args) => ServeHarness.Start(args, TimeSpan.FromSeconds(120));

    private static Task<string> WaitForUrlAsync(ServeHarness run) =>
        run.WaitForUrlAsync(TestContext.Current.CancellationToken);
}
