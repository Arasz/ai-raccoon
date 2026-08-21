using AiRaccoon.Core.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     WP3 pooling/normalization strategies (plan D1; engineer doc §3): CLS pooling (bge-m3's
///     default) and the L2/none normalization switch. Mean-pool + L2 stays covered by
///     <see cref="EmbeddingMathTests" /> — the mean path moved VERBATIM.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EmbeddingMathPoolingStrategyTests
{
    [Fact]
    public void ClsPoolAndNormalize_TakesRowZero_AndNormalizes()
    {
        // seqLen 2, dim 2: row 0 = [3, 4], row 1 = [1, 2] → CLS = [3, 4] normalized → [0.6, 0.8].
        float[] hidden = [3, 4, 1, 2];

        var result = EmbeddingMath.ClsPoolAndNormalize(hidden, 2);

        result[0].ShouldBe(0.6f, 1e-6);
        result[1].ShouldBe(0.8f, 1e-6);
    }

    [Fact]
    public void ClsPoolAndNormalize_ZeroRow_ReturnsZeroVector()
    {
        float[] hidden = [0, 0, 5, 6];

        var result = EmbeddingMath.ClsPoolAndNormalize(hidden, 2);

        result.ShouldAllBe(v => v == 0);
    }

    [Fact]
    public void ClsPool_TakesRowZero_WithoutNormalizing()
    {
        float[] hidden = [3, 4, 1, 2];

        var result = EmbeddingMath.ClsPool(hidden, 2);

        result.ShouldBe([3f, 4f]);
    }

    [Fact]
    public void L2Normalize_DividesByTheNorm()
    {
        var result = EmbeddingMath.L2Normalize([3f, 4f]);

        result[0].ShouldBe(0.6f, 1e-6);
        result[1].ShouldBe(0.8f, 1e-6);
    }

    [Fact]
    public void L2Normalize_ZeroVector_StaysZero()
    {
        var result = EmbeddingMath.L2Normalize([0f, 0f, 0f]);

        result.ShouldAllBe(v => v == 0);
    }
}
