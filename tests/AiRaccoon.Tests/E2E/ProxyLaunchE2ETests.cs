using System.Security.Cryptography;
using System.Text.Json;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Setup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.E2E;

/// <summary>
///     What a bare `ai-raccoon` launch does after ADR-0020: relay to one HTTP backend without
///     resolving a key, opening the bank or loading a model of its own.
/// </summary>
[Trait(TestCategories.Category, TestCategories.E2E)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ProxyLaunchE2ETests : IAsyncLifetime
{
    private readonly string _backendRoot = TestData.CreateTempRoot("proxy-backend");
    private readonly string _proxyRoot = TestData.CreateTempRoot("proxy-broken-bank");
    private IHost _backend = null!;
    private int _port;

    public async ValueTask InitializeAsync()
    {
        _port = AiRaccoonProcess.FreePort();
        _backend = McpServerSetup.CreateServerHost(new ServerConfig(_port, McpTransport.Http,
            new InfrastructureOptions { DataRoot = _backendRoot, Scope = InstallScope.User }));
        await _backend.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
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
    [Fact]
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
    [Fact]
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
        try
        {
            Directory.Delete(root, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup; the OS temp dir is scanned periodically anyway.
        }
    }
}
