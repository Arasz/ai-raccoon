using System.Net;
using System.Net.Sockets;
using AiRaccoon.Infrastructure.Extraction;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Cli;
using AiRaccoon.Setup.Serve;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Setup;

/// <summary>
///     Host-shape contract after the MCP setup refactor: stdio-only launches run on a
///     plain app host with no web server, HTTP/S launches run on a web host bound to the
///     configured port (never the ASP.NET default 5000), and a combined stdio+http set
///     keeps the web host with stdio attached.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class McpServerSetupHostTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("mcp-host-tests");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task StdioOnlyHost_HasNoWebServer_AndStartsWithTheDefaultPortHeld()
    {
        using var blocker = TryHoldLoopbackPort(5000);

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio));

        host.Services.GetService(typeof(IServer)).ShouldBeNull();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HttpHost_BindsTheConfiguredPort_NotTheDefault5000()
    {
        var port = FreePort();
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, port));

        await host.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var urls = ((WebApplication)host).Urls;
            urls.ShouldContain(url => url.Contains($":{port}", StringComparison.Ordinal));
            urls.ShouldNotContain(url => url.Contains(":5000", StringComparison.Ordinal));
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task HttpHost_WithPortZero_BindsAnEphemeralPort()
    {
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, 0));

        await host.StartAsync(TestContext.Current.CancellationToken);

        try
        {
            var urls = ((WebApplication)host).Urls;
            urls.ShouldNotBeEmpty();
            urls.ShouldNotContain(url => url.Contains(":5000", StringComparison.Ordinal) || url.Contains(":7721", StringComparison.Ordinal));
        }
        finally
        {
            await host.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task BothTransports_CreateWebHostWithStdio()
    {
        // Free port: 7721 may be held by a live server or a concurrent suite.
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio, FreePort()), [McpTransport.Stdio, McpTransport.Http]);

        host.Services.GetService(typeof(IServer)).ShouldNotBeNull();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void StdioOnlyHost_DoesNotRegisterTheExtractionHostedService()
    {
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio));

        host.Services.GetServices<IHostedService>()
            .ShouldNotContain(service => service is ExtractionHostedService);
    }

    [Fact]
    public void HttpHost_RegistersTheExtractionHostedService()
    {
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort()));

        host.Services.GetServices<IHostedService>()
            .ShouldContain(service => service is ExtractionHostedService);
    }

    [Fact]
    public void BothTransportsHost_RegistersTheExtractionHostedService()
    {
        // HTTP/S presence means the process can live long enough for the extraction
        // loop to matter; a pure-stdio process is per-connection and recycled.
        var host = McpServerSetup.CreateServerHost(
            Config(McpTransport.Stdio, FreePort()), [McpTransport.Stdio, McpTransport.Http]);

        host.Services.GetServices<IHostedService>()
            .ShouldContain(service => service is ExtractionHostedService);
    }

    [Fact]
    public void StdioOnlyHost_RegistersTheBankMaintenanceHostedService()
    {
        // Bank maintenance is registered in ALL modes: a stdio process is session-bound,
        // so its startup + shutdown boundary checkpoints are the only ones it gets.
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio));

        host.Services.GetServices<IHostedService>()
            .ShouldContain(service => service is BankMaintenanceHostedService);
    }

    [Fact]
    public void HttpHost_RegistersTheBankMaintenanceHostedService()
    {
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort()));

        host.Services.GetServices<IHostedService>()
            .ShouldContain(service => service is BankMaintenanceHostedService);
    }

    [Fact]
    public void BothTransportsHost_RegistersTheBankMaintenanceHostedService()
    {
        var host = McpServerSetup.CreateServerHost(
            Config(McpTransport.Stdio, FreePort()), [McpTransport.Stdio, McpTransport.Http]);

        host.Services.GetServices<IHostedService>()
            .ShouldContain(service => service is BankMaintenanceHostedService);
    }

    [Fact]
    public async Task RunAsync_HttpHost_StartsAndStopsCleanly()
    {
        var config = Config(McpTransport.Http, FreePort());
        var host = McpServerSetup.CreateServerHost(config);

        var runTask = host.RunAsync(config, TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        await runTask;
    }

    /// <summary>
    ///     The registered MCP surface is the full 20 tools: 17 memory + 3 watch. Regression
    ///     gate for PR #30 dropping .WithTools&lt;WatchTools&gt;() — host tests previously
    ///     pinned transport shape only, so the watch trio silently vanished from tools/list.
    /// </summary>
    [Fact]
    public void StdioHost_RegistersWatchTools_OnTheMcpSurface()
    {
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio));

        var options = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var toolNames = (options.ToolCollection ?? throw new InvalidOperationException("ToolCollection not configured"))
            .Select(t => t.ProtocolTool.Name).ToList();

        toolNames.Count.ShouldBe(22);
        toolNames.ShouldContain("memory_share_extract");
        toolNames.ShouldContain("memory_watch_add");
        toolNames.ShouldContain("memory_watch_status");
        toolNames.ShouldContain("memory_watch_remove");
    }

    [Fact]
    public void StdioOnlyHost_DoesNotRegisterTheIdleWatchdog()
    {
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio));

        host.Services.GetServices<IHostedService>()
            .ShouldNotContain(service => service is IdleWatchdog);
    }

    [Fact]
    public void StdioOnlyHost_WithIdleTimeout_StillDoesNotRegisterTheIdleWatchdog()
    {
        // Watchdog gating is host shape first, timeout second: a stdio-only host never
        // gets it even when a timeout is configured (it is recycled in minutes anyway).
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio, idleTimeout: TimeSpan.FromHours(4)));

        host.Services.GetServices<IHostedService>()
            .ShouldNotContain(service => service is IdleWatchdog);
    }

    [Fact]
    public void HttpHost_WithIdleTimeout_RegistersWatchdogAndSignaler_AsOneInstance()
    {
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort(), TimeSpan.FromHours(4)));

        // R1: three registrations, one instance — the middleware's signaler must be the
        // very instance the hosted service runs, or production signals the wrong object.
        var watchdogs = host.Services.GetServices<IHostedService>().OfType<IdleWatchdog>().ToList();
        watchdogs.Count.ShouldBe(1);
        var signaler = host.Services.GetRequiredService<IActivitySignaler>();
        ReferenceEquals(signaler, watchdogs[0]).ShouldBeTrue();
    }

    [Fact]
    public void HttpHost_WithoutIdleTimeout_DoesNotRegisterTheWatchdog()
    {
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, FreePort()));

        host.Services.GetServices<IHostedService>()
            .ShouldNotContain(service => service is IdleWatchdog);
        host.Services.GetService<IActivitySignaler>().ShouldBeNull();
    }

    [Fact]
    public async Task HttpHost_WithFakeClock_ToolCallResetsTheWatchdog_ThenTheHostShutsDown()
    {
        var port = FreePort();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero));
        var host = McpServerSetup.CreateServerHost(
            Config(McpTransport.Http, port, TimeSpan.FromSeconds(2)), [McpTransport.Http], time);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await Task.Delay(100, TestContext.Current.CancellationToken); // watchdog timer registers
            time.Advance(TimeSpan.FromSeconds(1)); // past the first tick, before the deadline
            await Task.Delay(100, TestContext.Current.CancellationToken);

            using var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
            var transport = new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = "idle-watchdog-test",
                    Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                httpClient,
                NullLoggerFactory.Instance,
                true);
            await using var client = await McpClient.CreateAsync(transport,
                cancellationToken: TestContext.Current.CancellationToken);
            await client.CallToolAsync("memory_stats", new Dictionary<string, object?> { ["projectId"] = "acme" },
                cancellationToken: TestContext.Current.CancellationToken);

            // The /mcp traffic reset the deadline to t0+1s: the original deadline (t0+2s)
            // is now past but the reset deadline is not — no shutdown yet.
            time.Advance(TimeSpan.FromSeconds(1.5));
            await Task.Delay(100, TestContext.Current.CancellationToken);
            lifetime.ApplicationStopping.IsCancellationRequested.ShouldBeFalse();

            time.Advance(TimeSpan.FromSeconds(1)); // t0+3.5s: 2.5s past the reset
            await Task.Delay(100, TestContext.Current.CancellationToken);
            lifetime.ApplicationStopping.IsCancellationRequested.ShouldBeTrue();

            // The runner's shutdown signal: WaitForShutdownAsync (what ServeRunner
            // awaits) returns, then the runner stops the host and it stops cleanly.
            await host.WaitForShutdownAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
            lifetime.ApplicationStopped.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private ServerConfig Config(McpTransport transport, int port = 7721, TimeSpan idleTimeout = default) =>
        new(port, transport, new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User }, idleTimeout);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static TcpListener? TryHoldLoopbackPort(int port)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        try
        {
            listener.Start();
            return listener;
        }
        catch (SocketException)
        {
            listener.Stop();
            return null;
        }
    }
}
