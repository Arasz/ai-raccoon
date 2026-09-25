using AiRaccoon.Core.Embedding;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     ADR-0118: the CoreML/Neural-Engine path buckets at 256-token steps up to a 1024-token window,
///     coarser than MLX's 64-token step because finer buckets cost more cache than they save. Every
///     shipped chunk budget, plus the two special tokens the bundled/manifest engines reserve at
///     embed time, must fit inside that window without the generator's CPU-fallback path.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CoreMlWindowBudgetTests
{
    public static TheoryData<int> ShippedChunkBudgets =>
    [
        OnnxEmbeddingGenerator.MaxContentTokens,
        EmbeddingService.MaxManifestChunkTokens,
        CodeChunker.DefaultBudget
    ];

    [Theory]
    [MemberData(nameof(ShippedChunkBudgets))]
    public void EveryShippedChunkBudgetPlusSpecialTokens_FitsTheCoreMlWindow(int budget) =>
        (budget + EngineDescriptor.DefaultSpecialTokenReservation).ShouldBeLessThanOrEqualTo(LengthBuckets.CoreMlWindow);
}
