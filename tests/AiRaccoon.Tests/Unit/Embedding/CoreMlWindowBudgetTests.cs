using AiRaccoon.Core.Embedding;
using AiRaccoon.Infrastructure.Chunking;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     ADR-0118: the CoreML/Neural-Engine path buckets at 256-token steps up to a 1024-token window,
///     coarser than MLX's 64-token step because finer buckets cost more cache than they save. Every
///     shipped chunk budget — the bundled manifest's own chunkTokens included, derived so a manifest
///     edit cannot escape this check — plus the two special tokens the bundled/manifest engines
///     reserve at embed time, must fit inside that window without the generator's CPU-fallback path.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class CoreMlWindowBudgetTests
{
    public static TheoryData<int> ShippedChunkBudgets =>
    [
        OnnxEmbeddingGenerator.MaxContentTokens,
        EmbeddingService.MaxManifestChunkTokens,
        CodeChunker.DefaultBudget,
        BundledManifestChunkTokens()
    ];

    /// <summary>The bundled manifest's chunkTokens — the shipped memory budget (config D), read from
    /// the manifest so this gate follows it wherever it is set.</summary>
    private static int BundledManifestChunkTokens() =>
        new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator())
            .Load(BundledModel.ResolveDirectory()).ChunkTokens
        ?? throw new InvalidOperationException("the bundled manifest must declare chunkTokens");

    [Theory]
    [MemberData(nameof(ShippedChunkBudgets))]
    public void EveryShippedChunkBudgetPlusSpecialTokens_FitsTheCoreMlWindow(int budget) =>
        (budget + EngineDescriptor.DefaultSpecialTokenReservation).ShouldBeLessThanOrEqualTo(LengthBuckets.CoreMlWindow);
}
