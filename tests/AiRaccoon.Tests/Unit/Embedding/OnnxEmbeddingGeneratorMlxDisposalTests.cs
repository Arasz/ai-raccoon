using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Unit.Embedding;

/// <summary>
///     ADR-0110: an MLX-backed generator's Dispose must stay idempotent — the standard IDisposable
///     contract every other session shape here already honors. Exercises the MLX disposal branch
///     with a real <see cref="SingleThreadExecutor" /> attached through the generator's test seam,
///     so it needs no onnxruntime MLX plugin (unavailable on this test host).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class OnnxEmbeddingGeneratorMlxDisposalTests
{
    [RetryFact]
    public void Dispose_CalledTwiceOnAnMlxBackedGenerator_DoesNotThrow()
    {
        var generator = new OnnxEmbeddingGenerator(TestData.MiniLmModelPath(),
            WordPieceEmbeddingTokenizer.Create(BundledModel.ResolveVocabPath()),
            EmbeddingService.BundledDescriptor, NullLogger.Instance);
        generator.AttachMlxExecutorForTesting(new SingleThreadExecutor("mlx-disposal-test"));

        generator.Dispose();

        Should.NotThrow(generator.Dispose);
    }
}
