using System.Diagnostics;
using System.Text.Json;
using AiRaccoon.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     The filter reads the project id off the wire arguments, so the two tools that do not pass a
///     plain one need a projection. Both halves of that table are checked against the registered
///     tools rather than restated here.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(ObservabilityCollection.Name)]
public sealed class ToolTelemetryProjectionTests : IAsyncLifetime
{
    private readonly string _dataRoot = TestData.CreateTempRoot("tool-telemetry-projection");
    private IHost _host = null!;
    private IReadOnlyList<McpServerTool> _tools = null!;

    public ValueTask InitializeAsync()
    {
        _host = TelemetryServerHost.Create(_dataRoot, TelemetryServerHost.FreePort());
        _tools = [.. _host.Services.GetServices<McpServerTool>()];
        _tools.ShouldNotBeEmpty();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Directory.Delete(_dataRoot, true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public void EveryProjectionKey_IsARegisteredToolName()
    {
        var registered = _tools.Select(tool => tool.ProtocolTool.Name).ToHashSet(StringComparer.Ordinal);

        var unknown = ToolTelemetry.Projections.Keys.Where(name => !registered.Contains(name)).ToList();

        unknown.ShouldBeEmpty($"projected but not registered — renamed or removed: {string.Join(", ", unknown)}");
    }

    /// <summary>
    ///     The default projection reads a <c>projectId</c> string argument. A tool that names its
    ///     project differently needs a table entry, and this is what says so.
    /// </summary>
    [Fact]
    public void EveryToolWithoutAProjection_DeclaresAProjectIdArgument()
    {
        var missing = _tools
            .Select(tool => tool.ProtocolTool)
            .Where(tool => !ToolTelemetry.Projections.ContainsKey(tool.Name))
            .Where(tool => !DeclaresProjectId(tool.InputSchema))
            .Select(tool => tool.Name)
            .ToList();

        missing.ShouldBeEmpty(
            $"no projectId argument and no projection entry — the filter would tag these empty: {string.Join(", ", missing)}");
    }

    /// <summary>
    ///     The composite of up to 8 project ids is unbounded cardinality on the counter, so it gets
    ///     the "multi" sentinel while the span keeps the full comma-joined composite. Both of
    ///     memory_share_extract's success exits are exercised, and each records exactly once.
    /// </summary>
    [Fact]
    public async Task ShareExtract_BoundsTheCounter_KeepsTheCompositeOnTheSpan_AndRecordsEachExitOnce()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var port = TelemetryServerHost.FreePort();
        var host = TelemetryServerHost.Create(_dataRoot, port);
        var metrics = host.Services.GetRequiredService<ToolCallMetrics>();
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);

        var started = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OtlpNames.MemoryToolsScope,
            Sample = (ref _) => ActivitySamplingResult.AllData,
            ActivityStarted = started.Add,
            ActivityStopped = _ => { }
        };
        ActivitySource.AddActivityListener(listener);

        await host.StartAsync(cancellationToken);
        try
        {
            await using var client = await TelemetryServerHost.ConnectAsync(port, cancellationToken);
            foreach (var mode in new[] { "propose", "promote" })
            {
                var result = await client.CallToolAsync("memory_share_extract",
                    new Dictionary<string, object?> { ["projectIds"] = new[] { "acme", "widget" }, ["mode"] = mode },
                    cancellationToken: cancellationToken);
                result.IsError.ShouldNotBe(true);
            }
        }
        finally
        {
            await host.StopAsync(cancellationToken);
        }

        var measurements = collector.GetMeasurementSnapshot();
        measurements.Count.ShouldBe(2, "one record per call, whichever exit the tool returns through");
        measurements.ShouldAllBe(m => m.Tags["result"]!.Equals("success"));
        measurements.ShouldAllBe(m => m.Tags["project_id"]!.Equals(ToolTelemetry.MultiProjectId));

        started.Count(a => a.OperationName == "tools/call memory_share_extract").ShouldBe(2);
        started.Where(a => a.OperationName == "tools/call memory_share_extract")
            .ShouldAllBe(a => a.Tags.Any(kv => kv.Key == "project_id" && kv.Value == "acme,widget"));
    }

    /// <summary>The one tool whose project id is optional keeps reporting under "all" when it is omitted.</summary>
    [Fact]
    public void PromotionList_WithoutAProjectId_ProjectsAll()
    {
        ToolTelemetry.ProjectFor("memory_promotion_list", null)
            .ShouldBe(new ToolTelemetry.ToolProject(ToolTelemetry.AllProjectsId, null));

        ToolTelemetry.ProjectFor("memory_promotion_list", ToolCallRecorder.Arguments(("projectId", "acme")))
            .ShouldBe(new ToolTelemetry.ToolProject("acme", null));
    }

    private static bool DeclaresProjectId(JsonElement inputSchema) =>
        inputSchema.ValueKind == JsonValueKind.Object
        && inputSchema.TryGetProperty("properties", out var properties)
        && properties.TryGetProperty("projectId", out _);
}
