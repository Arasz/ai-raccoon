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

/// <summary>
///     Option-1 write-time fold: once the bank is migrated, the telemetry filter records the
///     bank measurement under the folded winner while the span and the counter keep the raw
///     caller-sent id. Touches the process-wide Default map, so it serializes with the other
///     Default readers and resets both the map and the migration latch afterwards.
///
///     Second question from the same review (pinned telemetry in the winner): answered by keeping
///     the ride-along — <c>FoldTelemetryAsync</c> re-keys metrics to the winner so the loser row
///     can vanish entirely; metrics alone never schedule a fold, so nothing here changes that.
/// </summary>
[Collection(ProjectIdAliasDefaultCollection.Name)]
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ToolTelemetryBankFoldTests : IDisposable
{
    private const string Loser = "bank-fold-old-slug";
    private const string Winner = "bank-fold-new-slug";

    public ToolTelemetryBankFoldTests()
    {
        ToolTelemetry.ResetMigratedLatchForTests();
        ProjectIdAliasMap.ReplaceDefault(new ProjectIdAliasMap(
            [new ProjectIdAliasEntry(Loser, Winner)], [Winner], []));
    }

    public void Dispose()
    {
        ProjectIdAliasMap.ResetDefault();
        ToolTelemetry.ResetMigratedLatchForTests();
    }

    /// <summary>Migrated bank: the bank row folds to the winner; span and counter stay raw.</summary>
    [Fact]
    public async Task MigratedGate_FoldsBankMeasurementToWinner_SpanAndCounterKeepRaw()
    {
        var metrics = new ToolCallMetrics();
        using var collector = new MetricCollector<long>(metrics.Meter, OtlpNames.ToolInvocations);
        var recorder = new RecordingRecorder();

        await ToolTelemetry.RecordAsync(metrics, "memory_search", Arguments(Loser),
            _ => ValueTask.FromResult(new CallToolResult()), CancellationToken.None,
            recorder, migrationGate: new FixedMigrationGate(true));

        recorder.Recorded.ShouldHaveSingleItem().ProjectId.ShouldBe(Winner);
        collector.GetMeasurementSnapshot().ShouldHaveSingleItem().Tags["project_id"].ShouldBe(Loser);
    }

    /// <summary>Pre-migration bank: the gate folds nothing, so the bank row stays raw — exactly as before.</summary>
    [Fact]
    public async Task UnmigratedGate_RecordsBankMeasurementRaw()
    {
        var metrics = new ToolCallMetrics();
        var recorder = new RecordingRecorder();

        await ToolTelemetry.RecordAsync(metrics, "memory_search", Arguments(Loser),
            _ => ValueTask.FromResult(new CallToolResult()), CancellationToken.None,
            recorder, migrationGate: new FixedMigrationGate(false));

        recorder.Recorded.ShouldHaveSingleItem().ProjectId.ShouldBe(Loser);
    }

    /// <summary>Fail-open: an unreadable marker records raw rather than breaking the call.</summary>
    [Fact]
    public async Task ThrowingGate_RecordsBankMeasurementRaw()
    {
        var metrics = new ToolCallMetrics();
        var recorder = new RecordingRecorder();

        var result = await ToolTelemetry.RecordAsync(metrics, "memory_search", Arguments(Loser),
            _ => ValueTask.FromResult(new CallToolResult()), CancellationToken.None,
            recorder, migrationGate: new ThrowingMigrationGate());

        result.ShouldNotBeNull();
        recorder.Recorded.ShouldHaveSingleItem().ProjectId.ShouldBe(Loser);
    }

    /// <summary>The one-way latch: a second call skips the marker query and still folds.</summary>
    [Fact]
    public async Task MigratedLatch_SkipsTheGateOnTheSecondCall()
    {
        var metrics = new ToolCallMetrics();
        var recorder = new RecordingRecorder();
        var gate = new CountingMigrationGate(true);

        await ToolTelemetry.RecordAsync(metrics, "memory_search", Arguments(Loser),
            _ => ValueTask.FromResult(new CallToolResult()), CancellationToken.None,
            recorder, migrationGate: gate);
        await ToolTelemetry.RecordAsync(metrics, "memory_search", Arguments(Loser),
            _ => ValueTask.FromResult(new CallToolResult()), CancellationToken.None,
            recorder, migrationGate: gate);

        gate.Calls.ShouldBe(1);
        recorder.Recorded.Count.ShouldBe(2);
        recorder.Recorded.ShouldAllBe(m => m.ProjectId == Winner);
    }

    private static Dictionary<string, JsonElement> Arguments(string projectId) =>
        new() { ["projectId"] = JsonSerializer.SerializeToElement(projectId) };

    private sealed class FixedMigrationGate(bool migrated) : IProjectIdsMigrationGate
    {
        public Task<bool> IsMigratedAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(migrated);
    }

    private sealed class ThrowingMigrationGate : IProjectIdsMigrationGate
    {
        public Task<bool> IsMigratedAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("marker unreadable");
    }

    private sealed class CountingMigrationGate(bool migrated) : IProjectIdsMigrationGate
    {
        public int Calls { get; private set; }

        public Task<bool> IsMigratedAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(migrated);
        }
    }

    private sealed class RecordingRecorder : IMeasurementRecorder
    {
        public List<Measurement> Recorded { get; } = [];

        public void Record(Measurement measurement) => Recorded.Add(measurement);
    }
}
