using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.Filtering;

/// <summary>
///     Wires an external labelled-negative signal (a rejected write's content) into
///     <see cref="INoiseClusterStore" />, append-only — no assignment or merging against existing
///     samples, since that is scoring logic ADR-0039 defers to a future detector. Scope immunity
///     protects curated content (ADRs, shared docs) from ever becoming a training negative.
///     <para>
///     The fake embedder here deterministically derives a vector from the input string's characters
///     (never a fixed vector) — the original subsystem's tests used a fixed-vector fake regardless of
///     input, which is exactly what let a "noise" and "clean" test swap strings and both stay green
///     (ADR-0033/ADR-0039). <see cref="FakeContentEmbedder" /> below cannot pass that way: distinct
///     inputs provably produce distinct vectors (see <see cref="FakeContentEmbedder_ProducesDifferentVectorsForDifferentContent" />).
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class NoiseFeedbackCollectorTests
{
    [Fact]
    public async Task ProcessFeedbackAsync_ImmuneContent_NeverEmbedsOrStores()
    {
        var embedder = new FakeContentEmbedder();
        var store = new SpyNoiseClusterStore();
        var sut = new NoiseFeedbackCollector(store, embedder);

        await sut.ProcessFeedbackAsync("proj-1", "agent-1", "# ADR-0012 something", "test",
            cancellationToken: TestContext.Current.CancellationToken);

        embedder.CallCount.ShouldBe(0);
        store.UpsertCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ProcessFeedbackAsync_OrdinaryContent_EmbedsAndAppendsOneSample()
    {
        var embedder = new FakeContentEmbedder();
        var store = new SpyNoiseClusterStore();
        var sut = new NoiseFeedbackCollector(store, embedder);

        await sut.ProcessFeedbackAsync("proj-1", "agent-1", "background process completed normally", "hermes-hit",
            cancellationToken: TestContext.Current.CancellationToken);

        embedder.CallCount.ShouldBe(1);
        store.UpsertCallCount.ShouldBe(1);
        store.Upserted[0].Frequency.ShouldBe(1);
        store.Upserted[0].Status.ShouldBe("candidate");
    }

    [Fact]
    public async Task ProcessFeedbackAsync_TwoSimilarSamples_AppendTwoSeparateRows_NoMerging()
    {
        var embedder = new FakeContentEmbedder();
        var store = new SpyNoiseClusterStore();
        var sut = new NoiseFeedbackCollector(store, embedder);
        var ct = TestContext.Current.CancellationToken;

        await sut.ProcessFeedbackAsync("proj-1", "agent-1", "background process alpha completed", "hermes-hit", cancellationToken: ct);
        await sut.ProcessFeedbackAsync("proj-1", "agent-1", "background process beta completed", "hermes-hit", cancellationToken: ct);

        store.UpsertCallCount.ShouldBe(2, "append-only: no nearest-neighbour assignment happens here (ADR-0039)");
        store.Upserted[0].ClusterLabel.ShouldNotBe(store.Upserted[1].ClusterLabel);
    }

    [Fact]
    public async Task ProcessFeedbackAsync_WithPrecomputedVector_SkipsTheEmbedderEntirely()
    {
        var embedder = new FakeContentEmbedder();
        var store = new SpyNoiseClusterStore();
        var sut = new NoiseFeedbackCollector(store, embedder);

        await sut.ProcessFeedbackAsync("proj-1", "agent-1", "anything", "test",
            precomputedVector: [1f, 0f], cancellationToken: TestContext.Current.CancellationToken);

        embedder.CallCount.ShouldBe(0);
        store.UpsertCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task ProcessFeedbackAsync_EmbedderDegrades_NeverReachesTheStore()
    {
        var embedder = new FakeContentEmbedder(alwaysReturnsNull: true);
        var store = new SpyNoiseClusterStore();
        var sut = new NoiseFeedbackCollector(store, embedder);

        await sut.ProcessFeedbackAsync("proj-1", "agent-1", "anything", "test",
            cancellationToken: TestContext.Current.CancellationToken);

        store.UpsertCallCount.ShouldBe(0);
    }

    [Fact]
    public async Task FakeContentEmbedder_ProducesDifferentVectorsForDifferentContent()
    {
        var embedder = new FakeContentEmbedder();
        var ct = TestContext.Current.CancellationToken;

        var a = await embedder.EmbedContentAsync("the quick brown fox", ct);
        var b = await embedder.EmbedContentAsync("a totally unrelated sentence about databases", ct);

        a.ShouldNotBeNull();
        b.ShouldNotBeNull();
        a.ShouldNotBe(b);
    }

    /// <summary>Deterministically varies with input (character-code projection), unlike the original's fixed-vector fake.</summary>
    private sealed class FakeContentEmbedder(bool alwaysReturnsNull = false) : IContentEmbedder
    {
        public int CallCount { get; private set; }

        public Task<float[]?> EmbedContentAsync(string content, CancellationToken cancellationToken = default)
        {
            CallCount++;
            if (alwaysReturnsNull)
            {
                return Task.FromResult<float[]?>(null);
            }

            var vector = new float[4];
            for (var i = 0; i < content.Length; i++)
            {
                vector[i % vector.Length] += content[i];
            }

            var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
            if (norm > 0)
            {
                for (var i = 0; i < vector.Length; i++)
                {
                    vector[i] = (float)(vector[i] / norm);
                }
            }

            return Task.FromResult<float[]?>(vector);
        }
    }

    private sealed class SpyNoiseClusterStore : INoiseClusterStore
    {
        public int UpsertCallCount { get; private set; }
        public List<NoiseCluster> Upserted { get; } = [];

        public Task<IReadOnlyList<NoiseCluster>> GetClustersAsync(string projectId, string? userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NoiseCluster>>([]);

        public Task<long> UpsertClusterAsync(NoiseCluster cluster, CancellationToken cancellationToken = default)
        {
            UpsertCallCount++;
            Upserted.Add(cluster);
            return Task.FromResult((long)UpsertCallCount);
        }
    }
}
