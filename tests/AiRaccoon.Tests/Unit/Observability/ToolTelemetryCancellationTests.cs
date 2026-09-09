using System.Text.Json;
using AiRaccoon.Observability;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     Cancellation is caller-triggered and cheap to emit in bulk (timeout/retry/disconnect
///     storms), so the counter stays on the bounded sentinel — the WP9 threat model, same as
///     refusals — while <c>error.type</c> keeps the CLR type (bounded to ~2 values).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ToolTelemetryCancellationTests
{
    [Fact]
    public async Task CancelledCall_TagsCounterWithSentinel_KeepingExceptionType()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);

        await Should.ThrowAsync<TaskCanceledException>(() =>
            ToolTelemetry.RecordAsync(metrics, "memory_search", Arguments("acme-cancel-probe"),
                _ => ValueTask.FromException<CallToolResult>(new TaskCanceledException()),
                CancellationToken.None).AsTask());

        var recorded = collector.GetMeasurementSnapshot().ShouldHaveSingleItem();
        recorded.Tags["tool"].ShouldBe("memory_search");
        recorded.Tags["result"].ShouldBe("error");
        recorded.Tags["error.type"].ShouldBe("TaskCanceledException");
        recorded.Tags["project_id"].ShouldBe(ToolTelemetry.RefusedProjectId);
        recorded.Tags["project_id"].ShouldNotBe("acme-cancel-probe");
    }

    private static Dictionary<string, JsonElement> Arguments(string projectId) =>
        new() { ["projectId"] = JsonSerializer.SerializeToElement(projectId) };
}
