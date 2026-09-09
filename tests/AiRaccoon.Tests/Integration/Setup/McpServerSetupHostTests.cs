using System.Reflection;
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
using xRetry.v3;
using IdleWatchdog = AiRaccoon.Hosting.Watchdog.IdleWatchdog;

namespace AiRaccoon.Tests.Integration.Setup;

/// <summary>
///     Sole-host contract (P2/ADR-0020: the web host is the only shape — no stdio plain host,
///     no combined set): the host binds the configured port (never the ASP.NET default 5000),
///     always registers the long-lived background loops, and gates the watchdog on the timeout alone.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class McpServerSetupHostTests : IAsyncLifetime
{
    private readonly string _dataRoot = TestData.CreateTempRoot("mcp-host-tests");

    private IAsyncDisposable? _envGate;

    /// <summary>
    ///     Holds the env gate as a reader: this class opens a bank through the real host, so an
    ///     encryption test's window would make it open a plain bank with a key (docs/adr/0066).
    /// </summary>
    public async ValueTask InitializeAsync() =>
        _envGate = await TestData.HoldEnvGateAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        if (_envGate is not null)
        {
            await _envGate.DisposeAsync();
        }
    }

    /// <summary>
    ///     Inversion of the deleted StdioOnlyHost_HasNoWebServer contract (P2/ADR-0020): no host
    ///     shape without a web server exists anymore, so holding the ASP.NET default port blocks
    ///     nothing — the host still builds a web server and starts on its own port.
    /// </summary>
    [RetryFact]
    public async Task SoleHost_IsAlwaysAWebHost_EvenWithTheDefaultPortHeld()
    {
        using var blocker = LoopbackPort.TryOccupy(5000);

        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http));

        host.ShouldBeOfType<WebApplication>();
        host.Services.GetService(typeof(IServer)).ShouldNotBeNull();
        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     Meta-guard for the deleted matrix: P2 removed the stdio plain host (CreateAppHost) and
    ///     the transports-parameterized overloads. If either comes back, the matrix it reopens
    ///     needs its own contract — fail here, not silently.
    /// </summary>
    [RetryFact]
    public void McpServerSetup_DeclaresNoPlainAppHost_AndNoTransportsOverload()
    {
        var methods = typeof(McpServerSetup).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);

        methods.ShouldNotContain(m => m.Name.Contains("AppHost", StringComparison.Ordinal),
            "the stdio plain host is deleted (P2/ADR-0020); resurrecting it reopens the host matrix");
        methods.Where(m => m.Name.Contains("CreateServerHost", StringComparison.Ordinal))
            .SelectMany(m => m.GetParameters())
            .ShouldNotContain(p => p.ParameterType == typeof(IReadOnlyCollection<McpTransport>),
                "the transports-parameterized overloads are deleted (P2); the sole host takes no transport set");
    }

    [RetryFact]
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

    [RetryFact]
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

    /// <summary>
    ///     The long-lived inversion (P2/ADR-0020): the sole host is always long-lived, so the
    ///     extraction loop is always registered — the deleted StdioOnlyHost_DoesNotRegister test
    ///     asserted the opposite for a host shape that no longer exists.
    /// </summary>
    [RetryFact]
    public void HttpHost_RegistersTheExtractionHostedService()
    {
        using var lease = LoopbackPort.Reserve();
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, lease.Port));

        host.Services.GetServices<IHostedService>()
            .ShouldContain(service => service is ExtractionHostedService);
    }

    /// <summary>
    ///     Same inversion for the sweep loop: always registered on the sole host; the deleted
    ///     StdioOnlyHost_DoesNotRegister twin is its red-first predecessor.
    /// </summary>
    [RetryFact]
    public void HttpHost_RegistersTheSweepHostedService()
    {
        using var lease = LoopbackPort.Reserve();
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, lease.Port));

        host.Services.GetServices<IHostedService>()
            .ShouldContain(service => service is SweepHostedService);
    }

    [RetryFact]
    public void HttpHost_RegistersTheBankMaintenanceHostedService()
    {
        using var lease = LoopbackPort.Reserve();
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, lease.Port));

        host.Services.GetServices<IHostedService>()
            .ShouldContain(service => service is BankMaintenanceHostedService);
    }

    [RetryFact]
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
    ///     a missing .WithTools&lt;WatchTools&gt;() that host tests didn't catch). Renamed off the
    ///     deleted stdio host (P2 red-first rename, never a blind swap): the surface is the sole
    ///     host's now.
    /// </summary>
    [RetryFact]
    public void HttpHost_RegistersWatchTools_OnTheMcpSurface()
    {
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http));

        var options = host.Services.GetRequiredService<IOptions<McpServerOptions>>().Value;
        var toolNames = (options.ToolCollection ?? throw new InvalidOperationException("ToolCollection not configured"))
            .Select(t => t.ProtocolTool.Name).ToList();

        // The whole set, derived — a count plus six samples passes while the other twenty drift.
        toolNames.OrderBy(n => n, StringComparer.Ordinal).ShouldBe(RegisteredTools.Names());
    }

    /// <summary>
    ///     Watchdog gating is timeout-only on the sole host: the old stdio-shape exemption died
    ///     with the plain host (P2/ADR-0020), so a timeout always arms it.
    /// </summary>
    [RetryFact]
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

    [RetryFact]
    public void HttpHost_WithoutIdleTimeout_DoesNotRegisterTheWatchdog()
    {
        using var lease = LoopbackPort.Reserve();
        var host = McpServerSetup.CreateServerHost(Config(McpTransport.Http, lease.Port));

        host.Services.GetServices<IHostedService>()
            .ShouldNotContain(service => service is IdleWatchdog);
        host.Services.GetService<IActivitySignaler>().ShouldBeNull();
    }

    [RetryFact]
    public async Task HttpHost_WithFakeClock_ToolCallResetsTheWatchdog_ThenTheHostShutsDown()
    {
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 8, 6, 12, 0, 0, TimeSpan.Zero));
        var host = McpServerSetup.CreateServerHost(
            Config(McpTransport.Http, port, TimeSpan.FromSeconds(2)), time);
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
                .WaitAsync(TestContext.Current.CancellationToken);
            await host.StopAsync(TestContext.Current.CancellationToken);
            lifetime.ApplicationStopped.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)).ShouldBeTrue();
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    ///     Defense in depth behind the parse rejection (ADR-0104 D1): a removed transport has no
    ///     host path, so a config carrying one must fail here rather than bind an HTTP host for
    ///     it. Throws before any builder, port or bank exists, so the port stays free.
    /// </summary>
    [RetryTheory]
    [InlineData(McpTransport.Stdio)]
    [InlineData(McpTransport.Https)]
    public void CreateWebHost_RemovedTransport_ThrowsArgumentOutOfRange(McpTransport transport)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => McpServerSetup.CreateWebHost(Config(transport)));
    }

    private ServerConfig Config(McpTransport transport, int port = 0, TimeSpan idleTimeout = default) =>
        new(port, transport, new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User }, idleTimeout);
}
