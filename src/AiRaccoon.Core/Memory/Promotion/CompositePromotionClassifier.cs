namespace AiRaccoon.Core.Memory.Promotion;

using System;
using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;

public sealed class CompositePromotionClassifier(
    IContentEmbedder embedder,
    Func<bool>? isModelEnabled = null) : IPromotionClassifier
{
    public string Name => "CompositePromotionClassifier";

    /// <summary>Read through the injected accessor so the settings-table lookup happens at use, not at DI construction.</summary>
    public bool IsModelEnabled => isModelEnabled?.Invoke() ?? false;

    private readonly ZeroShotVectorPromotionClassifier _vectorClassifier = new(embedder);
    private readonly OnnxInstructPromotionClassifier _onnxClassifier = new(embedder, isModelEnabled);

    public async ValueTask<PromotionClassResult> ClassifyCandidateAsync(
        MemoryWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Step 1: Fast zero-shot vector distance pre-screen
        var vectorResult = await _vectorClassifier.ClassifyCandidateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!vectorResult.IsEligibleForPromotion)
        {
            return vectorResult; // Rejected at pre-screen
        }

        if (!IsModelEnabled)
        {
            return vectorResult;
        }

        // Step 2: ONNX Instruct evaluation for candidates passing pre-screen
        return await _onnxClassifier.ClassifyCandidateAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
