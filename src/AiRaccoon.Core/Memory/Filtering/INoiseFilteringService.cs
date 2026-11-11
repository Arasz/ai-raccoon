namespace AiRaccoon.Core.Memory.Filtering;

public interface INoiseFilteringService
{
    Task<NoiseFilterResult> EvaluatePreWriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default);
}
