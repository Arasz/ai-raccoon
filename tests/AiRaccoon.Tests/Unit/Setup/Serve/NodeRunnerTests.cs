using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Cli.Render;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup.Serve;

/// <summary>
///     ServeRunner acceptance: stdout URL reporting, foreign-listener PortInUse, idempotent
///     attach to an existing ai-raccoon server, bind-race recovery, and the --mcp-entry /
///     port-fallback / transport-warning surfaces.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class NodeRunnerTests : IDisposable
{
    private static readonly HttpClient HttpClient = new();
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-serve-runner");
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
    public async Task FreePort_PrintsExactUrlLine_AndExitsZeroAfterStop()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        var port = FreePort();
        var run = StartServe(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);

        var url = await WaitForLineAsync(run, line => line.StartsWith("http://", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        url.ShouldBe($"http://127.0.0.1:{port}/mcp");
        var exit = await StopAsync(run);

        exit.ShouldBe(ExitCode.Success);
        run.Stdout.ToString().ShouldBe($"http://127.0.0.1:{port}/mcp{Environment.NewLine}");
    }

    [Fact]
    public async Task PortZero_PrintsBoundUrl_AndMcpClientReachesIt()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        var run = StartServe(["--data-root", _dataRoot, "serve", "--port", "0"]);

        var url = await WaitForLineAsync(run, line => line.StartsWith("http://", StringComparison.Ordinal), TestContext.Current.CancellationToken);
        url.ShouldMatch(@"^http://127\.0\.0\.1:\d+/mcp$");

        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add(McpTokenGate.HeaderName, new McpTokenFile(_dataRoot).Read());
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = "serve-runner-test",
                Endpoint = new Uri(url),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Warning)),
            true);
        await using var client = await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
        var toolResult = await client.CallToolAsync("memory_stats",
            new Dictionary<string, object?> { ["projectId"] = "acme" }, null, null, TestContext.Current.CancellationToken);
        toolResult.Content.ToString().ShouldNotBeNullOrEmpty();

        var exit = await StopAsync(run);

        exit.ShouldBe(ExitCode.Success);
        run.Stdout.ToString().ShouldMatch(@"^http://127\.0\.0\.1:\d+/mcp\r?\n$");
    }

    [Fact]
    public async Task BusyPortWithForeignListener_ReturnsPortInUse_WithHintAndNoStackTrace()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var holder = HoldLoopbackPort(out var port); // held OPEN for the whole test
        var run = StartServe(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);

        var exit = await run.Exit;

        exit.ShouldBe(ExitCode.PortInUse);
        run.Stdout.ToString().ShouldBeEmpty();
        run.Stderr.ToString().ShouldContain("in use");
        run.Stderr.ToString().ShouldContain("--port 0");
        run.Stderr.ToString().ShouldNotContain("   at ");
    }

    [Fact]
    public async Task UnusableTokenPath_ReportsMcpTokenUnavailable_WithThePathAndNoStackTrace()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        var port = FreePort();
        // A directory where the token file belongs: unreadable, uncreatable and undeletable.
        var tokenPath = Path.Combine(_dataRoot, McpTokenFile.FileName);
        Directory.CreateDirectory(tokenPath);

        var run = StartServe(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        var exit = await run.Exit;

        exit.ShouldBe(ExitCode.McpTokenUnavailable);
        run.Stdout.ToString().ShouldBeEmpty();
        run.Stderr.ToString().ShouldContain(tokenPath);
        run.Stderr.ToString().ShouldNotContain("   at ");
    }

    [Fact]
    public async Task BusyPortWithAiRaccoonServer_Attaches_AndFirstKeepsOwnership()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        var port = FreePort();
        var first = StartServe(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        var firstUrl = await WaitForLineAsync(first, line => line.StartsWith("http://", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        var secondRoot = TestData.CreateTempRoot("ai-raccoon-serve-attach");
        try
        {
            // Attach + --mcp-entry: the entry for the OWNER's bound port is printed (F7).
            var second = StartServe(["--data-root", secondRoot, "serve", "--port", port.ToString(), "--mcp-entry", "--format", "hermes"]);
            var secondExit = await second.Exit;

            secondExit.ShouldBe(ExitCode.Success);
            second.Stdout.ToString().ShouldBe($"{McpEntryRenderer.RenderHermes(port)}{Environment.NewLine}");
            second.Stderr.ToString().ShouldContain("attached");
            second.Stderr.ToString().ShouldNotContain("   at ");


            // The OWNER's token: /mcp is gated, so an unauthorized GET would 401 before routing.
            var response = await GetMcpAsync(firstUrl, _dataRoot, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed); // GET unmapped; any real response proves ownership
        }
        finally
        {
            Directory.Delete(secondRoot, true);
        }

        var firstExit = await StopAsync(first);
        firstExit.ShouldBe(ExitCode.Success);
    }

    [Fact]
    public async Task ConcurrentStartsOnSamePort_ExactlyOneOwns_TheOtherAttachesOrReturnsPortInUse()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        var port = FreePort();
        var secondRoot = TestData.CreateTempRoot("ai-raccoon-serve-race");
        try
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstTask = StartServeAsync(["--data-root", _dataRoot, "serve", "--port", port.ToString()], gate.Task);
            var secondTask = StartServeAsync(["--data-root", secondRoot, "serve", "--port", port.ToString()], gate.Task);
            gate.SetResult();
            var first = await firstTask;
            var second = await secondTask;

            var exits = await Task.WhenAll(first.Exit, second.Exit);

            exits.ShouldAllBe(exit => exit == ExitCode.Success || exit == ExitCode.PortInUse);
            exits.Count(exit => exit == ExitCode.Success).ShouldBeGreaterThanOrEqualTo(1);
            if (exits[0] == ExitCode.Success)
            {
                first.Stdout.ToString().ShouldMatch(@"^http://127\.0\.0\.1:\d+/mcp\r?\n$");
            }
            else
            {
                first.Stdout.ToString().ShouldBeEmpty();
                first.Stderr.ToString().ShouldContain("in use");
            }

            if (exits[1] == ExitCode.Success)
            {
                second.Stdout.ToString().ShouldMatch(@"^http://127\.0\.0\.1:\d+/mcp\r?\n$");
            }
            else
            {
                second.Stdout.ToString().ShouldBeEmpty();
                second.Stderr.ToString().ShouldContain("in use");
            }

            foreach (var run in new[] { first, second })
            {
                run.Stderr.ToString().ShouldNotContain("   at ");
            }
        }
        finally
        {
            Directory.Delete(secondRoot, true);
        }
    }

    [Fact]
    public async Task McpEntryHermes_PrintsTheEntryJson()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        var port = FreePort();
        var run = StartServe(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--mcp-entry"]);

        var line = await WaitForLineAsync(run, line => line.StartsWith("{", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        line.ShouldBe(McpEntryRenderer.RenderHermes(port));
        var exit = await StopAsync(run);

        exit.ShouldBe(ExitCode.Success);
        run.Stdout.ToString().ShouldBe($"{McpEntryRenderer.RenderHermes(port)}{Environment.NewLine}");
    }

    [Fact]
    public async Task McpEntryClaude_PrintsTheEntryJson()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        var port = FreePort();
        var run = StartServe(
            ["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--mcp-entry", "--format", "claude"]);

        var line = await WaitForLineAsync(run, line => line.StartsWith("{", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        line.ShouldBe(McpEntryRenderer.RenderClaude(port));
        var exit = await StopAsync(run);

        exit.ShouldBe(ExitCode.Success);
    }

    [Fact]
    public async Task RootPort_IsHonored_WhenServePortAbsent()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        var port = FreePort();
        var run = StartServe(["--data-root", _dataRoot, "--port", port.ToString(), "serve"]);

        var url = await WaitForLineAsync(run, line => line.StartsWith("http://", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        url.ShouldBe($"http://127.0.0.1:{port}/mcp");
        var exit = await StopAsync(run);

        exit.ShouldBe(ExitCode.Success);
    }

    [Fact]
    public async Task ServePort_WinsOverRootPort()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        var rootPort = FreePort();
        var servePort = FreePort();
        var run = StartServe(["--data-root", _dataRoot, "--port", rootPort.ToString(), "serve", "--port", servePort.ToString()]);

        var url = await WaitForLineAsync(run, line => line.StartsWith("http://", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        url.ShouldBe($"http://127.0.0.1:{servePort}/mcp");
        var exit = await StopAsync(run);

        exit.ShouldBe(ExitCode.Success);
    }

    [Fact]
    public async Task NonHttpTransport_WarnsOnStderr_AndStillServesHttp()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        var port = FreePort();
        var run = StartServe(["--data-root", _dataRoot, "--transport", "stdio", "serve", "--port", port.ToString()]);

        var url = await WaitForLineAsync(run, line => line.StartsWith("http://", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        url.ShouldBe($"http://127.0.0.1:{port}/mcp");
        var exit = await StopAsync(run);

        exit.ShouldBe(ExitCode.Success);
        run.Stderr.ToString().ShouldContain("serve always uses http");
    }

    [Fact]
    public async Task DefaultTransport_DoesNotWarnThatServeIgnoresIt()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        var port = FreePort();
        // No --transport flag: the default (Proxy, ADR-0020) is not a user choice to ignore.
        var run = StartServe(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);

        var url = await WaitForLineAsync(run, line => line.StartsWith("http://", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        url.ShouldBe($"http://127.0.0.1:{port}/mcp");
        var exit = await StopAsync(run);

        exit.ShouldBe(ExitCode.Success);
        run.Stderr.ToString().ShouldNotContain("ignoring --transport");
        run.Stderr.ToString().ShouldNotContain("serve always uses http");
    }

    [Fact]
    public async Task IdleTimeout_ShutsTheHostDown_AfterTheSpanWithoutActivity()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        var port = FreePort();
        var run = StartServe(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--idle-timeout", "5s"]);

        var url = await WaitForLineAsync(run, line => line.StartsWith("http://", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        url.ShouldBe($"http://127.0.0.1:{port}/mcp");


        var stopwatch = Stopwatch.StartNew();
        var exit = await run.Exit;
        stopwatch.Stop();

        exit.ShouldBe(ExitCode.Success);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task IdleTimeoutZero_KeepsServing_AndNeverSelfShutsDown()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        var port = FreePort();
        var run = StartServe(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--idle-timeout", "0"]);

        var url = await WaitForLineAsync(run, line => line.StartsWith("http://", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);
        url.ShouldBe($"http://127.0.0.1:{port}/mcp");

        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        run.Exit.IsCompleted.ShouldBeFalse();

        var response = await GetMcpAsync(url, _dataRoot, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);

        var exit = await StopAsync(run);
        exit.ShouldBe(ExitCode.Success);
    }

    /// <summary>A /mcp request carrying the token minted under the given data root.</summary>
    private static async Task<HttpResponseMessage> GetMcpAsync(string url, string dataRoot, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(McpTokenGate.HeaderName, new McpTokenFile(dataRoot).Read());
        return await HttpClient.SendAsync(request, cancellationToken);
    }

    private static ServeRun StartServe(string[] args) => StartServeAsync(args).GetAwaiter().GetResult();

    private static async Task<ServeRun> StartServeAsync(string[] args, Task? gate = null)
    {
        CliArgs.TryParse(args, out var parsed);
        parsed!.Errors.ShouldBeEmpty();
        parsed.CommandPath.ShouldBe(["serve"]);
        var config = parsed.Options.ToServerConfig();
        var stdout = new LockingWriter();
        var stderr = new LockingWriter();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        if (gate is not null)
        {
            await gate;
        }

        var exit = TestData.CreateNodeRunner(parsed.ServerConfig.Options).RunAsync(parsed, new StandardStreams(TextReader.Null, stdout, stderr), cts.Token);
        return new ServeRun(exit, stdout, stderr, cts);
    }

    private static async Task<int> StopAsync(ServeRun run)
    {
        await run.Cts.CancelAsync();
        return await run.Exit;
    }

    private static async Task<string> WaitForLineAsync(ServeRun run, Func<string, bool> predicate, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = run.Stdout.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (line is not null && predicate(line.TrimEnd('\r')))
            {
                return line.TrimEnd('\r');
            }

            await Task.Delay(50, cancellationToken);
        }

        throw new TimeoutException($"timed out waiting for serve output; stderr: {run.Stderr}");
    }

    private static async Task<IDisposable> AcquireCleanEnvAsync(CancellationToken cancellationToken)
    {
        // Serialized with the other env-var tests: AIRACCOON_DB_PASSPHRASE is process-global
        // and must be cleared during a run so a dev machine's value cannot poison a fresh-bank test.
        await TestData.EnvVarGate.WaitAsync(cancellationToken);
        var original = Environment.GetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName);
        Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, null);
        return new EnvRestore(original);
    }

    private TcpListener HoldLoopbackPort(out int port)
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

                _ = Task.Run(AcceptClientLoop);
                return holder;

                async Task? AcceptClientLoop()
                {
                    // Accept and close so probes fail fast instead of timing out; the
                    // LISTENER itself stays open for the whole test (no port race).
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

    /// <summary>Thread-safe capture for the runner's stdout/stderr writers.</summary>
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
