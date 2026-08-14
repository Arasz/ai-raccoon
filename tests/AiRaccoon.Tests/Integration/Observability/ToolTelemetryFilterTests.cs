using AiRaccoon.Observability;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Shouldly;
using AiRaccoon.Tests.Unit.Observability;
using Xunit;
using AiRaccoon.Tests.TestHelpers;

namespace AiRaccoon.Tests.Integration.Observability;

/// <summary>
///     What one refused call records, over the real host. Both properties come from where the
///     telemetry filter sits in the pipeline, so they are pinned rather than left to registration order.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(ObservabilityCollection.Name)]
public sealed class ToolTelemetryFilterTests : IAsyncLifetime
{
    private const string ReadOnlyProject = "acme-ro";

    private readonly string _dataRoot = TestData.CreateTempRoot("tool-telemetry-filter");
    private CollectedMeasurement<long> _refused = null!;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await TelemetryServerHost.SeedAccessModeAsync(_dataRoot, ReadOnlyProject, "ro", cancellationToken);

        using var lease = LoopbackPort.Reserve();
        var port = lease.Port;
        var host = TelemetryServerHost.Create(_dataRoot, port);
        var metrics = host.Services.GetRequiredService<ToolCallMetrics>();
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);

        lease.ReleaseForBind();
        await host.StartAsync(cancellationToken);
        try
        {
            await using var client = await TelemetryServerHost.ConnectAsync(port, cancellationToken);
            var result = await client.CallToolAsync("memory_write",
                new Dictionary<string, object?> { ["projectId"] = ReadOnlyProject, ["content"] = "x" },
                cancellationToken: cancellationToken);

            // The refusal reaches the client as an error result, which is exactly why the filter
            // has to sit inside ToolRefusals to see anything at all.
            result.IsError.ShouldBe(true);
        }
        finally
        {
            await host.StopAsync(cancellationToken);
        }

        _refused = collector.GetMeasurementSnapshot().ShouldHaveSingleItem();
    }

    public ValueTask DisposeAsync()
    {
        TestData.DeleteTempRoot(_dataRoot);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Ordering pin: registered outside ToolRefusals the filter would see a returned error
    ///     result, record result=success and lose the type entirely.
    /// </summary>
    [Fact]
    public void RefusedCall_RecordsTheExceptionType()
    {
        _refused.Tags["tool"].ShouldBe("memory_write");
        _refused.Tags["result"].ShouldBe("error");
        _refused.Tags["error.type"].ShouldBe("AccessDeniedException");
    }

    /// <summary>
    ///     WP9: the counter's project id is bounded once the outcome is known, so a caller who is
    ///     refused cannot mint one series per id it invents.
    /// </summary>
    [Fact]
    public void RefusedCall_TagsTheCounterWithASentinel_NotTheCallersProjectId()
    {
        _refused.Tags["project_id"].ShouldBe(ToolTelemetry.RefusedProjectId);
        _refused.Tags["project_id"].ShouldNotBe(ReadOnlyProject);
    }
}
