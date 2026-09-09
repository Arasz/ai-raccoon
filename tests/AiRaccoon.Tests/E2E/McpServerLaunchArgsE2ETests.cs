using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Observability;
using ModelContextProtocol.Client;
using Shouldly;
using Xunit;
using xRetry.v3;
using AiRaccoon.Tests.TestHelpers;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     Proves the launch-identity flags end-to-end: --install-scope=project (injected via the
///     factory's UseSetting, mirroring the real entry point's arg) makes the server build the
///     bank for the project scope, not via env vars. Plus the removal gate (T8) and the
///     proxy-child full-surface oracle (T9): no in-process server exists anymore.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
[Collection(E2ETestCollection.Name)]
public class McpServerLaunchArgsE2ETests : IAsyncLifetime
{
    /// <summary>Only stops a hang from wedging the run; the assertions are the exit code and stderr.</summary>
    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(60);

    /// <summary>How long a cold spawn gets to name its pid on /observability before teardown gives up.</summary>
    private static readonly TimeSpan PidLookupDeadline = TimeSpan.FromSeconds(10);

    /// <summary>How long the daemon gets to die once killed, before teardown calls it stuck.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private McpClient _client = null!;
    private McpServerFactory _factory = null!;

    public async ValueTask InitializeAsync()
    {
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);
        _factory = new McpServerFactory(InstallScope.Project);
        _client = await _factory.CreateClientAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _factory.DisposeAsync();
    }

    [RetryFact]
    public async Task InstallScope_ProjectFlag_BankLivesUnderDataRootAiRaccoonDir()
    {
        // Any tool call opens the bank; stats is the lightest.
        await _client.CallToolAsync("memory_stats", new Dictionary<string, object?> { ["projectId"] = "acme" },
            null, null, CancellationToken.None);

        File.Exists(Path.Combine(_factory.DataRoot, ".ai-raccoon", "memory.db")).ShouldBeTrue();
        File.Exists(Path.Combine(_factory.DataRoot, "memory.db")).ShouldBeFalse();
    }

    /// <summary>
    ///     T8 — the removal gate: --transport stdio is rejected with the ruled hint (exit 9) and
    ///     starts nothing — no listener on the passed port, no fallback onto the default 7721,
    ///     no bank, no token, nothing on stdout (drained pipes).
    /// </summary>
    [RetryFact]
    public async Task RemovedStdioTransport_IsRejectedWithHint_AndStartsNothing()
    {
        var dataRoot = TestData.CreateTempRoot("removed-stdio");
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        // A live server may own the default port on a dev box (the always-on backend): hold it
        // when it is free so the fallback probe below is deterministic; when it is held, that
        // probe is skipped — the dead-on-parse spawn below cannot have bound it either way.
        using var defaultPort = LoopbackPort.TryOccupy(7721);
        try
        {
            lease.ReleaseForBind();
            var run = await RaccoonProcess.RunAsync(
                ["--transport", "stdio", "--data-root", dataRoot, "--port", port.ToString(CultureInfo.InvariantCulture)],
                HardCap, TestContext.Current.CancellationToken);

            run.ExitCode.ShouldBe(ExitCode.FailedToParseCliArgs);
            run.Stderr.ShouldContain("--transport");
            run.Stderr.ShouldContain("stdio");
            run.Stderr.ShouldContain("proxy");
            run.Stderr.ShouldContain("serve");
            run.Stdout.ShouldBeEmpty();
            (await TestData.CreateServerProbe().RespondsAsync(port, TestContext.Current.CancellationToken))
                .ShouldBeFalse();
            if (defaultPort is not null)
            {
                (await TestData.CreateServerProbe().RespondsAsync(7721, TestContext.Current.CancellationToken))
                    .ShouldBeFalse();
            }

            File.Exists(Path.Combine(dataRoot, "memory.db")).ShouldBeFalse();
            File.Exists(Path.Combine(dataRoot, McpTokenFile.FileName)).ShouldBeFalse();
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    /// <summary>
    ///     T9 — successor of the deleted ExplicitStdio_StillServesTheFullToolSurfaceInProcess
    ///     (P2/ADR-0020): the bare launch is a proxy child on a real stdio pipe that auto-starts
    ///     its backend. The differential oracle is the product's own derived surface
    ///     (<see cref="RegisteredTools" />): every registered tool must answer over the pipe,
    ///     and the backend must have minted its gate token under the same root.
    /// </summary>
    [RetryFact]
    public async Task BareLaunch_ServesTheFullToolSurfaceOverARealPipe()
    {
        var dataRoot = TestData.CreateTempRoot("proxy-full-surface");
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        try
        {
            await using var client = await AiRaccoonProcess.ConnectAsync(
                ["--data-root", dataRoot, "--port", port.ToString(CultureInfo.InvariantCulture)],
                TestContext.Current.CancellationToken);

            var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
            var result = await client.CallToolAsync("memory_stats",
                new Dictionary<string, object?> { ["projectId"] = "acme" },
                cancellationToken: TestContext.Current.CancellationToken);

            tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal)
                .ShouldBe(RegisteredTools.Names());
            result.IsError.ShouldNotBe(true);
            // The auto-started backend opened its own bank under the same root and minted the
            // gate token strictly before it bound — a proxy that never started one serves nothing.
            File.Exists(Path.Combine(dataRoot, "memory.db")).ShouldBeTrue();
            File.Exists(Path.Combine(dataRoot, McpTokenFile.FileName)).ShouldBeTrue();
        }
        finally
        {
            // The proxy never kills the daemon it started (ProxySpawnedBackendE2ETests owns that
            // contract); this test stops it so it cannot hold the bank past teardown.
            await StopSpawnedBackendAsync(port, dataRoot);
            TestData.DeleteTempRoot(dataRoot);
        }
    }

    /// <summary>Kills the daemon the proxy auto-started, via the pid /observability reports.</summary>
    private static async Task StopSpawnedBackendAsync(int port, string dataRoot)
    {
        var pid = await FindBackendPidAsync(port);
        if (pid is null)
        {
            return;
        }

        Process backend;
        try
        {
            backend = Process.GetProcessById(pid.Value);
        }
        catch (ArgumentException)
        {
            return; // retired between the lookup and the kill
        }

        using (backend)
        {
            if (!await RaccoonProcess.KillTreeAndWaitAsync(backend, ExitWait, CancellationToken.None))
            {
                throw new InvalidOperationException(
                    $"the spawned serve (pid {pid}) survived kill, so it still holds port {port} and {dataRoot}");
            }
        }
    }

    private static async Task<int?> FindBackendPidAsync(int port)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        int? pid = null;
        await WaitByPolling.WaitForAsync(async () =>
        {
            try
            {
                var info = await http.GetFromJsonAsync<ServerInfo>(
                    $"http://127.0.0.1:{port}/observability", JsonOptions, CancellationToken.None);
                if (info is { Name: "ai-raccoon" })
                {
                    pid = info.Pid;
                    return true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
            {
                // Not listening yet, or not listening at all.
            }

            return false;
        }, WaitByPolling.DefaultFirstTick, WaitByPolling.DefaultMaxTick, PidLookupDeadline, TimeProvider.System,
            CancellationToken.None);

        return pid;
    }
}
