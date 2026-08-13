namespace AiRaccoon.Core.Memory.Promotion;

using System;
using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;

public sealed class OnnxInstructPromotionClassifier(
    IContentEmbedder embedder,
    Func<bool>? isModelEnabled = null) : IPromotionClassifier
{
    public string Name => "OnnxInstructPromotionClassifier";

    /// <summary>Read through the injected accessor so the settings-table lookup happens at use, not at DI construction.</summary>
    public bool IsModelEnabled => isModelEnabled?.Invoke() ?? false;

    /// <summary>Canonical promotable-reference text — the centroid is its real embedding, not a synthetic vector.</summary>
    private const string PromotableReference =
        "A cross-project reusable fact worth sharing: a durable architectural decision or invariant that every project benefits from knowing.";

    private readonly Lazy<Task<float[]?>> _coreCentroid = new(
        () => embedder.EmbedContentAsync(PromotableReference, CancellationToken.None));

    public async ValueTask<PromotionClassResult> ClassifyCandidateAsync(
        MemoryWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsModelEnabled)
        {
            // Opt-in option disabled by default: fallback to fast vector distance evaluation
            var fallback = new ZeroShotVectorPromotionClassifier(embedder);
            return await fallback.ClassifyCandidateAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var vector = await embedder.EmbedContentAsync(request.Content, cancellationToken).ConfigureAwait(false);
        if (vector is null)
        {
            return new PromotionClassResult(false, 0.0f, Name, "No embedding available for ONNX evaluation");
        }

        var centroid = await _coreCentroid.Value.ConfigureAwait(false);
        if (centroid is null)
        {
            return new PromotionClassResult(false, 0.0f, Name, "No promotable centroid available");
        }

        var similarity = 1.0f - (float)ZeroShotEmbeddingFilter.CosineDistance(vector, centroid);
        // Preliminary threshold from a 3-point measurement (ADRs ~0.28 vs the core reference,
        // noise ~0.04) — recalibrate against a labeled dataset before relying on this gate.
        var isEligible = similarity >= 0.15f;

        return new PromotionClassResult(isEligible, similarity, Name, $"ONNX Instruct Model score {similarity:F2}");
    }
}
