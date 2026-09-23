using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
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
///     F70 (owner ruling K1, option a — private spawn): the proxy must never hand the data root's
///     loopback token to a listener that merely holds the configured port. It starts its own backend
///     on an ephemeral port and connects only to the URL that child printed; a squatter that binds
///     the port first is never contacted at all.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class BackendSessionsTokenExposureTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("backend-sessions-squatter");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     The gate: a squatter answering /mcp with a JSON-RPC body (what ServerProbe counts as
    ///     "an ai-raccoon server") must receive no request at all — no probe, and above all no
    ///     X-AiRaccoon-Token and no tool payload. Watched red before private spawn: the proxy
    ///     attached and sent the token byte-for-byte.
    /// </summary>
    [RetryFact]
    public async Task OpenAsync_WithASquatterHoldingTheConfiguredPort_DoesNotSendItTheToken()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        // F39: the proxy's auto-launch refuses an empty non-default root before it probes, so the
        // private-spawn gate this test measures must start from a real bank.
        await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(_dataRoot), TestContext.Current.CancellationToken);
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        using var squatter = new Squatter();
        var privatePort = 0;

        await using (var sessions = Subject(squatter.Port))
        {
            try
            {
                await sessions.OpenAsync(null, TestContext.Current.CancellationToken);
            }
            catch (BackendUnavailableException)
            {
                // Red: the session against the squatter cannot complete. The token is already sent
                // by then, which is exactly what the assertions below measure.
            }

            squatter.Requests.ShouldBeEmpty(
                $"the proxy contacted a squatter holding port {squatter.Port}; token headers seen: " +
                $"{string.Join(", ", squatter.TokenHeaderValues)}; request:\n{string.Join("\n---\n", squatter.Requests)}");

            // R6: the empty-request assertion above is only meaningful when a private backend was
            // actually acquired. Without this, a regression that makes the spawn fail would leave
            // the gate green for the wrong reason.
            sessions.Url.ShouldNotBeNullOrWhiteSpace(
                "the private spawn must have produced a backend URL, not merely failed without a token being sent");
            sessions.Url.ShouldNotBe(UrlFor(squatter.Port),
                "the squatter must not be treated as the backend");
            privatePort = new Uri(sessions.Url).Port;
        }

        if (privatePort != 0)
        {
            await RaccoonBackendCleanup.ShutdownIfRunningAsync(_dataRoot, privatePort, CancellationToken.None);
        }
    }

    /// <summary>
    ///     The positive control for the gate: with the explicit opt-in, the proxy still reaches a
    ///     real ai-raccoon server on the configured port and opens a session through its token — so
    ///     "never attach at all" cannot satisfy the threshold test above.
    /// </summary>
    [RetryFact]
    public async Task OpenAsync_WithAttachAgainstARealServer_OpensASessionThroughTheToken()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var server = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        await server.WaitForUrlAsync(TestContext.Current.CancellationToken);

        await using var sessions = Subject(port, attach: true);
        var session = await sessions.OpenAsync(null, TestContext.Current.CancellationToken);

        sessions.Url.ShouldBe(UrlFor(port));
        // The session only exists if the server accepted the token; listing tools proves it is the
        // real backend, not a listener that merely answers JSON-RPC.
        (await session.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldNotBeEmpty();

        (await server.StopAsync()).ShouldBe(ExitCode.Success);
    }

    private BackendSessions Subject(int port, bool attach = false) =>
        new(new BackendLauncher(TestData.CreateServerProbe(), BackendLauncher.DefaultBudget,
                TimeProvider.System, NullLogger<BackendLauncher>.Instance),
            new PlainHttpClientFactory(), NullLoggerFactory.Instance, ServeExecutable,
            new ServerConfig(port, McpTransport.Http, new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User })
            {
                Attach = attach
            });

    private static string UrlFor(int port) => $"http://127.0.0.1:{port}/mcp";

    private static string ServeExecutable =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "AiRaccoon.exe" : "AiRaccoon");

    private sealed class PlainHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}