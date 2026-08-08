using System.Numerics.Tensors;

namespace AiRaccoon.Core.Embedding;

/// <summary>
///     Mean-pooling + L2-normalization over the ONNX model's last_hidden_state — the pooling
///     sentence-transformers applies to all-MiniLM-L6-v2 (FR-NM-3; see
///     docs/work/features-native-memory/native-memory.feature).
/// </summary>
public static class EmbeddingMath
{
    public const int Dimension = 384;

    /// <summary>
    ///     Mean-pools the [seqLen x dim] hidden state weighted by the attention mask, then
    ///     L2-normalizes. A sequence with no active tokens yields the zero vector.
    /// </summary>
    public static float[] MeanPoolAndNormalize(ReadOnlySpan<float> hidden, ReadOnlySpan<int> mask, int seqLen, int dim)
    {
        var pooled = new float[dim];
        var active = 0;
        for (var s = 0; s < seqLen; s++)
        {
            if (mask[s] == 0)
            {
                continue;
            }

            active++;
            TensorPrimitives.Add(pooled, hidden.Slice(s * dim, dim), pooled);
        }

        if (active == 0)
        {
            return pooled;
        }

        TensorPrimitives.Divide(pooled, (float)active, pooled);

        var norm = TensorPrimitives.Norm((ReadOnlySpan<float>)pooled);
        if (!(norm > 0))
        {
            return pooled;
        }

        TensorPrimitives.Divide(pooled, norm, pooled);

        return pooled;
    }
}
