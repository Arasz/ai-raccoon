namespace AiRaccoon.Core.Memory.Filtering;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;

public sealed class NoiseFilteringService(
    IEnumerable<INoiseFilterPolicy> policies,
    INoiseStore? noiseStore,
    TimeProvider timeProvider) : INoiseFilteringService
{
    public async Task<NoiseFilterResult> EvaluatePreWriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
    {
        foreach (var policy in policies)
        {
            var result = await policy.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
            if (result.IsNoise)
            {
                if (noiseStore is not null)
                {
                    var expiresAt = (int)timeProvider.GetUtcNow().AddDays(14).ToUnixTimeSeconds();
                    await noiseStore.RecordNoiseAsync(request, result.PolicyName ?? "Unknown", expiresAt, cancellationToken).ConfigureAwait(false);
                }
                return result;
            }
        }
        return NoiseFilterResult.Clean;
    }
}
