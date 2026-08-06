namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Mean-pooling + L2-normalization over the ONNX model's last_hidden_state — the pooling
///     sentence-transformers applies to all-MiniLM-L6-v2 (FR-NM-3; see
///     docs/work/features-native-memory/native-memory.feature).
/// </summary>
internal static class EmbeddingMath
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
            var offset = s * dim;
            for (var d = 0; d < dim; d++)
            {
                pooled[d] += hidden[offset + d];
            }
        }

        if (active == 0)
        {
            return pooled;
        }

        for (var d = 0; d < dim; d++)
        {
            pooled[d] /= active;
        }

        double lengthSquared = 0;
        foreach (var v in pooled)
        {
            lengthSquared += (double)v * v;
        }

        var norm = MathF.Sqrt((float)lengthSquared);
        if (norm > 0)
        {
            for (var d = 0; d < dim; d++)
            {
                pooled[d] /= norm;
            }
        }

        return pooled;
    }
}
