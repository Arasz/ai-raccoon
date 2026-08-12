using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AiRaccoon.Observability;

public interface IToolCallMetrics : IDisposable
{
    /// <summary>Meter named "AiRaccoon.MemoryTools" — discoverable by dotnet-counters via EventPipe.</summary>
    Meter Meter { get; }
    /// <summary>ActivitySource for OpenTelemetry tracing.</summary>
    ActivitySource ActivitySource { get; }

    /// <summary>Records a tool invocation: increments the counter and records the duration histogram.</summary>
    void RecordInvocation(string tool, string projectId, TimeSpan duration, bool isError, string? errorType = null);
}
