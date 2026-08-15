using AiRaccoon.Core.Metrics;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Metrics;

/// <summary>The universal measurement row shape every recorder writes (WP0's port; WP3 writes it).</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MeasurementTests
{
    [Fact]
    public void Constructor_WithRequiredFields_AppliesNullDefaults()
    {
        var recordedAt = DateTimeOffset.UtcNow;

        var measurement = new Measurement("search.fts.duration_ms", MeasurementKind.Histogram, 12.5, "ms", recordedAt);

        measurement.Name.ShouldBe("search.fts.duration_ms");
        measurement.Kind.ShouldBe(MeasurementKind.Histogram);
        measurement.Value.ShouldBe(12.5);
        measurement.Unit.ShouldBe("ms");
        measurement.RecordedAt.ShouldBe(recordedAt);
        measurement.ProjectId.ShouldBeNull();
        measurement.QueryHash.ShouldBeNull();
        measurement.CorrelationId.ShouldBeNull();
        measurement.Tags.ShouldBeNull();
    }

    [Fact]
    public void Constructor_WithAllFields_KeepsThem()
    {
        var recordedAt = DateTimeOffset.UtcNow;

        var measurement = new Measurement(
            "search.fts.duration_ms", MeasurementKind.Histogram, 12.5, "ms", recordedAt,
            ProjectId: "acme", QueryHash: "qh-1", CorrelationId: "corr-1", Tags: """{"phase":"fts"}""");

        measurement.ProjectId.ShouldBe("acme");
        measurement.QueryHash.ShouldBe("qh-1");
        measurement.CorrelationId.ShouldBe("corr-1");
        measurement.Tags.ShouldBe("""{"phase":"fts"}""");
    }
}
