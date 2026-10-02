using AiRaccoon.Hosting.Common;
using AiRaccoon.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using ModelContextProtocol.Client;
using Xunit;

namespace AiRaccoon.Tests.TestHelpers;

/// <summary>
///     A real MCP server over loopback HTTP: host started on a free port, with an optional log
///     provider attached before start, and clients connected through the streamable-HTTP transport.
///     Disposing closes every client, then stops the host.
/// </summary>
public sealed class RealMcpServerFixture : IAsyncDisposable
{
    private readonly List<McpClient> _clients = [];
    private readonly List<HttpClient> _httpClients = [];
    private readonly IHost _host;

    private RealMcpServerFixture(IHost host, int port)
    {
        _host = host;
        Port = port;
    }

    public int Port { get; }

    public Uri BaseUri => new($"http://127.0.0.1:{Port}/");

    /// <summary>Starts a server over <paramref name="dataRoot" />; <paramref name="logs" /> sees everything it logs.</summary>
    public static async Task<RealMcpServerFixture> StartAsync(string dataRoot, CancellationToken cancellationToken,
        FakeLoggerProvider? logs = null)
    {
        var (port, host) = await LoopbackPort.BindWithRetryAsync(async candidate =>
        {
            var started = McpServerSetup.CreateServerHost(
                new ServerConfig(candidate, McpTransport.Http, TestData.CreateInfrastructureOptions(dataRoot)));
            if (logs is not null)
            {
                started.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
            }

            await started.StartAsync(cancellationToken);
            return (candidate, started);
        });
        return new RealMcpServerFixture(host, port);
    }

    /// <summary>Connects a client named <paramref name="name" /> to the server's /mcp endpoint.</summary>
    public async Task<McpClient> ConnectAsync(string name, CancellationToken cancellationToken = default)
    {
        var httpClient = new HttpClient { BaseAddress = BaseUri };
        _httpClients.Add(httpClient);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = name,
                Endpoint = new Uri(BaseUri, "mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            NullLoggerFactory.Instance,
            true);
        var client = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
        _clients.Add(client);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            await client.DisposeAsync();
        }

        foreach (var httpClient in _httpClients)
        {
            httpClient.Dispose();
        }

        await _host.StopAsync(TestContext.Current.CancellationToken);
    }
}
