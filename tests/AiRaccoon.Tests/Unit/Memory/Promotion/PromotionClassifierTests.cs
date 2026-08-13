using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Promotion;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.Promotion;

[Trait("Speed", "Fast")]
public class PromotionClassifierTests
{
    [Fact]
    public async Task ZeroShotVectorClassifier_WhenEmbeddingAvailable_IsEligibleForMatchingContent()
    {
        // The fake returns the same vector for every input, so the reference-derived centroid
        // equals the candidate vector -> similarity 1.0 -> eligible.
        var embedder = new FakeContentEmbedder(SampleVector());
        var classifier = new ZeroShotVectorPromotionClassifier(embedder);

        var request = new MemoryWriteRequest("proj-1", "# ADR-0029: Pre-Write Noise Filtering Pipeline");
        var result = await classifier.ClassifyCandidateAsync(request, TestContext.Current.CancellationToken);

        result.IsEligibleForPromotion.ShouldBeTrue();
        result.Score.ShouldBeGreaterThan(0.80f);
        embedder.CallCount.ShouldBeGreaterThanOrEqualTo(2, "centroid (reference) + candidate must both be embedded");
    }

    [Fact]
    public async Task ZeroShotVectorClassifier_WhenEmbeddingUnavailable_ReturnsNotEligible()
    {
        var classifier = new ZeroShotVectorPromotionClassifier(NullEmbedder.Instance);

        var result = await classifier.ClassifyCandidateAsync(
            new MemoryWriteRequest("proj-1", "some content"), TestContext.Current.CancellationToken);

        result.IsEligibleForPromotion.ShouldBeFalse();
    }

    [Fact]
    public async Task OnnxInstructClassifier_WhenDisabled_FallsBackToZeroShotVectorClassifier()
    {
        var embedder = new FakeContentEmbedder(SampleVector());
        var classifier = new OnnxInstructPromotionClassifier(embedder, isModelEnabled: false);

        var result = await classifier.ClassifyCandidateAsync(
            new MemoryWriteRequest("proj-1", "# ADR-0029: Pre-Write Noise Filtering Pipeline"),
            TestContext.Current.CancellationToken);

        result.IsEligibleForPromotion.ShouldBeTrue();
        result.ClassifierName.ShouldBe("ZeroShotVectorPromotionClassifier");
    }

    [Fact]
    public async Task OnnxInstructClassifier_WhenEnabled_RoutesThroughItsOwnCentroid()
    {
        var embedder = new FakeContentEmbedder(SampleVector());
        var classifier = new OnnxInstructPromotionClassifier(embedder, isModelEnabled: true);

        var result = await classifier.ClassifyCandidateAsync(
            new MemoryWriteRequest("proj-1", "# ADR-0029: Pre-Write Noise Filtering Pipeline"),
            TestContext.Current.CancellationToken);

        result.IsEligibleForPromotion.ShouldBeTrue();
        result.ClassifierName.ShouldBe("OnnxInstructPromotionClassifier");
    }

    [Fact]
    public async Task CompositeClassifier_WhenModelDisabled_PassesPreScreenAndReturnsVectorResult()
    {
        var embedder = new FakeContentEmbedder(SampleVector());
        var classifier = new CompositePromotionClassifier(embedder, isModelEnabled: false);

        var result = await classifier.ClassifyCandidateAsync(
            new MemoryWriteRequest("proj-1", "# ADR-0029: Pre-Write Noise Filtering Pipeline"),
            TestContext.Current.CancellationToken);

        result.IsEligibleForPromotion.ShouldBeTrue();
        result.ClassifierName.ShouldBe("ZeroShotVectorPromotionClassifier");
    }

    [Fact]
    public async Task CompositeClassifier_WhenModelEnabled_RoutesThroughTheOnnxClassifier()
    {
        var embedder = new FakeContentEmbedder(SampleVector());
        var classifier = new CompositePromotionClassifier(embedder, isModelEnabled: true);

        var result = await classifier.ClassifyCandidateAsync(
            new MemoryWriteRequest("proj-1", "# ADR-0029: Pre-Write Noise Filtering Pipeline"),
            TestContext.Current.CancellationToken);

        result.IsEligibleForPromotion.ShouldBeTrue();
        result.ClassifierName.ShouldBe("OnnxInstructPromotionClassifier");
    }

    private static float[] SampleVector()
    {
        var vector = new float[384];
        vector[0] = 1.0f;
        return vector;
    }

    private sealed class FakeContentEmbedder : IContentEmbedder
    {
        private readonly float[] _vector;

        public FakeContentEmbedder(float[] vector)
        {
            _vector = vector;
        }

        public int CallCount { get; private set; }

        public Task<float[]?> EmbedContentAsync(string content, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult<float[]?>(_vector);
        }
    }

    private sealed class NullEmbedder : IContentEmbedder
    {
        public static readonly NullEmbedder Instance = new();

        public Task<float[]?> EmbedContentAsync(string content, CancellationToken cancellationToken = default) =>
            Task.FromResult<float[]?>(null);
    }
}
