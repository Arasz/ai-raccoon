using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Embedding;

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

        var result = EmbeddingMath.MeanPoolAndNormalize(hidden, mask, seqLen: 2, dim: 3);

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

        var result = EmbeddingMath.MeanPoolAndNormalize(hidden, mask, seqLen: 2, dim: 3);

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

        var result = EmbeddingMath.MeanPoolAndNormalize(hidden, mask, seqLen: 3, dim: 3);

        var lengthSquared = result.Sum(v => (double)v * v);
        lengthSquared.ShouldBe(1.0, 1e-6);
    }

    [Fact]
    public void MeanPoolAndNormalize_WithNoActiveTokens_ReturnsZeroVector()
    {
        float[] hidden = [1, 2, 3];
        int[] mask = [0];

        var result = EmbeddingMath.MeanPoolAndNormalize(hidden, mask, seqLen: 1, dim: 3);

        result.ShouldAllBe(v => v == 0);
    }
}
