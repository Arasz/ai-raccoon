using System.Text.Json;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Core.Projects;
using AiRaccoon.Observability;
using AiRaccoon.Tests.Unit.Projects;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using ModelContextProtocol.Protocol;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>The bank uses the current alias map; the counter keeps the caller's spelling.</summary>
[Collection(ProjectIdAliasDefaultCollection.Name)]
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ToolTelemetryBankFoldTests : IDisposable
{
    private const string Loser = "bank-fold-old-slug";
    private const string Winner = "bank-fold-new-slug";

    public ToolTelemetryBankFoldTests()
    {
        ProjectIdAliasMap.ReplaceDefault(new ProjectIdAliasMap(
            [new ProjectIdAliasEntry(Loser, Winner)], [Winner], []));
    }

    public void Dispose()
    {
        ProjectIdAliasMap.ResetDefault();
    }

    [Fact]
    public async Task NoMigrationGate_FoldsBankMeasurementToWinner()
    {
        var recorder = new RecordingRecorder();
        await ToolTelemetry.RecordAsync(new ToolCallMetrics(), "memory_search", Arguments(Loser),
            _ => ValueTask.FromResult(new CallToolResult()), CancellationToken.None, recorder);
        recorder.Recorded.ShouldHaveSingleItem().ProjectId.ShouldBe(Winner);
    }

    [Fact]
    public async Task AliasMapChange_IsObservedOnTheNextCall_CounterKeepsRaw()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);
        var recorder = new RecordingRecorder();
        await ToolTelemetry.RecordAsync(metrics, "memory_search", Arguments(Loser),
            _ => ValueTask.FromResult(new CallToolResult()), CancellationToken.None, recorder);
        ProjectIdAliasMap.ReplaceDefault(new ProjectIdAliasMap(
            [new ProjectIdAliasEntry(Loser, "another-winner")], ["another-winner"], []));
        await ToolTelemetry.RecordAsync(metrics, "memory_search", Arguments(Loser),
            _ => ValueTask.FromResult(new CallToolResult()), CancellationToken.None, recorder);

        recorder.Recorded.Select(row => row.ProjectId).ShouldBe([Winner, "another-winner"]);
        var counters = collector.GetMeasurementSnapshot();
        counters.Count.ShouldBe(2);
        counters.ShouldAllBe(row => (string?)row.Tags["project_id"] == Loser);
    }

    [Theory]
    [InlineData("{0199A1B2-0000-7000-8000-0000000000C1}", "0199a1b2-0000-7000-8000-0000000000c1")]
    [InlineData("unmapped-raw-id", "unmapped-raw-id")]
    public async Task BankMeasurement_CanonicalizesGuidAndPreservesUnknownRawText(string input, string expected)
    {
        var recorder = new RecordingRecorder();
        await ToolTelemetry.RecordAsync(new ToolCallMetrics(), "memory_search", Arguments(input),
            _ => ValueTask.FromResult(new CallToolResult()), CancellationToken.None, recorder);
        recorder.Recorded.ShouldHaveSingleItem().ProjectId.ShouldBe(expected);
    }

    private static Dictionary<string, JsonElement> Arguments(string projectId) =>
        new() { ["projectId"] = JsonSerializer.SerializeToElement(projectId) };

    private sealed class RecordingRecorder : IMeasurementRecorder
    {
        public List<Measurement> Recorded { get; } = [];

        public void Record(Measurement measurement) => Recorded.Add(measurement);
    }
}
