using System.Security.Cryptography;
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
///     nonce challenge — and the client refuses without starting another backend.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class BackendSessionsTokenExposureTests : IDisposable
{
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
            await using (var sessions = Subject(port, RealLauncher()))
            {
                var session = await sessions.OpenAsync(null, TestContext.Current.CancellationToken);
                new Uri(sessions.Url).Port.ShouldBe(port);
                (await session.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken)).ShouldNotBeEmpty();
            }
            (await TestData.CreateServerProbe().RespondsAsync(port, TestContext.Current.CancellationToken))
                .ShouldBeTrue("the newly started shared backend must outlive proxy disposal");
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
    ///     refuses without starting another backend.
    /// </summary>
    [Fact]
    public async Task Acquire_WithASquatter_RefusesAndSendsZeroSecretBytes()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken, (EnvEncryptionKeyProvider.EnvVarName, null));
        await TestData.SeedBankAsync(TestData.CreateInfrastructureOptions(_dataRoot), TestContext.Current.CancellationToken);
        await new McpTokenFile(_dataRoot).EnsureAsync(TestContext.Current.CancellationToken);
        (await new IdentityKeyFile(TestData.CreateInfrastructureOptions(_dataRoot)).EnsureAsync(TestContext.Current.CancellationToken)).ShouldNotBeNull();
        using var squatter = new Squatter();
        await using var sessions = Subject(squatter.Port, new ThrowingBackendLauncher());
        var error = await Should.ThrowAsync<BackendUnavailableException>(() => sessions.OpenAsync(null, TestContext.Current.CancellationToken));
        error.Code.ShouldBe(ErrorCode.Server.Unproven);
        sessions.Url.ShouldBeEmpty();
        squatter.TokenHeaderValues.ShouldBeEmpty();
        RequestLines(squatter).ShouldContain(line => line.StartsWith("POST /identity/prove", StringComparison.Ordinal));
        RequestLines(squatter).ShouldAllBe(line => line.StartsWith("POST /mcp", StringComparison.Ordinal) || line.StartsWith("POST /identity/prove", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Acquire_RealRunningServerWithChangedTrustAnchor_RefusesWithoutStartingBackend()
    {
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        await TestData.SeedBankAsync(options, TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var server = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        await server.WaitForUrlAsync(TestContext.Current.CancellationToken);
        using var client = new HttpClient();
        var endpoint = ServerProbe.EndpointFor(port);
        (await new IdentityProver(options, client).ProveAsync(endpoint, TestContext.Current.CancellationToken)).ShouldBeNull();
        var keyPath = new IdentityKeyFile(options).Path;
        var original = await File.ReadAllTextAsync(keyPath, TestContext.Current.CancellationToken);
        var launcher = new RecordingLauncher(RealLauncher());
        try
        {
            using var replacement = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await IdentityTestKey.WriteAsync(keyPath, replacement.ExportPkcs8PrivateKeyPem(), TestContext.Current.CancellationToken);
            var prover = new IdentityProver(options, client);
            var failure = await prover.ProveAsync(endpoint, TestContext.Current.CancellationToken);
            failure.ShouldNotBeNull();
            var config = new ServerConfig(port, McpTransport.Http, options);
            var outcome = await BackendSessions.AcquireSharedAsync(TestData.CreateServerProbe(), prover, launcher,
                ServeExecutable, config, NullLogger.Instance, TestContext.Current.CancellationToken);
            launcher.Starts.ShouldBe(0, "failed identity of a running real server must never authorize another backend");
            outcome.Result.Url.ShouldBeNull();
            outcome.ProofFailure.ShouldBe(failure);
        }
        finally
        {
            await IdentityTestKey.WriteAsync(keyPath, original, CancellationToken.None);
            using var restoredClient = new HttpClient();
            (await new IdentityProver(options, restoredClient).ProveAsync(endpoint, TestContext.Current.CancellationToken)).ShouldBeNull();
            (await server.StopAsync()).ShouldBe(ErrorCode.Ok.Success);
        }
    }

    [Fact]
    public async Task Open_RealRunningServerWithChangedTrustAnchor_WritesNoServeMarker()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the recording executable is a POSIX shell wrapper");
        await using var env = await EnvScope.AcquireAsync(TestContext.Current.CancellationToken,
            (EnvEncryptionKeyProvider.EnvVarName, null));
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        await TestData.SeedBankAsync(options, TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        lease.ReleaseForBind();
        await using var server = ServeHarness.Start(["--data-root", _dataRoot, "serve", "--port", port.ToString()]);
        await server.WaitForUrlAsync(TestContext.Current.CancellationToken);
        using var client = new HttpClient();
        var endpoint = ServerProbe.EndpointFor(port);
        (await new IdentityProver(options, client).ProveAsync(endpoint, TestContext.Current.CancellationToken)).ShouldBeNull();
        var keyPath = new IdentityKeyFile(options).Path;
        var original = await File.ReadAllTextAsync(keyPath, TestContext.Current.CancellationToken);
        var marker = Path.Combine(_dataRoot, "serve-marker");
        var wrapper = Path.Combine(_dataRoot, "recording-ai-raccoon");
        await File.WriteAllTextAsync(wrapper, $"#!/bin/sh\nprintf '%s\\n' \"$$ $*\" >> '{marker}'\nexec '{ServeExecutable}' \"$@\"\n", TestContext.Current.CancellationToken);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        try
        {
            using var replacement = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await IdentityTestKey.WriteAsync(keyPath, replacement.ExportPkcs8PrivateKeyPem(), TestContext.Current.CancellationToken);
            var prover = new IdentityProver(options, client);
            var failure = await prover.ProveAsync(endpoint, TestContext.Current.CancellationToken);
            failure.ShouldNotBeNull();
            await using var sessions = Subject(port, RealLauncher(), wrapper);
            BackendUnavailableException? refusal = null;
            try
            {
                await sessions.OpenAsync(null, TestContext.Current.CancellationToken);
            }
            catch (BackendUnavailableException ex)
            {
                refusal = ex;
            }
            File.Exists(marker).ShouldBeFalse("no extra serve invocation may occur after failed proof");
            refusal.ShouldNotBeNull().Code.ShouldBe(ErrorCode.Server.Unproven);
            sessions.Url.ShouldBeEmpty();
        }
        finally
        {
            await IdentityTestKey.WriteAsync(keyPath, original, CancellationToken.None);
            if (File.Exists(marker))
            {
                foreach (var line in await File.ReadAllLinesAsync(marker, CancellationToken.None))
                {
                    if (int.TryParse(line.Split(' ')[0], out var pid))
                    {
                        try
                        {
                            using var child = System.Diagnostics.Process.GetProcessById(pid);
                            child.Kill(entireProcessTree: true);
                            await child.WaitForExitAsync(CancellationToken.None);
                        }
                        catch (ArgumentException) { }
                        catch (InvalidOperationException) { }
                    }
                }
            }
            using var restoredClient = new HttpClient();
            (await new IdentityProver(options, restoredClient).ProveAsync(endpoint, TestContext.Current.CancellationToken)).ShouldBeNull();
            (await server.StopAsync()).ShouldBe(ErrorCode.Ok.Success);
        }
    }

    private sealed class RecordingLauncher(IBackendLauncher inner) : IBackendLauncher
    {
        public int Starts { get; private set; }

        public Task<BackendResult> AcquireAsync(int port, string fileName, IReadOnlyList<string> arguments, CancellationToken ctx)
        {
            Starts++;
            return inner.AcquireAsync(port, fileName, arguments, ctx);
        }
    }

    private static IReadOnlyList<string> RequestLines(Squatter squatter) => [.. squatter.Requests.Select(request => request.Split("\r\n")[0])];

    private BackendSessions Subject(int port, IBackendLauncher launcher, string? executable = null)
    {
        var config = new ServerConfig(port, McpTransport.Http,
            new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User });
        return new BackendSessions(launcher, new IdentityProver(config.Options, new HttpClient()),
            TestData.CreateServerProbe(), new PlainHttpClientFactory(), NullLoggerFactory.Instance,
            executable ?? ServeExecutable, config, File.Exists, null, null, currentProcessPath: ServeExecutable);
    }

    private static BackendLauncher RealLauncher() =>
        new(TestData.CreateServerProbe(),
            BackendLauncher.DefaultBudget, TimeProvider.System, NullLogger<BackendLauncher>.Instance);

    private static string UrlFor(int port) => $"http://127.0.0.1:{port}/mcp";

    /// <summary>Any launcher call is a gate failure: the proven path must never consult it.</summary>
    private sealed class ThrowingBackendLauncher : IBackendLauncher
    {


        public Task<BackendResult> AcquireAsync(int port, string fileName, IReadOnlyList<string> arguments, CancellationToken ctx) =>
            throw new InvalidOperationException("a proven listener must be attached to, never start anything");
    }

    private sealed class PlainHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
