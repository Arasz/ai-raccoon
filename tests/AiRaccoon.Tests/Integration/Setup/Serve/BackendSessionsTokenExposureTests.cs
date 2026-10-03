using AiRaccoon.Hosting.Common;
using AiRaccoon.Hosting.Node;
using AiRaccoon.Hosting.Proxy;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using xRetry.v3;
using Xunit;

namespace AiRaccoon.Tests.Integration.Setup.Serve;

/// <summary>
///     Attach-or-start with the identity proof (ADR-0106): a proven listener on the configured port
///     is attached to and nothing is spawned; nothing listening starts one on the configured port;
///     an unproven listener gets zero secret bytes — only the existing /mcp probe and the bounded
///     nonce challenge — and the client continues on a private fallback. The dispose-time stop is
///     proof-gated too: a racer on the dead child's port is sent nothing (F3).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class BackendSessionsTokenExposureTests : IDisposable
{
    private static readonly TimeSpan PortFreeDeadline = TimeSpan.FromSeconds(30);

    private readonly string _dataRoot = TestData.CreateTempRoot("backend-sessions-attach-or-start");

    private static string ServeExecutable => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "AiRaccoon.exe" : "AiRaccoon");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     The revert gate: a proven ai-raccoon already on the configured port is attached to, and
    ///     no launcher call happens at all — the throwing launcher is the "spawns nothing" proof.
    ///     Disposing the sessions must leave the shared server serving.
    /// </summary>
    [RetryFact]
    public async Task Acquire_WithAProvenBackend_Attaches_AndSpawnsNothing()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(_dataRoot), TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var server = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        await server.WaitForUrlAsync(TestContext.Current.CancellationToken);

        await using (var sessions = Subject(port, new ThrowingBackendLauncher()))
        {
            var session = await sessions.OpenAsync(null, TestContext.Current.CancellationToken);

            sessions.Url.ShouldBe(UrlFor(port));
            // The session only exists if the server accepted the token; listing tools proves it is
            // the real backend, not a listener that merely answers JSON-RPC.
            (await session.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldNotBeEmpty();
        }

        (await TestData.CreateServerProbe().RespondsAsync(port, TestContext.Current.CancellationToken))
            .ShouldBeTrue("a proven shared server must outlive the client that attached to it");

        (await server.StopAsync()).ShouldBe(ErrorCode.Ok.Success);
    }

    /// <summary>
    ///     Nothing listening: one backend is launched, on the configured port — not a private
    ///     ephemeral one — and the client reaches it through the proof.
    /// </summary>
    [RetryFact]
    public async Task Acquire_WithNoListener_StartsOnTheConfiguredPort()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(_dataRoot), TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();

        try
        {
            await using var sessions = Subject(port, RealLauncher());
            var session = await sessions.OpenAsync(null, TestContext.Current.CancellationToken);

            new Uri(sessions.Url).Port.ShouldBe(port, "the started backend belongs on the configured port");
            (await session.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldNotBeEmpty();
        }
        finally
        {
            await RaccoonBackendCleanup.ShutdownIfRunningAsync(_dataRoot, port, CancellationToken.None);
        }
    }

    /// <summary>
    ///     The re-shaped F70 gate, in the honest D5 wording: a squatter that holds the configured
    ///     port and answers /mcp with a JSON-RPC-shaped body receives zero secret bytes — no token
    ///     header, no tool payload — and only the probe and the nonce challenge. The client
    ///     continues on a private fallback whose URL the squatter never sees.
    /// </summary>
    [RetryFact]
    public async Task Acquire_WithASquatter_FallsBackToPrivate_AndSendsZeroSecretBytes()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(_dataRoot), TestContext.Current.CancellationToken);
        await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken);
        (await new IdentityKeyFile(TestData.CreateInfrastructureOptions(_dataRoot)).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        using var squatter = new Squatter();
        var privatePort = 0;

        await using (var sessions = Subject(squatter.Port, RealLauncher()))
        {
            var session = await sessions.OpenAsync(null, TestContext.Current.CancellationToken);

            sessions.Url.ShouldNotBeNullOrWhiteSpace();
            new Uri(sessions.Url).Port.ShouldNotBe(squatter.Port);
            (await session.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldNotBeEmpty();
            privatePort = new Uri(sessions.Url).Port;

            squatter.TokenHeaderValues.ShouldBeEmpty(
                $"the squatter received the data root's token; requests:\n{string.Join("\n---\n", squatter.Requests)}");
            var requestLines = RequestLines(squatter);
            requestLines.ShouldContain(line => line.StartsWith("POST /identity/prove", StringComparison.Ordinal),
                "the listener must have been challenged before the fallback decision");
            requestLines.ShouldAllBe(line =>
                line.StartsWith("POST /mcp", StringComparison.Ordinal) ||
                line.StartsWith("POST /identity/prove", StringComparison.Ordinal));
        }

        if (privatePort != 0)
        {
            await RaccoonBackendCleanup.ShutdownIfRunningAsync(_dataRoot, privatePort, CancellationToken.None);
        }
    }

    /// <summary>
    ///     F3: the private child dies, a racer takes its port, and the dispose-time stop proves
    ///     first — the racer gets the challenge and nothing else. The challenge assertion keeps the
    ///     gate from passing because the stop path never ran.
    /// </summary>
    [RetryFact]
    public async Task DisposeStop_WithARacerOnTheDeadChildsPort_SendsNoSecretBytes()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(_dataRoot), TestContext.Current.CancellationToken);
        (await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        (await new IdentityKeyFile(TestData.CreateInfrastructureOptions(_dataRoot)).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        using var squatter = new Squatter();

        await using (var sessions = Subject(squatter.Port, RealLauncher()))
        {
            await sessions.OpenAsync(null, TestContext.Current.CancellationToken);
            var privatePort = new Uri(sessions.Url).Port;

            // Kill the child through its own token-guarded stop path and wait until its port is free.
            await RaccoonBackendCleanup.ShutdownIfRunningAsync(_dataRoot, privatePort, CancellationToken.None);
            (await WaitForPortFreeAsync(privatePort, TestContext.Current.CancellationToken))
                .ShouldBeTrue("the private child never let go of its port, so no racer could take it");

            using var racer = new Squatter(privatePort);
            await sessions.DisposeAsync();

            racer.TokenHeaderValues.ShouldBeEmpty(
                $"the racer on the dead child's port received the token; requests:\n{string.Join("\n---\n", racer.Requests)}");
            RequestLines(racer).ShouldNotContain(line => line.StartsWith("POST /shutdown", StringComparison.Ordinal));
            RequestLines(racer).ShouldContain(line => line.StartsWith("POST /identity/prove", StringComparison.Ordinal),
                "the stop path must have attempted the proof against whoever holds the port");
        }
    }

    /// <summary>
    ///     F3 with the window forced instead of waited for: the private child answers the stop-time
    ///     challenge, and before that answer is read it has let go of its port and a racer holds it.
    ///     The proof is good, so only a stop that rides the very connection the proof rode can reach
    ///     the child; any new connection reaches the racer. When the child also closes that
    ///     connection, nothing may be sent at all.
    /// </summary>
    [RetryTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposeStop_WhenTheChildLosesItsPortRightAfterProving_SendsTheRacerNoSecretBytes(bool childClosesTheConnection)
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        await TestData.SeedBankAsync(options, TestContext.Current.CancellationToken);
        var token = await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken);
        token.ShouldNotBeNull();
        var keyFile = new IdentityKeyFile(options);
        using var key = await keyFile.EnsureAsync(TestContext.Current.CancellationToken);
        key.ShouldNotBeNull();
        using var child = new DyingBackend(key, IdentityProof.RootFingerprint(keyFile.StateDirectory));
        using var squatter = new Squatter();

        await using (var sessions = Subject(squatter.Port, new PrivateOnlyLauncher(child.Url)))
        {
            // The child refuses the MCP session; the acquire has proven it and recorded it by then.
            await Should.ThrowAsync<BackendUnavailableException>(() =>
                sessions.OpenAsync(null, TestContext.Current.CancellationToken));

            child.HandOverOn($"POST {IdentityProof.EndpointPath}", childClosesTheConnection);
            await sessions.DisposeAsync();

            var racer = child.Racer.ShouldNotBeNull("the stop path never challenged the child, so the handover never ran");
            racer.TokenHeaderValues.ShouldBeEmpty(
                $"the racer on the child's port received the token; requests:\n{string.Join("\n---\n", racer.Requests)}");
            var shutdowns = child.Requests.Where(head => head.StartsWith($"POST {ShutdownEndpoint.Path}", StringComparison.Ordinal)).ToList();
            if (childClosesTheConnection)
            {
                shutdowns.ShouldBeEmpty("the proven connection was gone, so nothing may carry the stop");
            }
            else
            {
                shutdowns.Count.ShouldBe(1, "the stop rides the connection the proof rode, so the proven child still receives it");
                shutdowns[0].ShouldContain($"{McpTokenGate.HeaderName}: {token}");
            }
        }
    }

    private static IReadOnlyList<string> RequestLines(Squatter squatter) => [.. squatter.Requests.Select(request => request.Split("\r\n")[0])];

    /// <summary>True once nothing holds the port; a backend shutting down keeps it a moment longer.</summary>
    private static Task<bool> WaitForPortFreeAsync(int port, CancellationToken cancellationToken) =>
        WaitByPolling.WaitForAsync(() =>
            {
                using var taken = LoopbackPort.TryOccupy(port);
                return ValueTask.FromResult(taken is not null);
            }, WaitByPolling.DefaultFirstTick, WaitByPolling.DefaultMaxTick, PortFreeDeadline, TimeProvider.System,
            cancellationToken).AsTask();

    private BackendSessions Subject(int port, IBackendLauncher launcher)
    {
        var config = new ServerConfig(port, McpTransport.Http,
            new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User });
        return new BackendSessions(launcher, new IdentityProver(config.Options, new HttpClient()),
            TestData.CreateServerProbe(), new PlainHttpClientFactory(), NullLoggerFactory.Instance,
            ServeExecutable, config, File.Exists, null, null, currentProcessPath: ServeExecutable);
    }

    private static BackendLauncher RealLauncher() =>
        new(TestData.CreateServerProbe(),
            BackendLauncher.DefaultBudget, TimeProvider.System, NullLogger<BackendLauncher>.Instance);

    private static string UrlFor(int port) => $"http://127.0.0.1:{port}/mcp";

    /// <summary>Any launcher call is a gate failure: the proven path must never consult it.</summary>
    private sealed class ThrowingBackendLauncher : IBackendLauncher
    {
        public Task<BackendResult> StartPrivateAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ctx) =>
            throw new InvalidOperationException("a proven listener must be attached to, never spawn anything");

        public Task<BackendResult> AcquireAsync(int port, string fileName, IReadOnlyList<string> arguments, CancellationToken ctx) =>
            throw new InvalidOperationException("a proven listener must be attached to, never start anything");
    }

    /// <summary>Hands out one fixed private backend; the configured port is never started on.</summary>
    private sealed class PrivateOnlyLauncher(string url) : IBackendLauncher
    {
        public Task<BackendResult> StartPrivateAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken ctx) =>
            Task.FromResult(new BackendResult(url, null));

        public Task<BackendResult> AcquireAsync(int port, string fileName, IReadOnlyList<string> arguments, CancellationToken ctx) =>
            throw new InvalidOperationException("the squatted configured port must never be started on");
    }

    private sealed class PlainHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
