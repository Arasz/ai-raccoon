using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AiRaccoon.Hosting.Common;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Shouldly;
using Xunit;
using xRetry.v3;
using AiRaccoon.Tests.TestHelpers;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     What a bare `ai-raccoon` launch does after ADR-0020: relay to one HTTP backend without
///     resolving a key, opening the bank or loading a model of its own.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Nightly)]
[Collection(E2ETestCollection.Name)]
public sealed class ProxyLaunchE2ETests : IAsyncLifetime
{
    /// <summary>Only stops a hang from wedging the run; the assertions are the exit code and stderr.</summary>
    private static readonly TimeSpan HardCap = TimeSpan.FromSeconds(60);

    private readonly string _backendRoot = TestData.CreateTempRoot("proxy-backend");
    private readonly string _proxyRoot = TestData.CreateTempRoot("proxy-broken-bank");
    private IHost _backend = null!;
    private int _port;

    private IAsyncDisposable? _envGate;

    public async ValueTask InitializeAsync()
    {
        // Reader of the env gate (docs/adr/0066): this class stands up the real host.
        _envGate = await TestData.HoldEnvGateAsync(TestContext.Current.CancellationToken);
        using var lease = LoopbackPort.Reserve();
        _port = lease.Port;
        _backend = McpServerSetup.CreateServerHost(new ServerConfig(_port, McpTransport.Http,
            new InfrastructureOptions { DataRoot = _backendRoot, Scope = InstallScope.User }));
        lease.ReleaseForBind();
        await _backend.StartAsync(TestContext.Current.CancellationToken);
        // This fixture's backend is deliberately ungated — the backend is incidental to what these
        // tests measure. The proxy still reads a token, so mint one the way serve would.
        // ProxySpawnedBackendE2ETests is the path that goes through a real gate.
        await new McpTokenFile(_proxyRoot).EnsureAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_envGate is not null)
        {
            await _envGate.DisposeAsync();
            _envGate = null;
        }

        await _backend.StopAsync(CancellationToken.None);
        if (_backend is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }

        Delete(_backendRoot);
        Delete(_proxyRoot);
    }

    /// <summary>
    ///     The proxy's own data root holds a bank no key can open. It still answers, and the file is
    ///     untouched: the key resolve and decrypt probe the in-process server pays are never run.
    /// </summary>
    [RetryFact]
    public async Task BareLaunch_DoesNotOpenTheBank()
    {
        var bank = SqliteConnectionFactory.BankPathFor(
            new InfrastructureOptions { DataRoot = _proxyRoot, Scope = InstallScope.User });
        Directory.CreateDirectory(Path.GetDirectoryName(bank)!);
        var garbage = RandomNumberGenerator.GetBytes(4096);
        await File.WriteAllBytesAsync(bank, garbage, TestContext.Current.CancellationToken);

        await using var client = await AiRaccoonProcess.ConnectAsync(
            ["--data-root", _proxyRoot, "--port", _port.ToString()], TestContext.Current.CancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        tools.ShouldNotBeEmpty();
        (await File.ReadAllBytesAsync(bank, TestContext.Current.CancellationToken)).ShouldBe(garbage);
    }

    /// <summary>The proxy relays the backend's surface and identity, never one of its own.</summary>
    [RetryFact]
    public async Task BareLaunch_RelaysTheBackendSurfaceAndIdentity()
    {
        await using var direct = await ConnectDirectlyAsync();

        await using var client = await AiRaccoonProcess.ConnectAsync(
            ["--data-root", _proxyRoot, "--port", _port.ToString()], TestContext.Current.CancellationToken);
        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        var expected = await direct.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        tools.Select(tool => tool.Name).OrderBy(name => name)
            .ShouldBe(expected.Select(tool => tool.Name).OrderBy(name => name));
        // Protocol 2026-07-28 stamps the LOCAL ServerInfo over the relayed one, so this fails
        // unless the proxy adopts the backend's identity as its own.
        JsonSerializer.Serialize(client.ServerInfo).ShouldBe(JsonSerializer.Serialize(direct.ServerInfo));
    }

    /// <summary>
    ///     No in-process fallback, ever (ADR-0020): a backend that cannot start ends the process with
    ///     one actionable line, because a silent fallback would reinstate the fan-out unobserved.
    /// </summary>
    [RetryFact]
    public async Task WhenTheBackendCannotStart_ItFailsLoudly()
    {
        var root = TestData.CreateTempRoot("proxy-no-backend");
        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        try
        {
            // The spawned `serve` opens this bank and cannot; nothing is listening on the port either.
            var bank = SqliteConnectionFactory.BankPathFor(
                new InfrastructureOptions { DataRoot = root, Scope = InstallScope.User });
            await File.WriteAllBytesAsync(bank, RandomNumberGenerator.GetBytes(4096),
                TestContext.Current.CancellationToken);

            lease.ReleaseForBind();
            var run = await RaccoonProcess.RunAsync(
                ["--data-root", root, "--port", port.ToString(CultureInfo.InvariantCulture)],
                HardCap, TestContext.Current.CancellationToken);

            run.ExitCode.ShouldBe(ExitCode.ProxyBackendUnavailable);
            run.Stderr.ShouldContain($"http://127.0.0.1:{port}/mcp");
            run.Stderr.ShouldContain($"serve exit {ExitCode.FailedToOpenEncryptedBank}");
            run.Stderr.ShouldContain("ai-raccoon --transport stdio");
        }
        finally
        {
            Delete(root);
        }
    }

    private Task<McpClient> ConnectDirectlyAsync() =>
        McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Name = "proxy-launch-direct",
                    Endpoint = new Uri($"http://127.0.0.1:{_port}/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp
                },
                NullLoggerFactory.Instance),
            cancellationToken: TestContext.Current.CancellationToken);

    private static void Delete(string root)
    {
        TestData.DeleteTempRoot(root);
    }
}
