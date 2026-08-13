using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;

namespace AiRaccoon.Core.Memory.Filtering.Policies;

public sealed class ZeroShotEmbeddingNoisePolicy(
    IContentEmbedder embedder,
    INoiseVectorProvider noiseVectorProvider,
    float distanceThreshold = 0.20f) : INoiseFilterPolicy
{
    public string Name => "ZeroShotSemanticNoiseFilter";

    public async ValueTask<NoiseFilterResult> EvaluateAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var vector = await embedder.EmbedContentAsync(request.Content, cancellationToken).ConfigureAwait(false);
        if (vector is null)
        {
            return NoiseFilterResult.Clean;
        }

        var noiseVectors = await noiseVectorProvider.GetNoiseVectorsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var noiseVec in noiseVectors)
        {
            if (ZeroShotEmbeddingFilter.IsNoise(vector, noiseVec.Vector, distanceThreshold))
            {
                return NoiseFilterResult.Noise(Name);
            }
        }

        return NoiseFilterResult.Clean;
    }
}
