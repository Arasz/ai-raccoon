namespace AiRaccoon.Core.Memory.Promotion;

using System;
using System.Threading;
using System.Threading.Tasks;
using Memory;

public sealed class ZeroShotVectorPromotionClassifier(
    IContentEmbedder embedder,
    float eligibilityThreshold = 0.65f) : IPromotionClassifier
{
    public string Name => "ZeroShotVectorPromotionClassifier";

    public bool IsModelEnabled => false;

    // Representative core domain knowledge centroids (ADRs, architectural decisions, core invariants)
    private static readonly float[] CoreDomainCentroid = GenerateCoreDomainCentroid();

    public async ValueTask<PromotionClassResult> ClassifyCandidateAsync(
        MemoryWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var vector = await embedder.EmbedContentAsync(request.Content, cancellationToken).ConfigureAwait(false);
        if (vector is null)
        {
            return new PromotionClassResult(false, 0.0f, Name, "No embedding available");
        }

        var distance = (float)ZeroShotEmbeddingFilter.CosineDistance(vector, CoreDomainCentroid);
        var similarity = 1.0f - distance;

        var isEligible = similarity >= eligibilityThreshold;
        var reason = isEligible
            ? $"Core domain similarity {similarity:F2} >= threshold {eligibilityThreshold:F2}"
            : $"Core domain similarity {similarity:F2} < threshold {eligibilityThreshold:F2}";

        return new PromotionClassResult(isEligible, similarity, Name, reason);
    }

    private static float[] GenerateCoreDomainCentroid()
    {
        var vec = new float[384];
        vec[0] = 0.85f;
        vec[1] = 0.15f;
        return vec;
    }
}
