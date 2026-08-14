using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Observability;
using AiRaccoon.Tests.TestHelpers;
using ModelContextProtocol.Client;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     The composition users actually get (ADR-0020): the proxy starts `serve` itself, and that
///     `serve` mints the loopback token and gates /mcp. A pre-started ungated backend cannot show
///     this — it is the one path where the gate is really in the way.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(E2ETestCollection.Name)]
public sealed class ProxySpawnedBackendE2ETests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>How long a cold spawn gets to name its pid on /observability before teardown gives up.</summary>
    private static readonly TimeSpan PidLookupDeadline = TimeSpan.FromSeconds(10);

    /// <summary>How long the daemon gets to die once killed, before teardown calls it stuck.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(30);

    /// <summary>How long a port that named no pid gets to come free before teardown calls it stuck.</summary>
    private static readonly TimeSpan PortReleaseDeadline = TimeSpan.FromSeconds(5);

    private readonly string _dataRoot = TestData.CreateTempRoot("proxy-spawned-backend");
    private LoopbackPort _lease = null!;
    private int _port;

    public ValueTask InitializeAsync()
    {
        _lease = LoopbackPort.Reserve();
        _port = _lease.Port;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     The proxy never kills the daemon it started, so the test does — via the PID
    ///     /observability reports, which stays open by design. A daemon that survives this holds the
    ///     port and the bank for the rest of the run, so teardown fails rather than going quiet.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        // Before the port check below can mean anything, this test's own reservation has to be gone.
        _lease.Dispose();
        await StopSpawnedBackendAsync();
        TestData.DeleteTempRoot(_dataRoot);
    }

    /// <summary>
    ///     A client on the older revision is relayed as faithfully as one on the newest: the proxy
    ///     carries whatever version the client asked for rather than the one its own session opened at.
    /// </summary>
    [Fact]
    public async Task LegacyProtocolClient_IsRelayed()
    {
        _lease.ReleaseForBind();
        await using var client = await AiRaccoonProcess.ConnectAsync(
            ["--data-root", _dataRoot, "--port", _port.ToString()],
            new McpClientOptions { ProtocolVersion = "2025-11-25" }, TestContext.Current.CancellationToken);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        tools.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task ProxyOverASpawnedServe_CallsAToolThroughTheGate()
    {
        // A stock client, tuned for nothing: a cold `serve` may outlast the discover probe and send
        // the SDK down the legacy handshake, which LegacyProtocolClient_IsRelayed proves is carried.
        _lease.ReleaseForBind();
        await using var client = await AiRaccoonProcess.ConnectAsync(
            ["--data-root", _dataRoot, "--port", _port.ToString()], TestContext.Current.CancellationToken);

        var result = await client.CallToolAsync("memory_stats",
            new Dictionary<string, object?> { ["projectId"] = "acme" },
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsError.ShouldNotBe(true);
        // The backend really is a gated `serve`: it minted this file strictly before it bound.
        File.Exists(Path.Combine(_dataRoot, McpTokenFile.FileName)).ShouldBeTrue();
    }

    private async Task StopSpawnedBackendAsync()
    {
        var pid = await FindBackendPidAsync();
        if (pid is null)
        {
            // Silence is only acceptable when the port is genuinely free: taking it proves it.
            if (!await PortIsFreeAsync())
            {
                throw new InvalidOperationException(
                    $"port {_port} is still held but /observability never named a pid, so the spawned " +
                    $"serve could not be killed; it still holds {_dataRoot}");
            }

            return;
        }

        Process backend;
        try
        {
            backend = Process.GetProcessById(pid.Value);
        }
        catch (ArgumentException)
        {
            // Retired between the lookup and the kill.
            return;
        }

        using (backend)
        {
            if (!await RaccoonProcess.KillTreeAndWaitAsync(backend, ExitWait, CancellationToken.None))
            {
                throw new InvalidOperationException(
                    $"the spawned serve (pid {pid}) survived kill, so it still holds port {_port} and {_dataRoot}");
            }
        }
    }

    /// <summary>Retries the lookup: a cold spawn can still be booting when the test body ends.</summary>
    private async Task<int?> FindBackendPidAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        int? pid = null;
        await WaitByPolling.WaitForAsync(async () =>
        {
            try
            {
                var info = await http.GetFromJsonAsync<ServerInfo>(
                    $"http://127.0.0.1:{_port}/observability", JsonOptions, CancellationToken.None);
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

    /// <summary>True once nothing holds the port; a daemon shutting down keeps it a moment longer.</summary>
    private Task<bool> PortIsFreeAsync() =>
        WaitByPolling.WaitForAsync(() =>
        {
            using var taken = LoopbackPort.TryOccupy(_port);
            return ValueTask.FromResult(taken is not null);
        }, WaitByPolling.DefaultFirstTick, WaitByPolling.DefaultMaxTick, PortReleaseDeadline, TimeProvider.System,
            CancellationToken.None).AsTask();
}
