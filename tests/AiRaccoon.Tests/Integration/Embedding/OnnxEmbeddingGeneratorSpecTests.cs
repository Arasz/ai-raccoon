using AiRaccoon.Infrastructure.Embedding;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     WP3 generator generalization (plan D1, engineer doc §3/§4): pooling strategy selection,
///     normalization switch and runtime dimension reporting — all testable against the bundled
///     384-dim model, so the same shapes work for a manifest engine later.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class OnnxEmbeddingGeneratorSpecTests : IAsyncLifetime
{
    public async ValueTask InitializeAsync() =>
        await TestData.CreateBundledModel().EnsureAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static OnnxEmbeddingGenerator Build(string pooling = "mean", string normalization = "l2") =>
        new(
            BundledModel.ResolveModelPath(),
            WordPieceEmbeddingTokenizer.Create(BundledModel.ResolveVocabPath()),
            EmbeddingService.BundledDescriptor with { Pooling = pooling, Normalization = normalization },
            NullLogger<OnnxEmbeddingGenerator>.Instance);

    [Fact]
    public async Task Dimension_IsDerivedFromTheSessionOutput_NotAConstant()
    {
        using var generator = Build();
        generator.Dimension.ShouldBe(384);
    }

    [Fact]
    public async Task ClsPooling_SelectsTheFirstTokenRow_AndNormalizes()
    {
        using var mean = Build("mean", "l2");
        using var cls = Build("cls", "l2");
        const string text = "Memory retrieval ranking evidence for the CLS pooling probe";

        var meanVector = (await mean.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken))[0].Vector.ToArray();
        var clsVector = (await cls.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken))[0].Vector.ToArray();

        var clsNorm = Math.Sqrt(clsVector.Sum(v => (double)v * v));
        clsNorm.ShouldBe(1.0, 1e-4, "CLS embeddings are L2-normalized by default");
        var drift = 1.0 - meanVector.Zip(clsVector, (a, b) => (double)a * b).Sum();
        drift.ShouldBeGreaterThan(1e-3, "CLS pooling must produce a different vector than mean pooling");
    }

    [Fact]
    public async Task NormalizationNone_LeavesTheVectorUnnormalized()
    {
        using var generator = Build("mean", "none");
        const string text = "A vector that is not L2-normalized keeps its raw scale";

        var vector = (await generator.GenerateAsync([text], cancellationToken: TestContext.Current.CancellationToken))[0].Vector.ToArray();

        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        (norm - 1.0).ShouldBeGreaterThan(1e-3, "normalization=none must not normalize");
    }

    [Fact]
    public void ModelOutputPooling_WithoutAnEmbeddingOutput_IsRejectedAtConstruction()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            new OnnxEmbeddingGenerator(
                BundledModel.ResolveModelPath(),
                WordPieceEmbeddingTokenizer.Create(BundledModel.ResolveVocabPath()),
                EmbeddingService.BundledDescriptor with { Pooling = "model-output", EmbeddingOutput = null },
                NullLogger<OnnxEmbeddingGenerator>.Instance));

        ex.Message.ShouldContain("model-output");
        ex.Message.ShouldContain("embeddingOutput");
    }
}
