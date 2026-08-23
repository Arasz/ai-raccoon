using System.Diagnostics;
using AiRaccoon.Observability;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     The AiRaccoon.Background adapter's contract: one span and exactly one duration + pass
///     measurement per scope, tagged with the operation and its result.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BackgroundTelemetryTests
{
    private const string Operation = "probe.pass";

    [Fact]
    public void NoteWork_ThenSucceeded_StartsASpanOnTheBackgroundSource_NamedForTheOperation()
    {
        using var probe = new BackgroundTelemetryProbe(Operation);

        using (var scope = probe.Telemetry.Begin(Operation))
        {
            scope.NoteWork();
            scope.Succeeded();
        }

        var span = probe.Spans.ShouldHaveSingleItem();
        span.Source.Name.ShouldBe(OtlpNames.BackgroundScope);
        span.OperationName.ShouldBe(Operation);
        span.Kind.ShouldBe(ActivityKind.Internal);
        span.Status.ShouldBe(ActivityStatusCode.Ok);
    }

    [Fact]
    public void Succeeded_WithoutNoteWork_RecordsMeasurementsButEmitsNoSpan()
    {
        using var probe = new BackgroundTelemetryProbe(Operation);

        using (var scope = probe.Telemetry.Begin(Operation))
        {
            scope.Succeeded();
        }

        probe.Spans.ShouldBeEmpty();
        probe.Durations.ShouldHaveSingleItem().Tags["result"].ShouldBe("success");
        probe.Passes.ShouldHaveSingleItem().Tags["result"].ShouldBe("success");
    }

    [Fact]
    public void Failed_WithoutNoteWork_StillEmitsASpan()
    {
        using var probe = new BackgroundTelemetryProbe(Operation);

        using (var scope = probe.Telemetry.Begin(Operation))
        {
            scope.Failed(new InvalidOperationException("zephyrtwo"));
        }

        probe.Spans.ShouldHaveSingleItem().Status.ShouldBe(ActivityStatusCode.Error);
    }

    [Fact]
    public void Dispose_WithoutAnOutcome_AlsoEmitsASpan_BecauseATruncatedPassIsWorthSeeing()
    {
        using var probe = new BackgroundTelemetryProbe(Operation);

        probe.Telemetry.Begin(Operation).Dispose();

        probe.Spans.ShouldHaveSingleItem().Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Fact]
    public void Tag_BeforeNoteWork_StillLandsOnTheSpanOnceRecorded()
    {
        using var probe = new BackgroundTelemetryProbe(Operation);

        using (var scope = probe.Telemetry.Begin(Operation))
        {
            scope.Tag("registrations", "3");
            scope.NoteWork();
            scope.Succeeded();
        }

        probe.Spans.ShouldHaveSingleItem().Tags.ShouldContain(kv => kv.Key == "registrations" && kv.Value == "3");
    }

    [Fact]
    public void Succeeded_RecordsOneDurationAndOnePass_TaggedSuccess()
    {
        using var probe = new BackgroundTelemetryProbe(Operation);

        using (var scope = probe.Telemetry.Begin(Operation))
        {
            scope.Succeeded();
        }

        var duration = probe.Durations.ShouldHaveSingleItem();
        duration.Value.ShouldBeGreaterThanOrEqualTo(0);
        duration.Tags["operation"].ShouldBe(Operation);
        duration.Tags["result"].ShouldBe("success");

        var pass = probe.Passes.ShouldHaveSingleItem();
        pass.Value.ShouldBe(1);
        pass.Tags["result"].ShouldBe("success");
    }

    [Fact]
    public void Failed_TagsTheSpanAndTheMeasurementsWithTheErrorType()
    {
        using var probe = new BackgroundTelemetryProbe(Operation);

        using (var scope = probe.Telemetry.Begin(Operation))
        {
            scope.Failed(new InvalidOperationException("zephyrone"));
        }

        var span = probe.Spans.ShouldHaveSingleItem();
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.Tags.ShouldContain(kv => kv.Key == "error.type" && kv.Value == nameof(InvalidOperationException));

        probe.Durations.ShouldHaveSingleItem().Tags["result"].ShouldBe("error");
        probe.Passes.ShouldHaveSingleItem().Tags["error.type"].ShouldBe(nameof(InvalidOperationException));
    }

    [Fact]
    public void Dispose_WithoutAnOutcome_RecordsResultUnknown()
    {
        using var probe = new BackgroundTelemetryProbe(Operation);

        probe.Telemetry.Begin(Operation).Dispose();

        probe.Durations.ShouldHaveSingleItem().Tags["result"].ShouldBe("unknown");
    }

    [Fact]
    public void Succeeded_ThenDispose_RecordsExactlyOneMeasurement()
    {
        using var probe = new BackgroundTelemetryProbe(Operation);

        var scope = probe.Telemetry.Begin(Operation);
        scope.Succeeded();
        scope.Succeeded();
        scope.Dispose();

        probe.Durations.Count.ShouldBe(1);
        probe.Passes.Count.ShouldBe(1);
    }

    [Fact]
    public void Tag_LandsOnTheSpan_AndNotOnTheMeasurements()
    {
        using var probe = new BackgroundTelemetryProbe(Operation);

        using (var scope = probe.Telemetry.Begin(Operation))
        {
            scope.Tag("projects", "3");
            scope.NoteWork();
            scope.Succeeded();
        }

        probe.Spans.ShouldHaveSingleItem().Tags.ShouldContain(kv => kv.Key == "projects" && kv.Value == "3");
        probe.Durations.ShouldHaveSingleItem().Tags.ShouldNotContainKey("projects");
    }

    // ---- PartiallyFailed (H8): a pass that ran but where some units of work failed ----

    [Fact]
    public void PartiallyFailed_AlwaysEmitsASpan_EvenWithoutNoteWork()
    {
        using var probe = new BackgroundTelemetryProbe(Operation);

        using (var scope = probe.Telemetry.Begin(Operation))
        {
            scope.PartiallyFailed(2);
        }

        var span = probe.Spans.ShouldHaveSingleItem();
        span.Tags.ShouldContain(kv => kv.Key == "failures" && kv.Value == "2");
    }

    [Fact]
    public void PartiallyFailed_RecordsADistinctResult_NotSuccess()
    {
        using var probe = new BackgroundTelemetryProbe(Operation);

        using (var scope = probe.Telemetry.Begin(Operation))
        {
            scope.PartiallyFailed(1);
        }

        probe.Durations.ShouldHaveSingleItem().Tags["result"].ShouldNotBe("success");
        probe.Passes.ShouldHaveSingleItem().Tags["result"].ShouldNotBe("success");
    }

    [Fact]
    public void PartiallyFailed_ThenDispose_RecordsExactlyOneMeasurement()
    {
        using var probe = new BackgroundTelemetryProbe(Operation);

        var scope = probe.Telemetry.Begin(Operation);
        scope.PartiallyFailed(1);
        scope.Dispose();

        probe.Durations.Count.ShouldBe(1);
        probe.Passes.Count.ShouldBe(1);
    }

    // ---- RecordRows (WP11): a drain pass's row count on its own histogram ----

    [Fact]
    public void RecordRows_ThenSucceeded_RecordsOneRowsMeasurement_TaggedLikeDurationAndPasses()
    {
        using var probe = new BackgroundTelemetryProbe(Operation);

        using (var scope = probe.Telemetry.Begin(Operation))
        {
            scope.RecordRows(42);
            scope.Succeeded();
        }

        var rows = probe.Rows.ShouldHaveSingleItem();
        rows.Value.ShouldBe(42);
        rows.Tags["operation"].ShouldBe(Operation);
        rows.Tags["result"].ShouldBe("success");
    }

    /// <summary>A pass that never calls RecordRows (most background passes have no natural row count) must not fabricate a zero.</summary>
    [Fact]
    public void Succeeded_WithoutRecordRows_RecordsNoRowsMeasurement()
    {
        using var probe = new BackgroundTelemetryProbe(Operation);

        using (var scope = probe.Telemetry.Begin(Operation))
        {
            scope.Succeeded();
        }

        probe.Rows.ShouldBeEmpty();
    }

    /// <summary>
    ///     #548 review, B1: calling RecordRows AFTER the scope's one measurement was already claimed
    ///     used to silently drop the row count (the exact bug that shipped in EmbedDrainService).
    ///     Now it throws, so a future ordering mistake fails loudly instead of just losing data.
    /// </summary>
    [Fact]
    public void RecordRows_AfterSucceeded_ThrowsInsteadOfSilentlyDroppingTheValue()
    {
        using var probe = new BackgroundTelemetryProbe(Operation);
        var scope = probe.Telemetry.Begin(Operation);
        scope.Succeeded();

        Should.Throw<InvalidOperationException>(() => scope.RecordRows(42));
    }

    [Fact]
    public void Probe_IgnoresSpansFromAnotherTelemetryInstance_SharingTheSourceName()
    {
        using var probe = new BackgroundTelemetryProbe(Operation);

        // A different BackgroundTelemetry instance publishes on the SAME source name — the
        // shape of a parallel test collection's spans. The probe must not capture them, or a
        // single-item assertion (e.g. a maintenance pass) races every other background test.
        using var other = new BackgroundTelemetry();
        using (var scope = other.Begin(Operation))
        {
            scope.NoteWork();
            scope.Succeeded();
        }

        probe.Spans.ShouldBeEmpty();
        probe.Durations.ShouldBeEmpty();
    }
}
