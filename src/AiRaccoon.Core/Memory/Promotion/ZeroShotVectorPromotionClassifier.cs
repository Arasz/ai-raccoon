namespace AiRaccoon.Core.Memory.Promotion;

using System;
using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;

public sealed class ZeroShotVectorPromotionClassifier(
    IContentEmbedder embedder,
    float eligibilityThreshold = 0.65f) : IPromotionClassifier
{
    public string Name => "ZeroShotVectorPromotionClassifier";

    public bool IsModelEnabled => false;

    /// <summary>Canonical core-domain reference — the centroid is its real embedding, not a synthetic vector.</summary>
    private const string CoreDomainReference =
        "Architecture decision record documenting a durable technical decision: the chosen approach, the rejected alternatives, the trade-offs and the rationale.";

    private readonly Lazy<Task<float[]?>> _coreCentroid = new(
        () => embedder.EmbedContentAsync(CoreDomainReference, CancellationToken.None));

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

        var centroid = await _coreCentroid.Value.ConfigureAwait(false);
        if (centroid is null)
        {
            return new PromotionClassResult(false, 0.0f, Name, "No core-domain centroid available");
        }

        var similarity = 1.0f - (float)ZeroShotEmbeddingFilter.CosineDistance(vector, centroid);

        var isEligible = similarity >= eligibilityThreshold;
        var reason = isEligible
            ? $"Core domain similarity {similarity:F2} >= threshold {eligibilityThreshold:F2}"
            : $"Core domain similarity {similarity:F2} < threshold {eligibilityThreshold:F2}";

        return new PromotionClassResult(isEligible, similarity, Name, reason);
    }
}
