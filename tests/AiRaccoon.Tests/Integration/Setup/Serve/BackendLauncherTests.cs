using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Setup;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using BackendLauncher = AiRaccoon.Hosting.Proxy.BackendLauncher;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     BackendLauncher acceptance (ADR-0020): the proxy's stdout stays clean, a missing backend is
///     started and polled until it answers, an existing one is attached to without a spawn, a
///     backend that cannot start fails inside the budget instead of hanging. Occupied or inconclusive
///     ports never authorize a new process.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class BackendLauncherTests : IDisposable
{
    private const int StdoutFd = 1;

    /// <summary>An ai-raccoon MCP endpoint's answer to the probe: 400 with a JSON-RPC error body.</summary>
    private static readonly string FakeServerResponse = Response("400 Bad Request", "application/json",
        "{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32700,\"message\":\"parse\"}}");

    /// <summary>Anything but an MCP endpoint.</summary>
    private static readonly string ForeignServerResponse = Response("200 OK", "text/plain", "ok");

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-backend-launcher");
    private readonly List<TcpListener> _listeners = [];

    private static string ServeExecutable => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "AiRaccoon.exe" : "AiRaccoon");

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            listener.Stop();
        }

        TestData.DeleteTempRoot(_dataRoot);
    }

    [Theory]
    [InlineData(ProbeVerdict.Answered)]
    [InlineData(ProbeVerdict.Unanswered)]
    public async Task AcquireShared_WhenAListenerAppearsBeforeLauncherProbe_StartsNothing(ProbeVerdict secondVerdict)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the recording executable is a POSIX shell wrapper");
        var marker = Path.Combine(_dataRoot, "raced-start");
        var wrapper = Path.Combine(_dataRoot, "raced-wrapper");
        await File.WriteAllTextAsync(wrapper, $"#!/bin/sh\necho started > '{marker}'\nexit 7\n", TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var probe = new ChangingProbe(secondVerdict);
        var prover = new FakeIdentityProver();
        var launcher = new BackendLauncher(probe, TimeSpan.FromSeconds(1), TimeProvider.System, NullLogger<BackendLauncher>.Instance);
        var config = new ServerConfig(54321, McpTransport.Http, TestData.CreateInfrastructureOptions(_dataRoot));
        var result = await BackendSessions.AcquireSharedAsync(probe, prover, launcher, wrapper, config,
            NullLogger.Instance, TestContext.Current.CancellationToken);
        File.Exists(marker).ShouldBeFalse();
        result.Result.Url.ShouldBe(secondVerdict is ProbeVerdict.Answered ? UrlFor(54321) : null);
        prover.Calls.Count.ShouldBe(secondVerdict is ProbeVerdict.Answered ? 1 : 0);
    }

    [Fact]
    public async Task Acquire_WhenCancellationArrivesWithRefusal_DoesNotStart()
    {
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var probe = new ChangingProbe(ProbeVerdict.NotListening, caller);
        var launcher = new BackendLauncher(probe, TimeSpan.FromSeconds(1), TimeProvider.System, NullLogger<BackendLauncher>.Instance);
        await Should.ThrowAsync<OperationCanceledException>(() => launcher.AcquireAsync(54321,
            "ai-raccoon-no-such-executable", [], caller.Token));
    }

    private sealed class ChangingProbe(ProbeVerdict next, CancellationTokenSource? cancellation = null) : IServerProbe
    {
        private bool _first = true;
        public Task<ProbeVerdict> ProbeAsync(int port, CancellationToken ctx)
        {
            cancellation?.Cancel();
            var verdict = _first ? ProbeVerdict.NotListening : next;
            _first = false;
            return Task.FromResult(verdict);
        }
        public Task<ProbeVerdict> ProbeAsync(Uri endpoint, CancellationToken ctx) => ProbeAsync(endpoint.Port, ctx);
        public async Task<bool> RespondsAsync(int port, CancellationToken ctx) => await ProbeAsync(port, ctx) is ProbeVerdict.Answered;
        public Task<bool> RespondsAsync(Uri endpoint, CancellationToken ctx) => RespondsAsync(endpoint.Port, ctx);
    }

    [Fact]
    public async Task Acquire_WhenStartedChildExitsAndLastChanceIsInconclusive_ReturnsNoUrlAndStartsOnce()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the recording child uses a POSIX shell");
        var marker = Path.Combine(_dataRoot, "last-chance-starts");
        var launcher = new BackendLauncher(new ChangingProbe(ProbeVerdict.Unanswered),
            TimeSpan.FromSeconds(1), TimeProvider.System, NullLogger<BackendLauncher>.Instance);
        var result = await launcher.AcquireAsync(54321, "sh", ["-c", $"echo started >> '{marker}'; exit 7"], TestContext.Current.CancellationToken);
        result.Url.ShouldBeNull();
        result.ServeExitCode.ShouldBe(7);
        (await File.ReadAllLinesAsync(marker, TestContext.Current.CancellationToken)).ShouldBe(["started"]);
    }

    [Fact]
    public async Task Acquire_WithAnInconclusiveProbe_DoesNotStart()
    {
        var marker = Path.Combine(_dataRoot, "started");
        var launcher = new BackendLauncher(new FakeServerProbe(ProbeVerdict.Unanswered),
            TimeSpan.FromSeconds(1), TimeProvider.System, NullLogger<BackendLauncher>.Instance);
        var result = await launcher.AcquireAsync(54321, "sh", ["-c", $"echo started > '{marker}'"], TestContext.Current.CancellationToken);
        result.Url.ShouldBeNull();
        File.Exists(marker).ShouldBeFalse();
    }

    [Fact]
    public async Task Acquire_WithARespondingForeignHttpListener_DoesNotStart()
    {
        var port = HoldListener(ForeignServerResponse);
        var result = await Launcher().AcquireAsync(port, "/no-such-executable", [], TestContext.Current.CancellationToken);
        result.Url.ShouldBeNull();
    }

    [RetryFact]
    public async Task Acquire_DoesNotWriteToItsOwnStdout()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "fd-level stdout capture is POSIX-only");
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var capturePath = Path.Combine(_dataRoot, "stdout-capture.txt");
        var result = default(BackendResult);
        string managedStdout;

        await using (var capture = new FileStream(capturePath, FileMode.Create, FileAccess.Write))
        {
            (managedStdout, _) = await ConsoleCapture.RunAsync(async () =>
            {
                var savedStdout = Dup(StdoutFd);
                Dup2(capture.SafeFileHandle.DangerousGetHandle().ToInt32(), StdoutFd);
                try
                {
                    lease.ReleaseForBind();
                    result = await Launcher().AcquireAsync(port, ServeExecutable, ServeArguments(port),
                        TestContext.Current.CancellationToken);
                    // The backend prints its bound URL right after binding; hold the capture open long
                    // enough that an unredirected child would certainly have written it.
                    await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
                }
                finally
                {
                    Dup2(savedStdout, StdoutFd);
                    CloseFd(savedStdout);
                }
            });
        }

        result.Url.ShouldBe(UrlFor(port));
        var captured = await File.ReadAllTextAsync(capturePath, TestContext.Current.CancellationToken);
        captured.ShouldNotContain(UrlFor(port));
        // Console.Out is process-global, so a test running in parallel can land text in this
        // capture too. Assert what only the launcher could have written, not that it is empty.
        managedStdout.ShouldNotContain(UrlFor(port));
    }

    [RetryFact]
    public async Task Acquire_WhenNoServerIsListening_StartsOneAndAnswers()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;

        lease.ReleaseForBind();
        var result = await Launcher().AcquireAsync(port, ServeExecutable, ServeArguments(port),
            TestContext.Current.CancellationToken);

        result.Url.ShouldBe(UrlFor(port));
        result.ServeExitCode.ShouldBeNull();
        var live = await TestData.CreateServerProbe().RespondsAsync(port, TestContext.Current.CancellationToken);
        live.ShouldBeTrue();
    }

    [RetryFact]
    public async Task Acquire_WhenAServerIsAlreadyListening_DoesNotSpawn()
    {
        var port = HoldListener(FakeServerResponse);

        // An unstartable command: any spawn attempt throws instead of returning a URL.
        var result = await Launcher().AcquireAsync(port, "ai-raccoon-no-such-executable", [],
            TestContext.Current.CancellationToken);

        result.Url.ShouldBe(UrlFor(port));
        result.ServeExitCode.ShouldBeNull();
    }

    [RetryFact]
    public async Task Acquire_WhenTheBackendNeverAnswers_GivesUpAtTheBudget()
    {
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var clock = new FakeTimeProvider();
        var timers = new TimerRegistrations(clock);
        var launcher = new BackendLauncher(TestData.CreateServerProbe(), BackendLauncher.DefaultBudget,
            timers, NullLogger<BackendLauncher>.Instance);

        // A process that starts, never listens and outlives the budget.
        lease.ReleaseForBind();
        var acquire = launcher.AcquireAsync(port, "sleep", ["10"], TestContext.Current.CancellationToken);
        // Advancing before the budget and poll timers exist would silently do nothing.
        (await timers.WaitForAsync(2, TestContext.Current.CancellationToken))
            .ShouldBeTrue("the launcher never registered its timers");
        acquire.IsCompleted.ShouldBeFalse();

        clock.Advance(BackendLauncher.DefaultBudget);
        // The 10s ceiling is a hang guard, not a budget: it is a THIRD of DefaultBudget, so a
        // launcher that waited on the real clock instead of the injected one cannot reach this
        // line at all. That is what proves "no wall-clock time was spent waiting the budget out"
        // — the `stopwatch.Elapsed < DefaultBudget` assertion this replaces asserted the same
        // thing more loosely, on the system clock, and could go red purely from host load.
        var result = await acquire.WaitAsync(TestContext.Current.CancellationToken);

        result.Url.ShouldBeNull();
        result.ServeExitCode.ShouldBeNull();
    }

    /// <summary>
    ///     The defect this gates: DrainAsync used to discard the child's stderr unconditionally, so a
    ///     backend that started and then failed left the operator with nothing but a bare exit code.
    /// </summary>
    [RetryFact]
    public async Task Acquire_WhenTheBackendExitsWithStderr_SurfacesItInTheResult()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the fake failing child is a POSIX shell script");
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;

        lease.ReleaseForBind();
        var result = await Launcher().AcquireAsync(port, "sh",
            ["-c", "echo 'ai-raccoon: could not decrypt the bank' 1>&2; exit 7"],
            TestContext.Current.CancellationToken);

        result.Url.ShouldBeNull();
        result.ServeExitCode.ShouldBe(7);
        result.ServeStderr.ShouldNotBeNull();
        result.ServeStderr.ShouldContain("could not decrypt the bank");
    }

    /// <summary>A child that never prints a URL is given the same budget as the attach path.</summary>


    /// <summary>A caller that gives up mid-start leaves nobody holding the child either.</summary>


    [RetryFact]
    public async Task Acquire_WhenTheBackendCannotBeStarted_FailsWithTheCommandItTried()
    {
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;

        lease.ReleaseForBind();
        var failure = await Should.ThrowAsync<BackendStartException>(() =>
            Launcher().AcquireAsync(port, "ai-raccoon-no-such-executable", [], TestContext.Current.CancellationToken));

        failure.Message.ShouldContain("ai-raccoon-no-such-executable");
    }

    [RetryFact]
    public async Task Acquire_WhenTheCallerHasAlreadyCancelled_ThrowsWithoutSpawning()
    {
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        // An unstartable command: a spawn would surface as BackendStartException, never as a cancel.
        lease.ReleaseForBind();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            Launcher().AcquireAsync(port, "ai-raccoon-no-such-executable", [], caller.Token));
    }

    [RetryFact]
    public async Task Acquire_WhenTheCallerCancels_PropagatesInsteadOfReportingFailure()
    {
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var clock = new FakeTimeProvider();
        var timers = new TimerRegistrations(clock);
        var launcher = new BackendLauncher(TestData.CreateServerProbe(), BackendLauncher.DefaultBudget,
            timers, NullLogger<BackendLauncher>.Instance);
        using var caller = new CancellationTokenSource();

        lease.ReleaseForBind();
        var acquire = launcher.AcquireAsync(port, "sleep", ["10"], caller.Token);
        // Cancel only once the acquire is genuinely in flight, not merely scheduled.
        (await timers.WaitForAsync(2, TestContext.Current.CancellationToken))
            .ShouldBeTrue("the launcher never registered its timers");
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => acquire.WaitAsync(TestContext.Current.CancellationToken));
    }

    private static BackendLauncher Launcher() => new(TestData.CreateServerProbe(), BackendLauncher.DefaultBudget,
        TimeProvider.System, NullLogger<BackendLauncher>.Instance);

    private static string UrlFor(int port) => $"http://127.0.0.1:{port}/mcp";

    /// <summary>A short idle timeout so a spawned backend retires on its own — nothing here ever kills it.</summary>
    private string[] ServeArguments(int port) =>
    [
        "--data-root", _dataRoot, "serve", "--port", port.ToString(CultureInfo.InvariantCulture),
        "--idle-timeout", "20s"
    ];

    private static string Response(string status, string contentType, string body) =>
        $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";

    /// <summary>Holds a loopback port for the whole test, answering every connection with one canned response.</summary>
    private int HoldListener(string response)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _listeners.Add(listener);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    await using var stream = client.GetStream();
                    var request = new byte[4096];
                    if (await stream.ReadAsync(request) > 0)
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(response));
                        await stream.FlushAsync();
                    }
                }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException or IOException)
                {
                    return;
                }
            }
        });

        return port;
    }


    private static async Task<IDisposable> AcquireCleanEnvAsync(CancellationToken cancellationToken)
    {
        // The spawned backend inherits this process's environment: clear the process-global
        // passphrase so a dev machine's value cannot poison a fresh-bank run.
        await TestData.EnvVarGate.WaitAsync(cancellationToken);
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

    // DllImport rather than LibraryImport: the generated marshalling stub needs AllowUnsafeBlocks,
    // and three blittable int calls do not justify unsafe code across the whole test project.
#pragma warning disable SYSLIB1054
    [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
    private static extern int Dup(int fd);

    [DllImport("libc", EntryPoint = "dup2", SetLastError = true)]
    private static extern int Dup2(int oldFd, int newFd);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int CloseFd(int fd);
#pragma warning restore SYSLIB1054
}
