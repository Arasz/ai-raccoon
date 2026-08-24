namespace AiRaccoon.Core.Memory.Filtering;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Memory;

public sealed class NoiseFilteringService(IEnumerable<INoiseFilterPolicy> policies) : INoiseFilteringService
{
    public async Task<NoiseFilterResult> EvaluatePreWriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
    {
        foreach (var policy in policies)
        {
            var result = await policy.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.IsNoise)
            {
                return result;
            }
        }

        return NoiseFilterResult.Clean;
    }
}
