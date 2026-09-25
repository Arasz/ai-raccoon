using AiRaccoon.Core.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     ADR-0113: an MLX row is padded up to the next multiple of 64 tokens, never past the model's
///     window, so the many chunk lengths of a corpus collapse into a handful of compiled shapes.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class LengthBucketsTests
{
    [Theory]
    [InlineData(1, 64)]
    [InlineData(63, 64)]
    [InlineData(64, 64)]
    [InlineData(65, 128)]
    [InlineData(200, 256)]
    [InlineData(256, 256)]
    [InlineData(500, 512)]
    [InlineData(700, 704)]
    [InlineData(705, 768)]
    [InlineData(1000, 1024)]
    public void PaddedLength_RoundsUpToTheNextMultipleOf64(int length, int expected) =>
        LengthBuckets.PaddedLength(length, 8190).ShouldBe(expected);

    [Fact]
    public void PaddedLength_NeverExceedsTheWindow() =>
        LengthBuckets.PaddedLength(8150, 8190).ShouldBe(8190);

    [Fact]
    public void PaddedLength_ShippedChunkBudgetsFillTheirWindowTopExactly()
    {
        // Each shipped budget plus [CLS]/[SEP] is a bucket of its own, so a full chunk pads nothing.
        foreach (var budget in new[] { 254, 510, 766, 1022 })
        {
            LengthBuckets.PaddedLength(budget + 2, 8190).ShouldBe(budget + 2);
        }
    }

    [Fact]
    public void PaddedLength_ZeroLength_IsRejected() =>
        Should.Throw<ArgumentOutOfRangeException>(() => LengthBuckets.PaddedLength(0, 8190));
}
