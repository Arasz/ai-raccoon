using AiRaccoon.Core.Memory.Filtering;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.Filtering;

/// <summary>Leader-follower clustering: assign to the nearest centroid within threshold, else spawn a new candidate cluster. See docs/adr/0039.</summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class OnlineNoiseClusteringServiceTests
{
    private static NoiseCandidate Candidate(float[] vector, string content = "sample") =>
        new("proj-1", "agent-1", content, vector);

    [Fact]
    public async Task ProcessCandidateAsync_NoExistingClusters_SpawnsANewCandidateCluster()
    {
        var store = new FakeNoiseClusterStore();
        var sut = new OnlineNoiseClusteringService(store);

        var id = await sut.ProcessCandidateAsync(Candidate([1f, 0f]), 0.12, TestContext.Current.CancellationToken);

        id.ShouldBe(1);
        store.Upserted.Count.ShouldBe(1);
        store.Upserted[0].Status.ShouldBe("candidate");
        store.Upserted[0].Frequency.ShouldBe(1);
    }

    [Fact]
    public async Task ProcessCandidateAsync_WithinThreshold_UpdatesExistingClusterInsteadOfSpawning()
    {
        var existing = new NoiseCluster(7, "proj-1", "agent-1", "noise_cluster_abc", "prior sample", 1, "candidate", [1f, 0f]);
        var store = new FakeNoiseClusterStore([existing]);
        var sut = new OnlineNoiseClusteringService(store);

        // Near-identical vector: cosine distance well under the 0.12 threshold.
        var id = await sut.ProcessCandidateAsync(Candidate([0.999f, 0.045f]), 0.12, TestContext.Current.CancellationToken);

        id.ShouldBe(7);
        store.Upserted.Count.ShouldBe(1);
        store.Upserted[0].ClusterLabel.ShouldBe("noise_cluster_abc");
        store.Upserted[0].Frequency.ShouldBe(2);
    }

    [Fact]
    public async Task ProcessCandidateAsync_BeyondThreshold_SpawnsANewClusterInsteadOfReusing()
    {
        var existing = new NoiseCluster(7, "proj-1", "agent-1", "noise_cluster_abc", "prior sample", 1, "candidate", [1f, 0f]);
        var store = new FakeNoiseClusterStore([existing]);
        var sut = new OnlineNoiseClusteringService(store);

        // Orthogonal vector: cosine distance is 1.0, far past any sane threshold.
        var id = await sut.ProcessCandidateAsync(Candidate([0f, 1f]), 0.12, TestContext.Current.CancellationToken);

        store.Upserted.Count.ShouldBe(1);
        store.Upserted[0].ClusterLabel.ShouldNotBe("noise_cluster_abc");
        store.Upserted[0].Frequency.ShouldBe(1);
    }

    private sealed class FakeNoiseClusterStore(IReadOnlyList<NoiseCluster>? seed = null) : INoiseClusterStore
    {
        private long _nextId = 1;
        private readonly List<NoiseCluster> _clusters = seed is null ? [] : [.. seed];

        public List<NoiseCluster> Upserted { get; } = [];

        public Task<IReadOnlyList<NoiseCluster>> GetClustersAsync(string projectId, string? userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<NoiseCluster>>(_clusters);

        public Task<long> UpsertClusterAsync(NoiseCluster cluster, CancellationToken cancellationToken = default)
        {
            Upserted.Add(cluster);
            var id = cluster.Id == 0 ? _nextId++ : cluster.Id;
            return Task.FromResult(id);
        }
    }
}
