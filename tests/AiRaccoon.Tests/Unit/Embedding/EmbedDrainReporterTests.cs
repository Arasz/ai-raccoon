using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     WP-P4-1 (docs/work/2026-08-26-doctor-parity-moe-p4-observability.md): the embed drain's
///     log + metric surface moves out of <see cref="EmbedDrainService" /> into one
///     <see cref="EmbedDrainReporter" /> that BOTH drain paths call. These tests pin the moved
///     surface's ids, levels, templates and series; the category pin lives in
///     EmbedDrainMetricsTests (J5 — FakeLogger&lt;T&gt; stamps its own category, so only a real
///     ILoggerFactory can prove the caller's category survives).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EmbedDrainReporterTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PassStarted_Logs1002AtDebugWithTheCorpusTag()
    {
        var logger = new FakeLogger<EmbedDrainReporterTests>();
        var reporter = NewReporter();

        reporter.PassStarted(logger, EmbedCorpus.Memory);

        var record = logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Id.Id.ShouldBe(1002);
        record.Level.ShouldBe(LogLevel.Debug);
        record.Message.ShouldBe("Embed drain pass started for Memory");
        record.StructuredState!.Single(kv => kv.Key == "Corpus").Value.ShouldBe("Memory");
    }

    [Fact]
    public void PassFinished_Logs1003AndRecordsBothDrainSeriesForTheCorpus()
    {
        var logger = new FakeLogger<EmbedDrainReporterTests>();
        var measurements = new RecordingMeasurementRecorder();
        var reporter = NewReporter(measurements);

        reporter.PassFinished(logger, EmbedCorpus.Code, 5, TimeSpan.FromMilliseconds(7));

        var record = logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Id.Id.ShouldBe(1003);
        record.Level.ShouldBe(LogLevel.Information);
        record.Message.ShouldBe("Embed drain pass finished for Code: 5 row(s)");
        record.StructuredState!.Single(kv => kv.Key == "Corpus").Value.ShouldBe("Code");
        record.StructuredState!.Single(kv => kv.Key == "Rows").Value.ShouldBe("5");

        var rows = measurements.Recorded.Single(m => m.Name == "drain.code.rows");
        rows.Kind.ShouldBe(MeasurementKind.Histogram);
        rows.Value.ShouldBe(5);
        rows.ProjectId.ShouldBe(MetricsConfigKeys.SelfMetricsProjectId);

        var duration = measurements.Recorded.Single(m => m.Name == "drain.code.duration_ms");
        duration.Kind.ShouldBe(MeasurementKind.Histogram);
        duration.Value.ShouldBe(7);
        duration.ProjectId.ShouldBe(MetricsConfigKeys.SelfMetricsProjectId);
    }

    [Fact]
    public void PassFailed_Logs1005AtWarningWithTheCorpusTag()
    {
        var logger = new FakeLogger<EmbedDrainReporterTests>();
        var reporter = NewReporter();

        reporter.PassFailed(logger, EmbedCorpus.Memory, new InvalidOperationException("boom"));

        var record = logger.Collector.GetSnapshot().ShouldHaveSingleItem();
        record.Id.Id.ShouldBe(1005);
        record.Level.ShouldBe(LogLevel.Warning);
        record.Message.ShouldBe("Embed drain pass failed for Memory");
    }

    private static EmbedDrainReporter NewReporter(RecordingMeasurementRecorder? measurements = null) =>
        new(measurements ?? new RecordingMeasurementRecorder(), new FakeTimeProvider(FixedNow));
}
