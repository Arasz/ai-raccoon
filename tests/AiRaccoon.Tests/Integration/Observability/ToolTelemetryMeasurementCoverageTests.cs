using AiRaccoon.Infrastructure.Metrics;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using Shouldly;
using AiRaccoon.Tests.Unit.Observability;
using Xunit;
using AiRaccoon.Tests.TestHelpers;

namespace AiRaccoon.Tests.Integration.Observability;

/// <summary>
///     G1 (spec.json): every tool on the derived tool inventory records at least one measurement
///     through IMeasurementRecorder when called once — the metrics-table pipeline
///     (docs/plans/2026-08-15-performance-metrics-implementation.md, WP8), not the OTLP counter
///     ToolTelemetryCoverageTests already covers.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(ObservabilityCollection.Name)]
public sealed class ToolTelemetryMeasurementCoverageTests
{
    [Fact]
    public async Task EveryToolOnTheDerivedInventory_RecordsAtLeastOneMeasurement()
    {
        await using var envGate = await TestData.HoldEnvGateAsync(TestContext.Current.CancellationToken);
        var dataRoot = TestData.CreateTempRoot("tool-telemetry-measurement-coverage");
        try
        {
            using var lease = LoopbackPort.Reserve();
            var port = lease.Port;
            var host = TelemetryServerHost.Create(dataRoot, port);

            // Derived from RegisteredTools (tests/.../TestHelpers/RegisteredTools.cs) — never
            // hand-kept, so a new tool joins this assertion without anybody editing it
            // (spec.json G1: "the DERIVED tool inventory"; .ai-badger/invariants/derive-or-delete-the-list.md).
            var toolNames = RegisteredTools.Names();
            toolNames.ShouldNotBeEmpty();

            var buffer = host.Services.GetRequiredService<IMeasurementBuffer>();
            var recorded = new HashSet<string>(StringComparer.Ordinal);

            lease.ReleaseForBind();
            await host.StartAsync(TestContext.Current.CancellationToken);
            try
            {
                await using var client = await TelemetryServerHost.ConnectAsync(port, TestContext.Current.CancellationToken);

                // Empty arguments: most calls are refused, but a refused call still must record
                // (WP8 AC3) — the contract is "every call emits", not "every successful call emits".
                foreach (var tool in toolNames)
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
                // Drained BEFORE StopAsync: the flusher's own 30s interval will not have ticked, but
                // its StopAsync override flushes what is left, so a drain after shutdown finds an
                // empty buffer. Draining here reads exactly what the tool-level hook enqueued.
                foreach (var measurement in buffer.DrainAll())
                {
                    recorded.Add(measurement.Name);
                }

                await host.StopAsync(TestContext.Current.CancellationToken);
            }

            var missing = toolNames.Where(name => !recorded.Contains(name)).ToList();

            missing.ShouldBeEmpty(
                $"registered but recorded no measurement: {string.Join(", ", missing)}");
        }
        finally
        {
            TestData.DeleteTempRoot(dataRoot);
        }
    }
}
