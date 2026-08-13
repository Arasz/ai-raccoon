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
    public async Task ZeroShotVectorClassifier_WhenVectorIsSimilarToCore_ReturnsEligible()
    {
        var testVector = new float[384];
        testVector[0] = 0.85f;
        testVector[1] = 0.15f;

        var embedder = new FakeContentEmbedder(testVector);
        var classifier = new ZeroShotVectorPromotionClassifier(embedder);

        var request = new MemoryWriteRequest("proj-1", "# ADR-0029: Pre-Write Noise Filtering Pipeline");
        var result = await classifier.ClassifyCandidateAsync(request, TestContext.Current.CancellationToken);

        result.IsEligibleForPromotion.ShouldBeTrue();
        result.Score.ShouldBeGreaterThan(0.80f);
    }

    [Fact]
    public async Task ZeroShotVectorClassifier_WhenVectorIsOrthogonal_ReturnsNotEligible()
    {
        var testVector = new float[384];
        testVector[2] = 1.0f; // Orthogonal to index 0,1

        var embedder = new FakeContentEmbedder(testVector);
        var classifier = new ZeroShotVectorPromotionClassifier(embedder);

        var request = new MemoryWriteRequest("proj-1", "[IMPORTANT: Background process proc_123 completed normally...]");
        var result = await classifier.ClassifyCandidateAsync(request, TestContext.Current.CancellationToken);

        result.IsEligibleForPromotion.ShouldBeFalse();
    }

    [Fact]
    public async Task OnnxInstructClassifier_WhenDisabled_FallsBackToZeroShotVectorClassifier()
    {
        var testVector = new float[384];
        testVector[0] = 0.85f;
        testVector[1] = 0.15f;

        var embedder = new FakeContentEmbedder(testVector);
        var classifier = new OnnxInstructPromotionClassifier(embedder, isModelEnabled: false);

        var request = new MemoryWriteRequest("proj-1", "# ADR-0029: Pre-Write Noise Filtering Pipeline");
        var result = await classifier.ClassifyCandidateAsync(request, TestContext.Current.CancellationToken);

        result.IsEligibleForPromotion.ShouldBeTrue();
        result.ClassifierName.ShouldBe("ZeroShotVectorPromotionClassifier");
    }

    [Fact]
    public async Task CompositeClassifier_WhenModelDisabled_PassesPreScreenAndReturnsVectorResult()
    {
        var testVector = new float[384];
        testVector[0] = 0.85f;
        testVector[1] = 0.15f;

        var embedder = new FakeContentEmbedder(testVector);
        var classifier = new CompositePromotionClassifier(embedder, isModelEnabled: false);

        var request = new MemoryWriteRequest("proj-1", "# ADR-0029: Pre-Write Noise Filtering Pipeline");
        var result = await classifier.ClassifyCandidateAsync(request, TestContext.Current.CancellationToken);

        result.IsEligibleForPromotion.ShouldBeTrue();
    }

    private class FakeContentEmbedder : IContentEmbedder
    {
        private readonly float[] _vector;

        public FakeContentEmbedder(float[] vector)
        {
            _vector = vector;
        }

        public Task<float[]?> EmbedContentAsync(string content, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<float[]?>(_vector);
        }
    }
}
