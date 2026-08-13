namespace AiRaccoon.Core.Memory.Promotion;

using System;
using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;

public sealed class CompositePromotionClassifier(
    IContentEmbedder embedder,
    bool isModelEnabled = false) : IPromotionClassifier
{
    public string Name => "CompositePromotionClassifier";

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

        if (!isModelEnabled)
        {
            return vectorResult;
        }

        // Step 2: ONNX Instruct evaluation for candidates passing pre-screen
        return await _onnxClassifier.ClassifyCandidateAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
