using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;

namespace AiRaccoon.Infrastructure.Embedding.Download;

/// <summary>Applies output-rank facts reported by the downloaded ONNX graph to a plan.</summary>
internal static class ModelDownloadPlanPooling
{
    /// <summary>A rank-2 token output is already pooled by the graph and overrides inferred pooling.</summary>
    internal static ModelDownloadPlan ApplyGraphOutputRanks(
        ModelDownloadPlan plan,
        IReadOnlyDictionary<string, int> outputRanks)
    {
        var output = plan.TokenEmbeddingsOutput;
        if (plan.PoolingMode == PoolingMode.ModelOutput || string.IsNullOrWhiteSpace(output)
                                                        || !outputRanks.TryGetValue(output, out var rank) || rank != OnnxOutputRanks.PooledRank)
        {
            return plan;
        }

        return plan with
        {
            PoolingMode = PoolingMode.ModelOutput,
            EmbeddingOutput = output,
            PoolingProvenance = "onnx-graph"
        };
    }
}
