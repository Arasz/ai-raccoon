using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Settings;
using AiRaccoon.Setup;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     Owner ruling 2026-09-22 ("no own backend - attach - the same rules as usual - CLI - only
///     proxy - if no server is running - we start it") reverted every server-routed CLI verb
///     (`settings …`, `model …`, `watch registered`, `noise entries`, `repair`, …) off the F70/K1
///     private spawn this path briefly carried, back to the legacy attach-or-start acquire on
///     <c>--port</c> — the same shape every settings command used before F70. That means whatever
///     already answers the configured port is trusted with the data root's token, exactly like every
///     other attach-shaped decision in this codebase (`serve --attach`, the proxy's own
///     <c>--attach</c>): this is a documented, ruled acceptance of F70's exposure on this one path,
///     not the private-spawn gate F70/K1 built for the proxy and (until this ruling) mirrored here.
///     See ADR-0105.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CliSettingsTokenExposureTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("cli-settings-squatter");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     Documents the accepted exposure (owner ruling 2026-09-22): a listener that merely holds
    ///     the configured port and answers /mcp with a JSON-RPC-shaped body — what <c>ServerProbe</c>
    ///     accepts as "an ai-raccoon server" — receives the settings request and the data root's
    ///     token, because the shared acquire trusts whatever answers the port by design. This is not
    ///     a defect: it is the same trust decision `serve --attach` and the proxy's `--attach` make,
    ///     now the CLI settings transport's only shape, with no opt-in flag to gate it behind.
    /// </summary>
    [RetryFact]
    public async Task AcquireAsync_AgainstAListenerHoldingTheConfiguredPort_SendsItTheToken_AsRuledAcceptable()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        // A real token on disk, exactly as a live data root would hold one: the shared acquire reads
        // it after the probe and sends it to whatever answered, which is the measurement this test
        // records as the accepted (not refused) behaviour.
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        using var squatter = new Squatter();
        var config = new ServerConfig(squatter.Port, McpTransport.Http,
            new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User });

        var store = await CliSettingsBackend.AcquireAsync(RealLauncher(), ServeExecutable, config,
            NullLogger.Instance, TestContext.Current.CancellationToken);
        // A settings verb always reads something; the token rides the default header on that request.
        await store.GetSettingAsync("sweep.threshold", TestContext.Current.CancellationToken);

        store.ShouldBeOfType<ServerSettingsStore>();
        squatter.Requests.ShouldNotBeEmpty(
            "the shared acquire must reach whatever answers the configured port — that is the ruled " +
            "trade-off of the attach-or-start shape, not a regression");
        squatter.TokenHeaderValues.ShouldNotBeEmpty(
            "the data root's token must ride the request to the configured port, as it does for every " +
            "other attach-shaped acquire in this codebase");
    }

    /// <summary>
    ///     The positive control: the same acquire, with no flag at all, still reaches a real
    ///     ai-raccoon server on the configured port and reads through its token — the accepted-
    ///     exposure test above cannot be satisfied by an acquire that simply never reaches anything.
    /// </summary>
    [RetryFact]
    public async Task AcquireAsync_AgainstARealServer_ReadsThroughTheToken_WithNoAttachFlagNeeded()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var server = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        await server.WaitForUrlAsync(TestContext.Current.CancellationToken);

        var config = new ServerConfig(port, McpTransport.Http,
            new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User });
        var store = await CliSettingsBackend.AcquireAsync(RealLauncher(), ServeExecutable, config,
            NullLogger.Instance, TestContext.Current.CancellationToken);

        // A write-then-read round trip only answers if the server accepted the token.
        await store.SetSettingAsync("sweep.threshold", "0.7", TestContext.Current.CancellationToken);
        (await store.GetSettingAsync("sweep.threshold", TestContext.Current.CancellationToken)).ShouldBe("0.7");

        (await server.StopAsync()).ShouldBe(ExitCode.Success);
    }

    private static BackendLauncher RealLauncher() => new(TestData.CreateServerProbe(),
        BackendLauncher.DefaultBudget, TimeProvider.System, NullLogger<BackendLauncher>.Instance);

    private static string ServeExecutable =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "AiRaccoon.exe" : "AiRaccoon");
}
