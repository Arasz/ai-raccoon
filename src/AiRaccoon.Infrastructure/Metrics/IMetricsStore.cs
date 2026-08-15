using AiRaccoon.Core.Metrics;

namespace AiRaccoon.Infrastructure.Metrics;

/// <summary>Persists a batch of measurements to the bank's `metrics` table.</summary>
public interface IMetricsStore
{
    Task SaveBatchAsync(IReadOnlyList<Measurement> measurements, CancellationToken cancellationToken = default);
}
