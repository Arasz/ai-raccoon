using System.Net;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup.Cli.Render;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     ServeRunner acceptance: stdout URL reporting, foreign-listener PortInUse, idempotent
///     attach to an existing ai-raccoon server, bind-race recovery, and the --mcp-entry /
///     port-fallback / transport-warning surfaces.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class NodeRunnerTests : IDisposable
{
    private static readonly HttpClient HttpClient = new();
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-serve-runner");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task FreePort_PrintsExactUrlLine_AndExitsZeroAfterStop()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var run = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);

        var url = await run.WaitForUrlAsync(TestContext.Current.CancellationToken);
        url.ShouldBe($"http://127.0.0.1:{port}/mcp");
        var exit = await run.StopAsync();

        exit.ShouldBe(ExitCode.Success);
        run.Stdout.ShouldBe($"http://127.0.0.1:{port}/mcp{Environment.NewLine}");
    }

    [Fact]
    public async Task PortZero_PrintsBoundUrl_AndMcpClientReachesIt()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        await using var run = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", "0"]);

        var url = await run.WaitForUrlAsync(TestContext.Current.CancellationToken);
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
        // Content is IList<ContentBlock>: ToString() is the type name, so it can never be empty.
        toolResult.IsError.ShouldNotBe(true);
        string.Concat(toolResult.Content.OfType<TextContentBlock>().Select(block => block.Text))
            .ShouldContain("\"entries\"");

        var exit = await run.StopAsync();

        exit.ShouldBe(ExitCode.Success);
        run.Stdout.ShouldMatch(@"^http://127\.0\.0\.1:\d+/mcp\r?\n$");
    }

    [Fact]
    public async Task BusyPortWithForeignListener_ReturnsPortInUse_WithHintAndNoStackTrace()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var holder = LoopbackPort.Occupy(); // held OPEN for the whole test
        await using var run = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", holder.Port.ToString()]);

        var exit = await run.Exit;

        exit.ShouldBe(ExitCode.PortInUse);
        run.Stdout.ShouldBeEmpty();
        run.Stderr.ShouldContain("in use");
        run.Stderr.ShouldContain("--port 0");
        run.Stderr.ShouldNotContain("   at ");
    }

    [Fact]
    public async Task UnusableTokenPath_ReportsMcpTokenUnavailable_WithThePathAndNoStackTrace()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        // A directory where the token file belongs: unreadable, uncreatable and undeletable.
        var tokenPath = Path.Combine(_dataRoot, McpTokenFile.FileName);
        Directory.CreateDirectory(tokenPath);

        lease.ReleaseForBind();
        await using var run = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        var exit = await run.Exit;

        exit.ShouldBe(ExitCode.McpTokenUnavailable);
        run.Stdout.ShouldBeEmpty();
        run.Stderr.ShouldContain(tokenPath);
        run.Stderr.ShouldNotContain("   at ");
    }

    [Fact]
    public async Task BusyPortWithAiRaccoonServer_Attaches_AndFirstKeepsOwnership()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var first = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        var firstUrl = await first.WaitForUrlAsync(TestContext.Current.CancellationToken);

        var secondRoot = TestData.CreateTempRoot("ai-raccoon-serve-attach");
        try
        {
            // Attach + --mcp-entry: the entry for the OWNER's bound port is printed (F7).
            await using var second = ServeHarness.Start(["--data-root", secondRoot, "serve", "--port", port.ToString(), "--mcp-entry", "--format", "hermes"]);
            var secondExit = await second.Exit;

            secondExit.ShouldBe(ExitCode.Success);
            second.Stdout.ShouldBe($"{McpEntryRenderer.RenderHermes(port)}{Environment.NewLine}");
            second.Stderr.ShouldContain("attached");
            second.Stderr.ShouldNotContain("   at ");
            // UX-F10: attaching silently to another server's bank must not go unremarked --
            // name the bank this invocation asked for so a --data-root mismatch is visible.
            second.Stderr.ShouldContain(Path.Combine(secondRoot, "memory.db"));


            // The OWNER's token: /mcp is gated, so an unauthorized GET would 401 before routing.
            var response = await GetMcpAsync(firstUrl, _dataRoot, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed); // GET unmapped; any real response proves ownership
        }
        finally
        {
            TestData.DeleteTempRoot(secondRoot);
        }

        var firstExit = await first.StopAsync();
        firstExit.ShouldBe(ExitCode.Success);
    }

    [Fact]
    public async Task ConcurrentStartsOnSamePort_ExactlyOneOwns_TheOtherAttachesOrReturnsPortInUse()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var secondRoot = TestData.CreateTempRoot("ai-raccoon-serve-race");
        try
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstTask = ServeHarness.StartAsync(["--data-root", _dataRoot, "serve", "--port", port.ToString()], gate: gate.Task);
            var secondTask = ServeHarness.StartAsync(["--data-root", secondRoot, "serve", "--port", port.ToString()], gate: gate.Task);
            // Both runners bind only once the gate opens: hold the number until then.
            lease.ReleaseForBind();
            gate.SetResult();
            await using var first = await firstTask;
            await using var second = await secondTask;

            var exits = await Task.WhenAll(first.Exit, second.Exit);

            exits.ShouldAllBe(exit => exit == ExitCode.Success || exit == ExitCode.PortInUse);
            exits.Count(exit => exit == ExitCode.Success).ShouldBeGreaterThanOrEqualTo(1);
            if (exits[0] == ExitCode.Success)
            {
                first.Stdout.ShouldMatch(@"^http://127\.0\.0\.1:\d+/mcp\r?\n$");
            }
            else
            {
                first.Stdout.ShouldBeEmpty();
                first.Stderr.ShouldContain("in use");
            }

            if (exits[1] == ExitCode.Success)
            {
                second.Stdout.ShouldMatch(@"^http://127\.0\.0\.1:\d+/mcp\r?\n$");
            }
            else
            {
                second.Stdout.ShouldBeEmpty();
                second.Stderr.ShouldContain("in use");
            }

            foreach (var run in new[] { first, second })
            {
                run.Stderr.ShouldNotContain("   at ");
            }
        }
        finally
        {
            TestData.DeleteTempRoot(secondRoot);
        }
    }

    [Fact]
    public async Task McpEntryHermes_PrintsTheEntryJson()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var run = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--mcp-entry"]);

        var line = await run.WaitForLineAsync(candidate => candidate.StartsWith("{", StringComparison.Ordinal),
            TestContext.Current.CancellationToken, "the MCP entry JSON");
        line.ShouldBe(McpEntryRenderer.RenderHermes(port));
        var exit = await run.StopAsync();

        exit.ShouldBe(ExitCode.Success);
        run.Stdout.ShouldBe($"{McpEntryRenderer.RenderHermes(port)}{Environment.NewLine}");
    }

    [Fact]
    public async Task McpEntryClaude_PrintsTheEntryJson()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var run = ServeHarness.Start(
            ["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--mcp-entry", "--format", "claude"]);

        var line = await run.WaitForLineAsync(candidate => candidate.StartsWith("{", StringComparison.Ordinal),
            TestContext.Current.CancellationToken, "the MCP entry JSON");
        line.ShouldBe(McpEntryRenderer.RenderClaude(port));
        var exit = await run.StopAsync();

        exit.ShouldBe(ExitCode.Success);
    }

    [Fact]
    public async Task RootPort_IsHonored_WhenServePortAbsent()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var run = ServeHarness.Start(["--data-root", _dataRoot, "--port", port.ToString(), "serve"]);

        var url = await run.WaitForUrlAsync(TestContext.Current.CancellationToken);
        url.ShouldBe($"http://127.0.0.1:{port}/mcp");
        var exit = await run.StopAsync();

        exit.ShouldBe(ExitCode.Success);
    }

    [Fact]
    public async Task ServePort_WinsOverRootPort()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var rootLease = LoopbackPort.Reserve();
        using var serveLease = LoopbackPort.Reserve();
        var rootPort = rootLease.Port;
        var servePort = serveLease.Port;
        rootLease.ReleaseForBind();
        serveLease.ReleaseForBind();
        await using var run = ServeHarness.Start(["--data-root", _dataRoot, "--port", rootPort.ToString(), "serve", "--port", servePort.ToString()]);

        var url = await run.WaitForUrlAsync(TestContext.Current.CancellationToken);
        url.ShouldBe($"http://127.0.0.1:{servePort}/mcp");
        var exit = await run.StopAsync();

        exit.ShouldBe(ExitCode.Success);
    }

    [Fact]
    public async Task NonHttpTransport_WarnsOnStderr_AndStillServesHttp()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var run = ServeHarness.Start(["--data-root", _dataRoot, "--transport", "stdio", "serve", "--port", port.ToString()]);

        var url = await run.WaitForUrlAsync(TestContext.Current.CancellationToken);
        url.ShouldBe($"http://127.0.0.1:{port}/mcp");
        var exit = await run.StopAsync();

        exit.ShouldBe(ExitCode.Success);
        run.Stderr.ShouldContain("serve always uses http");
    }

    [Fact]
    public async Task DefaultTransport_DoesNotWarnThatServeIgnoresIt()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        // No --transport flag: the default (Proxy, ADR-0020) is not a user choice to ignore.
        lease.ReleaseForBind();
        await using var run = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);

        var url = await run.WaitForUrlAsync(TestContext.Current.CancellationToken);
        url.ShouldBe($"http://127.0.0.1:{port}/mcp");
        var exit = await run.StopAsync();

        exit.ShouldBe(ExitCode.Success);
        run.Stderr.ShouldNotContain("ignoring --transport");
        run.Stderr.ShouldNotContain("serve always uses http");
    }

    [Fact]
    public async Task IdleTimeout_ShutsTheHostDown_AfterTheSpanWithoutActivity()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var run = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--idle-timeout", "5s"]);

        var url = await run.WaitForUrlAsync(TestContext.Current.CancellationToken);
        url.ShouldBe($"http://127.0.0.1:{port}/mcp");


        var exit = await run.Exit;

        // The self-exit is the fact; its duration is not a verdict (PR #464). A fully broken idle
        // timeout hangs the await above and is caught by the harness cap / job timeout.
        exit.ShouldBe(ExitCode.Success);
    }

    [Fact]
    public async Task IdleTimeoutZero_KeepsServing_AndNeverSelfShutsDown()
    {
        using var env = await AcquireCleanEnvAsync(TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var run = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString(), "--idle-timeout", "0"]);

        var url = await run.WaitForUrlAsync(TestContext.Current.CancellationToken);
        url.ShouldBe($"http://127.0.0.1:{port}/mcp");

        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        run.Exit.IsCompleted.ShouldBeFalse();

        var response = await GetMcpAsync(url, _dataRoot, TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.MethodNotAllowed);

        var exit = await run.StopAsync();
        exit.ShouldBe(ExitCode.Success);
    }

    /// <summary>A /mcp request carrying the token minted under the given data root.</summary>
    private static async Task<HttpResponseMessage> GetMcpAsync(string url, string dataRoot, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add(McpTokenGate.HeaderName, new McpTokenFile(dataRoot).Read());
        return await HttpClient.SendAsync(request, cancellationToken);
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


    private sealed class EnvRestore(string? original) : IDisposable
    {
        public void Dispose()
        {
            Environment.SetEnvironmentVariable(EnvEncryptionKeyProvider.EnvVarName, original);
            TestData.EnvVarGate.Release();
        }
    }
}
