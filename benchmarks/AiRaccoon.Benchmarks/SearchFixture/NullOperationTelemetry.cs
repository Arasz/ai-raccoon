using AiRaccoon.Core.Observability;

namespace AiRaccoon.Benchmarks.SearchFixture;

/// <summary>
///     No-op <see cref="IOperationTelemetry" /> for the fixture graph: the benchmark measures
///     allocations, so a telemetry implementation here must never itself allocate or retain
///     anything (mirrors the test project's TestTelemetry.None).
/// </summary>
public sealed class NullOperationTelemetry : IOperationTelemetry, IOperationScope
{
    public static NullOperationTelemetry Instance { get; } = new();

    public IOperationScope Begin(string operation) => this;

    public void Tag(string key, string value)
    {
    }

    public void NoteWork()
    {
    }

    public void RecordRows(long rows)
    {
    }

    public void Succeeded()
    {
    }

    public void Failed(Exception exception)
    {
    }

    public void PartiallyFailed(int failureCount)
    {
    }

    public void Dispose()
    {
    }
}
