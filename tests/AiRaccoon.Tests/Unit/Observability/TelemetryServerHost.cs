using System.Net;
using System.Net.Sockets;
using AiRaccoon.Core.Access;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Infrastructure.Sqlite.Encryption.Providers;
using AiRaccoon.Setup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>The real tool-serving host on a loopback port — the only place the filter registration is exercised.</summary>
internal static class TelemetryServerHost
{
    public static IHost Create(string dataRoot, int port) =>
        McpServerSetup.CreateServerHost(
            new ServerConfig(port, McpTransport.Http, TestData.CreateInfrastructureOptions(dataRoot), default));

    public static async Task<McpClient> ConnectAsync(int port, CancellationToken cancellationToken)
    {
        var httpClient = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/") };
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Name = "tool-telemetry-test",
                Endpoint = new Uri($"http://127.0.0.1:{port}/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            NullLoggerFactory.Instance,
            true);
        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    /// <summary>Writes the per-project access mode row the host reads per call.</summary>
    public static async Task SeedAccessModeAsync(string dataRoot, string projectId, string mode,
        CancellationToken cancellationToken)
    {
        var options = TestData.CreateInfrastructureOptions(dataRoot);
        var store = new SqliteMemoryStore(
            new SqliteConnectionFactory(options,
                new EncryptionKeyResolver(new EncryptionSourceSidecar(SqliteConnectionFactory.BankPathFor(options)),
                    [new EnvEncryptionKeyProvider()])),
            TimeProvider.System, new TokenizerChunker(), new EmbeddingService(), NullLogger<SqliteMemoryStore>.Instance);
        await store.SetSettingAsync(AccessModePolicy.ProjectSettingKey(projectId), mode, cancellationToken);
    }

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
