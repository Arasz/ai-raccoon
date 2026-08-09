using AiRaccoon.Core.Observability;

namespace AiRaccoon.Tests;

/// <summary>No-op IOperationTelemetry for tests that exercise a background service for reasons
/// other than its instrumentation. Tests that assert on it use BackgroundTelemetryProbe instead.</summary>
public sealed class TestTelemetry : IOperationTelemetry, IOperationScope
{
    public static TestTelemetry None { get; } = new();

    public IOperationScope Begin(string operation) => this;

    public void Tag(string key, string value)
    {
    }

    public void NoteWork()
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
