using AiRaccoon.Core.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     Unit tests for the mean-pool + L2-normalize math that turns the ONNX model's
///     last_hidden_state into a sentence embedding (sentence-transformers semantics).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EmbeddingMathTests
{
    [Fact]
    public void MeanPoolAndNormalize_WithSingleActiveToken_ReturnsThatTokenNormalized()
    {
        // seqLen 2, dim 3; only the first token attends.
        float[] hidden = [1, 2, 3, 4, 5, 6];
        int[] mask = [1, 0];

        var result = EmbeddingMath.MeanPoolAndNormalize(hidden, mask, 2, 3);

        var norm = Math.Sqrt(1 + 4 + 9);
        result[0].ShouldBe((float)(1 / norm), 1e-6);
        result[1].ShouldBe((float)(2 / norm), 1e-6);
        result[2].ShouldBe((float)(3 / norm), 1e-6);
    }

    [Fact]
    public void MeanPoolAndNormalize_AveragesOverAllActiveTokens_ThenNormalizes()
    {
        // seqLen 2, dim 3; both tokens attend → mean = [2.5, 3.5, 4.5].
        float[] hidden = [1, 2, 3, 4, 5, 6];
        int[] mask = [1, 1];

        var result = EmbeddingMath.MeanPoolAndNormalize(hidden, mask, 2, 3);

        var norm = Math.Sqrt(2.5 * 2.5 + 3.5 * 3.5 + 4.5 * 4.5);
        result[0].ShouldBe((float)(2.5 / norm), 1e-6);
        result[1].ShouldBe((float)(3.5 / norm), 1e-6);
        result[2].ShouldBe((float)(4.5 / norm), 1e-6);
    }

    [Fact]
    public void MeanPoolAndNormalize_ResultIsUnitLength()
    {
        float[] hidden = [0.1f, -0.2f, 0.3f, 0.4f, -0.5f, 0.6f, 0.7f, 0.8f, -0.9f];
        int[] mask = [1, 1, 1];

        var result = EmbeddingMath.MeanPoolAndNormalize(hidden, mask, 3, 3);

        var lengthSquared = result.Sum(v => (double)v * v);
        lengthSquared.ShouldBe(1.0, 1e-6);
    }

    [Fact]
    public void MeanPoolAndNormalize_WithNoActiveTokens_ReturnsZeroVector()
    {
        float[] hidden = [1, 2, 3];
        int[] mask = [0];

        var result = EmbeddingMath.MeanPoolAndNormalize(hidden, mask, 1, 3);

        result.ShouldAllBe(v => v == 0);
    }

    /// <summary>
    ///     dim = 384 (production dimension) exercises a full SIMD lane; every other test in this
    ///     class uses dim = 3, which is entirely vector tail. Mask is active-first at ~60%
    ///     density, matching OnnxEmbeddingGenerator.RunBatch's layout.
    /// </summary>
    [Fact]
    public void MeanPoolAndNormalize_AtProductionDimension_MatchesNaiveReference()
    {
        const int dim = 384;
        const int seqLen = 256;
        const int active = 154; // ~60% of 256, active-first.

        var random = new Random(20260808);
        var hidden = new float[seqLen * dim];
        for (var i = 0; i < hidden.Length; i++)
        {
            hidden[i] = (float)(random.NextDouble() * 2 - 1);
        }

        var mask = new int[seqLen];
        for (var s = 0; s < active; s++)
        {
            mask[s] = 1;
        }

        var result = EmbeddingMath.MeanPoolAndNormalize(hidden, mask, seqLen, dim);

        var reference = new double[dim];
        for (var s = 0; s < active; s++)
        {
            var offset = s * dim;
            for (var d = 0; d < dim; d++)
            {
                reference[d] += hidden[offset + d];
            }
        }

        for (var d = 0; d < dim; d++)
        {
            reference[d] /= active;
        }

        var norm = Math.Sqrt(reference.Sum(v => v * v));
        for (var d = 0; d < dim; d++)
        {
            reference[d] /= norm;
        }

        for (var d = 0; d < dim; d++)
        {
            result[d].ShouldBe((float)reference[d], 1e-5);
        }
    }
}
