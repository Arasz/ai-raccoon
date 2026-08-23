using System.Diagnostics;
using AiRaccoon.Observability;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace AiRaccoon.Tests.Unit.Observability;

/// <summary>
///     Captures the spans and measurements one background operation emits. Filtering on the
///     operation name keeps parallel tests on the process-wide ActivitySource from clobbering
///     each other, so no test collection has to be serialized.
/// </summary>
internal sealed class BackgroundTelemetryProbe : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly MetricCollector<double> _durations;
    private readonly MetricCollector<long> _passes;
    private readonly MetricCollector<long> _rows;
    private readonly List<Activity> _spans = [];
    private readonly Lock _gate = new();

    public BackgroundTelemetryProbe(string operation)
    {
        Telemetry = new BackgroundTelemetry();
        _durations = new MetricCollector<double>(Telemetry.Meter, OtlpNames.BackgroundPassDuration);
        _passes = new MetricCollector<long>(Telemetry.Meter, OtlpNames.BackgroundPasses);
        _rows = new MetricCollector<long>(Telemetry.Meter, OtlpNames.BackgroundPassRows);
        _listener = new ActivityListener
        {
            // Reference-equality on the probe's own ActivitySource, not the source NAME: every
            // BackgroundTelemetry publishes on the same "AiRaccoon.Background" name, so a
            // name filter would also capture a parallel test collection's spans (two spans where
            // a maintenance pass asserts one).
            ShouldListenTo = source => ReferenceEquals(source, Telemetry.ActivitySource),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName != operation)
                {
                    return;
                }

                lock (_gate)
                {
                    _spans.Add(activity);
                }
            }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public BackgroundTelemetry Telemetry { get; }

    public IReadOnlyList<Activity> Spans
    {
        get
        {
            lock (_gate)
            {
                return [.. _spans];
            }
        }
    }

    public IReadOnlyList<CollectedMeasurement<double>> Durations => _durations.GetMeasurementSnapshot();

    public IReadOnlyList<CollectedMeasurement<long>> Passes => _passes.GetMeasurementSnapshot();

    public IReadOnlyList<CollectedMeasurement<long>> Rows => _rows.GetMeasurementSnapshot();

    public void Dispose()
    {
        _listener.Dispose();
        _durations.Dispose();
        _passes.Dispose();
        _rows.Dispose();
        Telemetry.Dispose();
    }
}
