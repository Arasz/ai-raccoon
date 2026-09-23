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
///     K1a (owner ruling 2026-09-22), re-shaped for attach-or-start: the proxy owns the lifetime of
///     the private <em>fallback</em> child it starts when the configured port cannot be proven, and
///     stops it over the proof-gated /shutdown on dispose. The instance it starts on the configured
///     port — and any proven shared server it attaches to — is never the proxy's to stop; it serves
///     other clients too. Driven with real spawned `serve` processes, because the defect only exists
///     at the process level.
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
    ///     The gate: once the proxy's sessions are disposed, the private fallback it started (because
    ///     a squatter held the configured port) holds its port no longer.
    /// </summary>
    [RetryFact]
    public async Task Shutdown_StopsTheEphemeralFallback_ButNeverTheConfiguredPortInstance()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        // F39: the proxy's auto-launch refuses an empty non-default root before it spawns, so the
        // private-backend lifetime gate must start from a real bank.
        await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(_dataRoot), TestContext.Current.CancellationToken);
        using var squatter = new Squatter();
        var sessions = Subject(squatter.Port);
        try
        {
            var session = await sessions.OpenAsync(null, TestContext.Current.CancellationToken);
            (await session.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken))
                .ShouldNotBeEmpty("the gate needs a real, working private fallback first");

            var port = new Uri(sessions.Url).Port;
            _privatePort = port;
            port.ShouldNotBe(squatter.Port, "the fallback must bind its own port, not the squatted one");
            (await TestData.CreateServerProbe().RespondsAsync(port, TestContext.Current.CancellationToken))
                .ShouldBeTrue("the private backend must be live before shutdown for this gate to mean anything");

            await sessions.DisposeAsync();

            (await PortIsFreeAsync(port, TestContext.Current.CancellationToken)).ShouldBeTrue(
                $"the private fallback on port {port} still holds the port after the proxy shut down — " +
                "the proxy must stop the backend it started, not leave it to the idle watchdog");
        }
        finally
        {
            await sessions.DisposeAsync();
        }
    }

    /// <summary>
    ///     The configured-port instance is shared, not private: the proxy starts it when nothing was
    ///     listening but must leave it running on dispose, exactly as it leaves a proven attached
    ///     server. So "stop everything on dispose" cannot satisfy the gate above.
    /// </summary>
    [RetryFact]
    public async Task Shutdown_AfterStartingTheConfiguredPortInstance_NeverStopsIt()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(_dataRoot), TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();

        var sessions = Subject(port);
        try
        {
            var session = await sessions.OpenAsync(null, TestContext.Current.CancellationToken);
            (await session.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken))
                .ShouldNotBeEmpty();

            await sessions.DisposeAsync();

            (await TestData.CreateServerProbe().RespondsAsync(port, TestContext.Current.CancellationToken))
                .ShouldBeTrue("the configured-port instance is shared — it must outlive the proxy that started it");
        }
        finally
        {
            await sessions.DisposeAsync();
            await RaccoonBackendCleanup.ShutdownIfRunningAsync(_dataRoot, port, CancellationToken.None);
        }
    }

    /// <summary>True once nothing holds the port; a backend shutting down keeps it a moment longer.</summary>
    private static Task<bool> PortIsFreeAsync(int port, CancellationToken cancellationToken) =>
        WaitByPolling.WaitForAsync(() =>
        {
            using var taken = LoopbackPort.TryOccupy(port);
            return ValueTask.FromResult(taken is not null);
        }, WaitByPolling.DefaultFirstTick, WaitByPolling.DefaultMaxTick, StopDeadline, TimeProvider.System,
            cancellationToken).AsTask();

    private BackendSessions Subject(int port)
    {
        var config = new ServerConfig(port, McpTransport.Http,
            new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User });
        return new BackendSessions(
            new BackendLauncher(TestData.CreateServerProbe(), BackendLauncher.DefaultBudget,
                TimeProvider.System, NullLogger<BackendLauncher>.Instance),
            new IdentityProver(config.Options, new HttpClient()),
            TestData.CreateServerProbe(), new PlainHttpClientFactory(), NullLoggerFactory.Instance,
            ServeExecutable, config);
    }

    private static string ServeExecutable =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "AiRaccoon.exe" : "AiRaccoon");

    private sealed class PlainHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
