using AiRaccoon.Core.Metrics;

namespace AiRaccoon.Benchmarks.SearchFixture;

/// <summary>
///     Discards every measurement. The fixture graph's allocations ARE what the benchmark
///     measures, so an <c>IMeasurementRecorder</c> here must never itself allocate or retain
///     anything — unlike a mocking-framework substitute, which records every call and its
///     arguments for the substitute's lifetime (#548 review, L1).
/// </summary>
public sealed class NoOpMeasurementRecorder : IMeasurementRecorder
{
    public static NoOpMeasurementRecorder Instance { get; } = new();

    public void Record(Measurement measurement)
    {
    }
}
