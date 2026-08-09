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
    private readonly List<Activity> _spans = [];
    private readonly Lock _gate = new();

    public BackgroundTelemetryProbe(string operation)
    {
        Telemetry = new BackgroundTelemetry();
        _durations = new MetricCollector<double>(Telemetry.Meter, OtlpNames.BackgroundPassDuration);
        _passes = new MetricCollector<long>(Telemetry.Meter, OtlpNames.BackgroundPasses);
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OtlpNames.BackgroundScope,
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

    public void Dispose()
    {
        _listener.Dispose();
        _durations.Dispose();
        _passes.Dispose();
        Telemetry.Dispose();
    }
}
