using System.Net;
using System.Net.Sockets;
using System.Text;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup.Cli;
using Shouldly;
using Xunit;
using NodeRunner = AiRaccoon.Hosting.Node.NodeRunner;

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
    private readonly List<TcpListener> _holders = [];

    public void Dispose()
    {
        foreach (var holder in _holders)
        {
            holder.Stop();
        }

        Directory.Delete(_dataRoot, true);
    }

    [Fact]
    public async Task NothingListening_ServesLikePlainServe()
    {
        using var env = await AcquireCleanEnvAsync();
        var port = FreePort();
        var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var url = await WaitForUrlAsync(run);

        url.ShouldBe($"http://127.0.0.1:{port}/mcp");
        run.Stderr.ToString().ShouldNotContain("   at ");
        (await StopAsync(run)).ShouldBe(ExitCode.Success);
    }

    [Fact]
    public async Task AnExistingServer_IsCycled_AndTheRestartOwnsThePort()
    {
        using var env = await AcquireCleanEnvAsync();
        var port = FreePort();
        var old = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        await WaitForUrlAsync(old);

        var restarted = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);
        var url = await WaitForUrlAsync(restarted);

        // The old server really exited — its own run completed, it was not merely bypassed.
        var oldExit = await old.Exit.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        oldExit.ShouldBe(ExitCode.Success);
        url.ShouldBe($"http://127.0.0.1:{port}/mcp");
        restarted.Stderr.ToString().ShouldNotContain("attached");
        // And the port answers for the restarted process, not a survivor.
        (await ProbeAsync(port)).ShouldBeTrue();
        (await StopAsync(restarted)).ShouldBe(ExitCode.Success);
    }

    [Fact]
    public async Task AServerThatRefusesOurToken_ExitsRestartFailed_AndNeverAttaches()
    {
        using var env = await AcquireCleanEnvAsync();
        var port = FreePort();
        // Our data root has a token; the listener rejects it, i.e. it serves a different root.
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        await using var fake = await FakeRaccoon.StartAsync(port, HttpStatusCode.Unauthorized,
            TestContext.Current.CancellationToken);
        var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        exit.ShouldBe(ExitCode.RestartFailed);
        run.Stdout.ToString().ShouldBeEmpty();
        run.Stderr.ToString().ShouldContain("restart");
        run.Stderr.ToString().ShouldNotContain("   at ");
        fake.ShutdownRequests.ShouldBe(1);
    }

    [Fact]
    public async Task AServerTooOldToBeCycled_ExitsRestartFailed()
    {
        using var env = await AcquireCleanEnvAsync();
        var port = FreePort();
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        await using var fake = await FakeRaccoon.StartAsync(port, HttpStatusCode.NotFound,
            TestContext.Current.CancellationToken);
        var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        exit.ShouldBe(ExitCode.RestartFailed);
        run.Stderr.ToString().ShouldContain("restart");
        run.Stdout.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task AServerWeHoldNoTokenFor_ExitsRestartFailed_WithoutAskingItToStop()
    {
        using var env = await AcquireCleanEnvAsync();
        var port = FreePort();
        await using var fake = await FakeRaccoon.StartAsync(port, HttpStatusCode.Accepted,
            TestContext.Current.CancellationToken);
        var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        exit.ShouldBe(ExitCode.RestartFailed);
        // No token to present, so nothing was asked to stop: an unauthenticated shutdown is not attempted.
        fake.ShutdownRequests.ShouldBe(0);
        run.Stderr.ToString().ShouldContain(new McpTokenFile(_dataRoot).Path);
    }

    [Fact]
    public async Task AListenerThatWillNotIdentify_ReportsPortInUse_WithoutAskingItToStop()
    {
        using var env = await AcquireCleanEnvAsync();
        var port = FreePort();
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        // Speaks JSON-RPC on /mcp, so the probe recognizes it, but /observability names someone else.
        await using var fake = await FakeRaccoon.StartAsync(port, HttpStatusCode.Accepted,
            TestContext.Current.CancellationToken, name: "not-a-raccoon");
        var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        // Nothing took the port: the same listener held it throughout.
        run.Stderr.ToString().ShouldContain("does not identify as an ai-raccoon");
        run.Stderr.ToString().ShouldNotContain("took the port");
        exit.ShouldBe(ExitCode.PortInUse);
        fake.ShutdownRequests.ShouldBe(0);
        run.Stdout.ToString().ShouldBeEmpty();
    }

    [Fact]
    public async Task AServerThatReportsNoVersion_IsStillNamedInTheRefusal()
    {
        using var env = await AcquireCleanEnvAsync();
        var port = FreePort();
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        // A pre-ADR-0022 server: identifies as an ai-raccoon, reports no version, has no /shutdown.
        await using var fake = await FakeRaccoon.StartAsync(port, HttpStatusCode.NotFound,
            TestContext.Current.CancellationToken, version: null);
        var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        exit.ShouldBe(ExitCode.RestartFailed);
        run.Stderr.ToString().ShouldContain("(version not reported)");
        run.Stderr.ToString().ShouldNotContain("the ai-raccoon  ");
    }

    [Fact]
    public async Task AForeignListener_StillReportsPortInUse()
    {
        using var env = await AcquireCleanEnvAsync();
        using var holder = HoldPort(out var port);
        var run = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--restart"]);

        var exit = await run.Exit.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        // Not an ai-raccoon, so it is never asked to stop; the unchanged in-use line is the answer.
        exit.ShouldBe(ExitCode.PortInUse);
        run.Stderr.ToString().ShouldContain("in use");
    }

    [Fact]
    public async Task WithoutRestart_AnExistingServerIsStillAttachedTo()
    {
        using var env = await AcquireCleanEnvAsync();
        var port = FreePort();
        var old = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        await WaitForUrlAsync(old);

        var second = Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        var exit = await second.Exit.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        exit.ShouldBe(ExitCode.Success);
        second.Stderr.ToString().ShouldContain("attached");
        old.Exit.IsCompleted.ShouldBeFalse();
        (await StopAsync(old)).ShouldBe(ExitCode.Success);
    }

    private static Task<bool> ProbeAsync(int port) => ServerProbe.ForLoopback().RespondsAsync(port, TestContext.Current.CancellationToken);

    private static ServeRun Start(string[] args)
    {
        CliArgs.TryParse(args, out var parsed);
        parsed.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["serve"]);
        var stdout = new LockingWriter();
        var stderr = new LockingWriter();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        return new ServeRun(NodeRunner.RunAsync(parsed, parsed.Options.ToServerConfig(), stdout, stderr, cts.Token),
            stdout, stderr, cts);
    }

    private static async Task<int> StopAsync(ServeRun run)
    {
        await run.Cts.CancelAsync();
        return await run.Exit;
    }

    private static async Task<string> WaitForUrlAsync(ServeRun run)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            var line = run.Stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (line is not null && line.StartsWith("http://", StringComparison.Ordinal))
            {
                return line.TrimEnd('\r');
            }

            if (run.Exit.IsCompleted)
            {
                throw new InvalidOperationException($"serve exited {await run.Exit} before reporting a URL; stderr: {run.Stderr}");
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"timed out waiting for serve output; stderr: {run.Stderr}");
    }

    private static async Task<IDisposable> AcquireCleanEnvAsync()
    {
        await TestData.EnvVarGate.WaitAsync(TestContext.Current.CancellationToken);
        var original = Environment.GetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName);
        Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, null);
        return new EnvRestore(original);
    }

    private TcpListener HoldPort(out int port)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var candidate = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var holder = new TcpListener(IPAddress.Loopback, candidate);
            try
            {
                holder.Start();
                port = candidate;
                _holders.Add(holder);
                _ = Task.Run(AcceptLoop);
                return holder;

                async Task AcceptLoop()
                {
                    while (true)
                    {
                        try
                        {
                            using var client = await holder.AcceptTcpClientAsync();
                        }
                        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
                        {
                            return;
                        }
                    }
                }
            }
            catch (SocketException)
            {
                holder.Stop();
            }
        }

        throw new InvalidOperationException("Could not reserve a loopback port");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record ServeRun(Task<int> Exit, LockingWriter Stdout, LockingWriter Stderr, CancellationTokenSource Cts);

    private sealed class EnvRestore(string? original) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, original);
            TestData.EnvVarGate.Release();
        }
    }

    private sealed class LockingWriter : TextWriter
    {
        private readonly StringBuilder _buffer = new();
        private readonly Lock _lock = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_lock)
            {
                _buffer.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_lock)
            {
                _buffer.Append(value);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_lock)
            {
                _buffer.AppendLine(value);
            }
        }

        public override string ToString()
        {
            lock (_lock)
            {
                return _buffer.ToString();
            }
        }
    }
}
