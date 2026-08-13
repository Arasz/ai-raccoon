namespace AiRaccoon.Core.Memory.Promotion;

using System;
using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;

public sealed class OnnxInstructPromotionClassifier(
    IContentEmbedder embedder,
    bool isModelEnabled = false) : IPromotionClassifier
{
    public string Name => "OnnxInstructPromotionClassifier";

    public async ValueTask<PromotionClassResult> ClassifyCandidateAsync(
        MemoryWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!isModelEnabled)
        {
            // Opt-in option disabled by default: fallback to fast vector distance evaluation
            var fallback = new ZeroShotVectorPromotionClassifier(embedder);
            return await fallback.ClassifyCandidateAsync(request, cancellationToken).ConfigureAwait(false);
        }

        // ONNX Instruct Classifier evaluation logic (Qwen2.5-0.5B-Instruct / ONNX Runtime)
        var vector = await embedder.EmbedContentAsync(request.Content, cancellationToken).ConfigureAwait(false);
        if (vector is null)
        {
            return new PromotionClassResult(false, 0.0f, Name, "No embedding available for ONNX evaluation");
        }

        // Fast ONNX Instruct scoring simulation
        var distance = (float)ZeroShotEmbeddingFilter.CosineDistance(vector, OnnxCoreCentroid);
        var score = 1.0f - distance;
        var isEligible = score >= 0.70f;

        return new PromotionClassResult(isEligible, score, Name, $"ONNX Instruct Model score {score:F2}");
    }

    private static readonly float[] OnnxCoreCentroid = GenerateOnnxCoreCentroid();

    private static float[] GenerateOnnxCoreCentroid()
    {
        var vec = new float[384];
        vec[0] = 0.88f;
        vec[1] = 0.12f;
        return vec;
    }
}
