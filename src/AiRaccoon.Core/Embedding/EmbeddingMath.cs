using System.Numerics.Tensors;

namespace AiRaccoon.Core.Embedding;

/// <summary>
///     Pooling + normalization over the ONNX model's hidden states (FR-NM-3; see
///     docs/work/features-native-memory/native-memory.feature). Mean-pool + L2 is the
///     sentence-transformers semantics for all-MiniLM-L6-v2 and moved here VERBATIM (WP3);
///     CLS pooling and the l2/none normalization switch serve manifest models (plan D1).
/// </summary>
public static class EmbeddingMath
{
    /// <summary>
    ///     Mean-pools the [seqLen x dim] hidden state weighted by the attention mask, then
    ///     L2-normalizes. A sequence with no active tokens yields the zero vector.
    /// </summary>
    public static float[] MeanPoolAndNormalize(ReadOnlySpan<float> hidden, ReadOnlySpan<int> mask, int seqLen, int dim)
    {
        var pooled = MeanPool(hidden, mask, seqLen, dim);
        return L2Normalize(pooled);
    }

    /// <summary>Mean-pools without normalizing — the pooling half of the bundled path (normalization=none).</summary>
    public static float[] MeanPool(ReadOnlySpan<float> hidden, ReadOnlySpan<int> mask, int seqLen, int dim)
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

        TensorPrimitives.Divide(pooled, active, pooled);
        return pooled;
    }

    /// <summary>Takes hidden state row 0 (the [CLS]/&lt;s&gt; token) without normalizing.</summary>
    public static float[] ClsPool(ReadOnlySpan<float> hidden, int dim) => [.. hidden[..dim]];

    /// <summary>Row-0 CLS pooling + L2-normalization (bge-m3's default pooling shape, plan D1).</summary>
    public static float[] ClsPoolAndNormalize(ReadOnlySpan<float> hidden, int dim) => L2Normalize(ClsPool(hidden, dim));

    /// <summary>
    ///     L2-normalizes in place of the vector copy; the zero vector stays zero (matches the
    ///     bundled mean path's guard).
    /// </summary>
    public static float[] L2Normalize(ReadOnlySpan<float> vector)
    {
        var result = vector.ToArray();
        var norm = TensorPrimitives.Norm(result);
        if (norm > 0)
        {
            TensorPrimitives.Divide(result, norm, result);
        }

        return result;
    }
}
