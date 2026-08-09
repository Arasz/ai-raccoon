using AiRaccoon.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol;
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
            var port = TelemetryServerHost.FreePort();
            var host = TelemetryServerHost.Create(dataRoot, port);

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
                await using var client = await TelemetryServerHost.ConnectAsync(port, TestContext.Current.CancellationToken);

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

}
