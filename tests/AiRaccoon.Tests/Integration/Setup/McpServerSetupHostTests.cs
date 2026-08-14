using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Watchdog;
using AiRaccoon.Infrastructure.Degradation;
using AiRaccoon.Infrastructure.Extraction;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Setup;
using AiRaccoon.Setup.Extensions;
using AiRaccoon.Tests.TestHelpers;
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
using IdleWatchdog = AiRaccoon.Hosting.Watchdog.IdleWatchdog;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     Host-shape contract: stdio-only launches run with no web server; HTTP/S launches bind
///     the configured port (never the ASP.NET default 5000); a combined stdio+http set keeps
///     the web host with stdio attached.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class McpServerSetupHostTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("mcp-host-tests");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task StdioOnlyHost_HasNoWebServer_AndStartsWithTheDefaultPortHeld()
    {
        using var blocker = LoopbackPort.TryOccupy(5000);

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio));

        host.Services.GetService(typeof(IServer)).ShouldBeNull();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task HttpHost_BindsTheConfiguredPort_NotTheDefault5000()
    {
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, port));

        lease.ReleaseForBind();
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
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http));

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
        using var lease = LoopbackPort.Reserve();
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio, lease.Port), [McpTransport.Stdio, McpTransport.Http], TimeProvider.System);

        host.Services.GetService(typeof(IServer)).ShouldNotBeNull();
        lease.ReleaseForBind();
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
        using var lease = LoopbackPort.Reserve();
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, lease.Port));

        host.Services.GetServices<IHostedService>()
            .ShouldContain(service => service is ExtractionHostedService);
    }

    [Fact]
    public void BothTransportsHost_RegistersTheExtractionHostedService()
    {
        // HTTP/S presence means the process can live long enough for the extraction
        // loop to matter; a pure-stdio process is per-connection and recycled.
        using var lease = LoopbackPort.Reserve();
        var host = McpServerSetup.CreateServerHost(
            Config(McpTransport.Stdio, lease.Port), [McpTransport.Stdio, McpTransport.Http], TimeProvider.System);

        host.Services.GetServices<IHostedService>()
            .ShouldContain(service => service is ExtractionHostedService);
    }

    [Fact]
    public void StdioOnlyHost_DoesNotRegisterTheSweepHostedService()
    {
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio));

        host.Services.GetServices<IHostedService>()
            .ShouldNotContain(service => service is SweepHostedService);
    }

    [Fact]
    public void HttpHost_RegistersTheSweepHostedService()
    {
        using var lease = LoopbackPort.Reserve();
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, lease.Port));

        host.Services.GetServices<IHostedService>()
            .ShouldContain(service => service is SweepHostedService);
    }

    [Fact]
    public void BothTransportsHost_RegistersTheSweepHostedService()
    {
        using var lease = LoopbackPort.Reserve();
        var host = McpServerSetup.CreateServerHost(
            Config(McpTransport.Stdio, lease.Port), [McpTransport.Stdio, McpTransport.Http], TimeProvider.System);

        host.Services.GetServices<IHostedService>()
            .ShouldContain(service => service is SweepHostedService);
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
        using var lease = LoopbackPort.Reserve();
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, lease.Port));

        host.Services.GetServices<IHostedService>()
            .ShouldContain(service => service is BankMaintenanceHostedService);
    }

    [Fact]
    public void BothTransportsHost_RegistersTheBankMaintenanceHostedService()
    {
        using var lease = LoopbackPort.Reserve();
        var host = McpServerSetup.CreateServerHost(
            Config(McpTransport.Stdio, lease.Port), [McpTransport.Stdio, McpTransport.Http], TimeProvider.System);

        host.Services.GetServices<IHostedService>()
            .ShouldContain(service => service is BankMaintenanceHostedService);
    }

    [Fact]
    public async Task RunAsync_HttpHost_StartsAndStopsCleanly()
    {
        using var lease = LoopbackPort.Reserve();
        var config = Config(McpTransport.Http, lease.Port);
        var host = McpServerSetup.CreateServerHost(config);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        lease.ReleaseForBind();
        var runTask = host.RunAsync(config, TestContext.Current.CancellationToken);

        // Wait on the lifetime signal, not on a sleep: a slow machine used to decide this.
        await WaitForAsync(lifetime.ApplicationStarted, TestContext.Current.CancellationToken);
        lifetime.ApplicationStopped.IsCancellationRequested.ShouldBeFalse();

        await host.StopAsync(TestContext.Current.CancellationToken);
        await runTask;

        lifetime.ApplicationStopped.IsCancellationRequested.ShouldBeTrue();
    }

    /// <summary>Completes when the token fires, or throws once the test's own timeout elapses.</summary>
    private static async Task WaitForAsync(CancellationToken signal, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource();
        await using var registration = signal.Register(() => completion.TrySetResult());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        await completion.Task.WaitAsync(linked.Token);
    }

    /// <summary>
    ///     Pins the registered MCP tool count, including the watch trio (previously dropped by
    ///     a missing .WithTools&lt;WatchTools&gt;() that host tests didn't catch).
    /// </summary>
    [Fact]
    public void StdioHost_RegistersWatchTools_OnTheMcpSurface()
    {
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Stdio));

        var options = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var toolNames = (options.ToolCollection ?? throw new InvalidOperationException("ToolCollection not configured"))
            .Select(t => t.ProtocolTool.Name).ToList();

        toolNames.Count.ShouldBe(26);
        toolNames.ShouldContain("memory_share_extract");
        toolNames.ShouldContain("memory_watch_add");
        toolNames.ShouldContain("memory_watch_status");
        toolNames.ShouldContain("memory_watch_remove");
        toolNames.ShouldContain("memory_record_followthrough");
        toolNames.ShouldContain("memory_record_grade");
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
        using var lease = LoopbackPort.Reserve();
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, lease.Port, TimeSpan.FromHours(4)));

        // Three registrations, one instance — the middleware's signaler must be the very
        // instance the hosted service runs, or production signals the wrong object.
        var watchdogs = host.Services.GetServices<IHostedService>().OfType<IdleWatchdog>().ToList();
        watchdogs.Count.ShouldBe(1);
        var signaler = host.Services.GetRequiredService<IActivitySignaler>();
        ReferenceEquals(signaler, watchdogs[0]).ShouldBeTrue();
    }

    [Fact]
    public void HttpHost_WithoutIdleTimeout_DoesNotRegisterTheWatchdog()
    {
        using var lease = LoopbackPort.Reserve();
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, lease.Port));

        host.Services.GetServices<IHostedService>()
            .ShouldNotContain(service => service is IdleWatchdog);
        host.Services.GetService<IActivitySignaler>().ShouldBeNull();
    }

    [Fact]
    public async Task HttpHost_WithFakeClock_ToolCallResetsTheWatchdog_ThenTheHostShutsDown()
    {
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero));
        var host = McpServerSetup.CreateServerHost(
            Config(McpTransport.Http, port, TimeSpan.FromSeconds(2)), [McpTransport.Http], time);
        var lifetime = host.Services.GetRequiredService<IHostApplicationLifetime>();

        lease.ReleaseForBind();
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

    private ServerConfig Config(McpTransport transport, int port = 0, TimeSpan idleTimeout = default) =>
        new(port, transport, new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User }, idleTimeout);
}
