using System.Net;
using System.Net.Sockets;
using AiRaccoon.Observability;
using AiRaccoon.Setup;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     Instrumentation coverage is a property of the server, not of each tool author's discipline:
///     every tool the real host registers must record an invocation when it is called.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(ObservabilityCollection.Name)]
public sealed class ToolTelemetryCoverageTests
{
    [Fact]
    public async Task EveryRegisteredTool_RecordsAnInvocation()
    {
        var dataRoot = TestData.CreateTempRoot("tool-telemetry-coverage");
        try
        {
            var port = FreePort();
            var host = McpServerSetup.CreateServerHost(
                new ServerConfig(port, McpTransport.Http, TestData.CreateInfrastructureOptions(dataRoot), default));

            // The list is derived from the container, never hand-kept: a tool added to any
            // WithTools<> class joins this assertion without anybody editing it.
            var registered = host.Services.GetServices<McpServerTool>()
                .Select(tool => tool.ProtocolTool.Name)
                .ToList();
            registered.ShouldNotBeEmpty();

            var metrics = host.Services.GetRequiredService<ToolCallMetrics>();
            using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);

            await host.StartAsync(TestContext.Current.CancellationToken);
            try
            {
                await using var client = await ConnectAsync(port);

                // Empty arguments: the call is refused, and a refused call must be counted too —
                // the contract is "every call emits", not "every successful call emits".
                foreach (var tool in registered)
                {
                    try
                    {
                        await client.CallToolAsync(tool, new Dictionary<string, object?>(),
                            cancellationToken: TestContext.Current.CancellationToken);
                    }
                    catch (McpException)
                    {
                        // A genuine fault comes back as a JSON-RPC error; it still had to be counted.
                    }
                }
            }
            finally
            {
                await host.StopAsync(TestContext.Current.CancellationToken);
            }

            var counted = collector.GetMeasurementSnapshot()
                .Select(measurement => measurement.Tags["tool"]?.ToString())
                .ToHashSet(StringComparer.Ordinal);
            var uninstrumented = registered.Where(tool => !counted.Contains(tool)).ToList();

            uninstrumented.ShouldBeEmpty(
                $"registered but recorded no invocation: {string.Join(", ", uninstrumented)}");
        }
        finally
        {
            Directory.Delete(dataRoot, true);
        }
    }

    private static async Task<McpClient> ConnectAsync(int port)
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
        return await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
