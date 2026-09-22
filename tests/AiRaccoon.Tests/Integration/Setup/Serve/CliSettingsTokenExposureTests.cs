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
///     F70/K1's settings-verb leg: every server-routed CLI verb (`settings …`, `model …`,
///     `watch registered`, `noise entries`, `repair`, …) acquires its backend through
///     <see cref="CliSettingsBackend" />, so it must private-spawn exactly like the proxy. Before
///     this gate that path called the legacy attach-or-start acquire and sent the data root's
///     token to whatever held the configured port — measured with `noise entries` in the join
///     review.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class CliSettingsTokenExposureTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("cli-settings-squatter");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     The gate: a squatter holding the configured port must receive nothing at all — not the
    ///     probe, and above all not the token on the store's first request. The store read is the
    ///     shape the join review measured (`GET /noise/summary` with the real token).
    /// </summary>
    [RetryFact]
    public async Task AcquireAsync_WithoutAttach_WithASquatterHoldingTheConfiguredPort_DoesNotSendItTheToken()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        // A real token on disk, exactly as the join-review harness minted one: the legacy path reads
        // it after the probe and sends it to the squatter, which is the measurement this gate makes.
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        using var squatter = new Squatter();
        var config = new ServerConfig(squatter.Port, McpTransport.Http,
            new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User });
        var recording = new RecordingLauncher(RealLauncher());

        var store = await CliSettingsBackend.AcquireAsync(recording, ServeExecutable, config,
            TestContext.Current.CancellationToken);
        // A settings verb always reads something; the token rides the default header on that request.
        await store.GetSettingAsync("sweep.threshold", TestContext.Current.CancellationToken);

        store.ShouldBeOfType<ServerSettingsStore>();
        // The measurement first: the squatter's request log is what proves the token arrived.
        squatter.Requests.ShouldBeEmpty(
            $"the settings path contacted a squatter holding port {squatter.Port}; token headers seen: " +
            $"{string.Join(", ", squatter.TokenHeaderValues)}; request:\n{string.Join("\n---\n", squatter.Requests)}");
        // The positive half: the private spawn really happened. Without it the empty-request
        // assertion above could pass for a spawn that failed before sending anything.
        recording.PrivateUrl.ShouldNotBeNullOrWhiteSpace(
            "the settings path must start its own private backend instead of attaching to the configured port");
        new Uri(recording.PrivateUrl!).Port.ShouldNotBe(squatter.Port);

        await RaccoonBackendCleanup.ShutdownIfRunningAsync(_dataRoot, new Uri(recording.PrivateUrl!).Port,
            CancellationToken.None);
    }

    /// <summary>
    ///     The positive control for the gate: with the explicit opt-in, a settings verb still
    ///     reaches a real ai-raccoon server on the configured port and reads through its token —
    ///     so "never attach at all" cannot satisfy the threshold test above.
    /// </summary>
    [RetryFact]
    public async Task AcquireAsync_WithAttachAgainstARealServer_ReadsThroughTheToken()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var server = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        await server.WaitForUrlAsync(TestContext.Current.CancellationToken);

        var config = new ServerConfig(port, McpTransport.Http,
            new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User })
        {
            Attach = true
        };
        var store = await CliSettingsBackend.AcquireAsync(RealLauncher(), ServeExecutable, config,
            TestContext.Current.CancellationToken);

        // A write-then-read round trip only answers if the server accepted the token.
        await store.SetSettingAsync("sweep.threshold", "0.7", TestContext.Current.CancellationToken);
        (await store.GetSettingAsync("sweep.threshold", TestContext.Current.CancellationToken)).ShouldBe("0.7");

        (await server.StopAsync()).ShouldBe(ExitCode.Success);
    }

    private static BackendLauncher RealLauncher() => new(TestData.CreateServerProbe(),
        BackendLauncher.DefaultBudget, TimeProvider.System, NullLogger<BackendLauncher>.Instance);

    private static string ServeExecutable =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "AiRaccoon.exe" : "AiRaccoon");

    /// <summary>Delegates to the real launcher and keeps the private URL, which the store itself
    /// never exposes and the test needs to shut the spawned backend down afterwards.</summary>
    private sealed class RecordingLauncher(BackendLauncher inner) : IBackendLauncher
    {
        public string? PrivateUrl { get; private set; }

        public async Task<BackendResult> StartPrivateAsync(string fileName, IReadOnlyList<string> arguments,
            CancellationToken ctx)
        {
            var result = await inner.StartPrivateAsync(fileName, arguments, ctx);
            PrivateUrl = result.Url;
            return result;
        }

        public Task<BackendResult> AcquireAsync(int port, string fileName, IReadOnlyList<string> arguments,
            CancellationToken ctx) =>
            inner.AcquireAsync(port, fileName, arguments, ctx);
    }
}
