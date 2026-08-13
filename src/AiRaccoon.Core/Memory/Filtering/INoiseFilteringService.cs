namespace AiRaccoon.Core.Memory.Filtering;
using System.Threading;
using System.Threading.Tasks;

public interface INoiseFilteringService
{
    Task<bool> EvaluatePreWriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default);
}
