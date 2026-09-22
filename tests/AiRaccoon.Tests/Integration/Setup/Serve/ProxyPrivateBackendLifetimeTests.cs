using System.Globalization;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     Owner ruling 2026-09-22: the proxy keeps the private spawn as its default launch, but it
///     owns the lifetime of the backend it starts — when the proxy shuts down, that private backend
///     stops with it. Before this, every proxy run left its own `serve` behind, running unattended
///     under the 4-hour idle watchdog (measured as one live backend per run). Driven with real
///     spawned `serve` processes, because the defect only exists at the process level. The attach
///     path is the positive control: a shared backend is never the proxy's to stop.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ProxyPrivateBackendLifetimeTests : IDisposable
{
    /// <summary>How long the backend gets to let go of its port once the proxy has shut down.</summary>
    private static readonly TimeSpan StopDeadline = TimeSpan.FromSeconds(30);

    private readonly string _dataRoot = TestData.CreateTempRoot("proxy-private-backend-lifetime");
    private int? _privatePort;

    public void Dispose()
    {
        if (_privatePort is { } port)
        {
            RaccoonBackendCleanup.ShutdownIfRunningAsync(_dataRoot, port, CancellationToken.None).GetAwaiter().GetResult();
        }

        TestData.DeleteTempRoot(_dataRoot);
    }

    /// <summary>
    ///     The gate: once the proxy's sessions are disposed, the private backend it started holds
     ///     its port no longer. Red before the ruling landed: the backend kept answering long after
     ///     the proxy was gone, so a teardown had to kill it by hand.
    /// </summary>
    [RetryFact]
    public async Task Shutdown_StopsThePrivateBackendTheProxyStarted()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        using var lease = LoopbackPort.Reserve();
        var sessions = Subject(lease.Port);
        try
        {
            var session = await sessions.OpenAsync(null, TestContext.Current.CancellationToken);
            (await session.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken))
                .ShouldNotBeEmpty("the gate needs a real, working private backend first");

            var port = new Uri(sessions.Url).Port;
            _privatePort = port;
            port.ShouldNotBe(lease.Port, "the private spawn must bind its own port, not the configured one");
            (await TestData.CreateServerProbe().RespondsAsync(port, TestContext.Current.CancellationToken))
                .ShouldBeTrue("the private backend must be live before shutdown for this gate to mean anything");

            await sessions.DisposeAsync();

            (await PortIsFreeAsync(port, TestContext.Current.CancellationToken)).ShouldBeTrue(
                $"the private backend on port {port} still holds the port after the proxy shut down — " +
                "the proxy must stop the backend it started, not leave it to the idle watchdog");
        }
        finally
        {
            await sessions.DisposeAsync();
        }
    }

    /// <summary>
    ///     The positive control: an attached proxy never stops the shared server — it is other
     ///     clients' backend too. So "stop everything on dispose" cannot satisfy the gate above.
    /// </summary>
    [RetryFact]
    public async Task Shutdown_WithAttach_NeverStopsTheSharedBackend()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var server = ServeHarness.Start(
            ["--data-root", _dataRoot, "serve", "--port", port.ToString(CultureInfo.InvariantCulture)]);
        await server.WaitForUrlAsync(TestContext.Current.CancellationToken);

        var sessions = Subject(port, attach: true);
        try
        {
            var session = await sessions.OpenAsync(null, TestContext.Current.CancellationToken);
            (await session.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken))
                .ShouldNotBeEmpty();

            await sessions.DisposeAsync();

            (await TestData.CreateServerProbe().RespondsAsync(port, TestContext.Current.CancellationToken))
                .ShouldBeTrue("the shared backend must outlive an attached proxy — it serves other clients too");
        }
        finally
        {
            await sessions.DisposeAsync();
        }

        (await server.StopAsync()).ShouldBe(ExitCode.Success);
    }

    /// <summary>True once nothing holds the port; a backend shutting down keeps it a moment longer.</summary>
    private static Task<bool> PortIsFreeAsync(int port, CancellationToken cancellationToken) =>
        WaitByPolling.WaitForAsync(() =>
        {
            using var taken = LoopbackPort.TryOccupy(port);
            return ValueTask.FromResult(taken is not null);
        }, WaitByPolling.DefaultFirstTick, WaitByPolling.DefaultMaxTick, StopDeadline, TimeProvider.System,
            cancellationToken).AsTask();

    private BackendSessions Subject(int port, bool attach = false) =>
        new(new BackendLauncher(TestData.CreateServerProbe(), BackendLauncher.DefaultBudget,
                TimeProvider.System, NullLogger<BackendLauncher>.Instance),
            new PlainHttpClientFactory(), NullLoggerFactory.Instance, ServeExecutable,
            new ServerConfig(port, McpTransport.Http,
                new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User })
            {
                Attach = attach
            });

    private static string ServeExecutable =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "AiRaccoon.exe" : "AiRaccoon");

    private sealed class PlainHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
