using AiRaccoon.Core.Embedding;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Embedding.Manifest;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     ADR-0114's zero-waste rule pinned against the REAL bundled manifest: a full chunk of the
///     manifest's own chunkTokens budget plus [CLS]/[SEP] is exactly one MLX 64-token bucket (no
///     padding waste, no extra compiled shape) and fits the CoreML 1024-token window (no silent CPU
///     fallback). Derived from ai-raccoon.manifest.json, never a literal: a manifest edit to
///     chunkTokens must keep both properties or this fails.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BundledManifestBucketAlignmentTests
{
    [RetryFact]
    public void TheBundledManifestBudgetPlusesSpecialTokens_IsExactlyOneMlxBucket_AndFitsTheCoreMlWindow()
    {
        var descriptor = new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator())
            .Load(BundledModel.ResolveDirectory());
        descriptor.ChunkTokens.ShouldNotBeNull(
            "the bundled manifest must declare chunkTokens — the budget's source of truth (config D)");
        var fullRow = descriptor.ChunkTokens!.Value + descriptor.SpecialTokenReservation;

        LengthBuckets.PaddedLength(fullRow, descriptor.ContextWindowTokens).ShouldBe(fullRow,
            "a full chunk must be an exact 64-token MLX bucket, or every full row pads and compiles its own shape (ADR-0114)");
        fullRow.ShouldBeLessThanOrEqualTo(LengthBuckets.CoreMlWindow,
            "past the CoreML window every row silently falls back to the CPU (ADR-0118)");
    }
}
