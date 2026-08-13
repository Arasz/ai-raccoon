using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Memory.Filtering;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.Filtering;

[Trait("Speed", "Fast")]
public class OnlineNoiseClusteringServiceTests
{
    [Fact]
    public async Task ProcessCandidateAsync_WhenDistanceIsBelowThreshold_UpdatesExistingClusterCentroid()
    {
        // Arrange
        var fakeStore = new FakeNoiseClusterStore();
        var service = new OnlineNoiseClusteringService(fakeStore);

        var candidateVector = new float[] { 0.96f, 0.04f, 0.0f };
        var candidate = new NoiseCandidate(
            ProjectId: "proj-1",
            UserId: "user-1",
            SampleContent: "proc_123 completed",
            Vector: candidateVector);

        // Act
        var clusterId = await service.ProcessCandidateAsync(candidate, TestContext.Current.CancellationToken);

        // Assert
        clusterId.ShouldBeGreaterThan(0);
        fakeStore.Clusters.Count.ShouldBe(1);
        fakeStore.Clusters[0].Frequency.ShouldBe(2); // Updated frequency
    }

    [Fact]
    public async Task ProcessCandidateAsync_WhenDistanceIsAboveThreshold_SpawnsNewClusterCandidate()
    {
        // Arrange
        var fakeStore = new FakeNoiseClusterStore();
        var service = new OnlineNoiseClusteringService(fakeStore);

        var candidateVector = new float[] { 0.0f, 1.0f, 0.0f };
        var candidate = new NoiseCandidate(
            ProjectId: "proj-1",
            UserId: "user-1",
            SampleContent: "New distinct CLI error trace",
            Vector: candidateVector);

        // Act
        var clusterId = await service.ProcessCandidateAsync(candidate, TestContext.Current.CancellationToken);

        // Assert
        clusterId.ShouldBeGreaterThan(0);
        fakeStore.Clusters.Count.ShouldBe(2); // Spawned new cluster
    }

    private class FakeNoiseClusterStore : INoiseClusterStore
    {
        public List<NoiseCluster> Clusters { get; } = new()
        {
            new NoiseCluster(
                Id: 1,
                ProjectId: "proj-1",
                UserId: "user-1",
                ClusterLabel: "hermes_process_logs",
                SampleContent: "proc_0 completed",
                Frequency: 1,
                Status: "candidate",
                CentroidEmbedding: new float[] { 0.95f, 0.05f, 0.0f })
        };

        public Task<IReadOnlyList<NoiseCluster>> GetClustersAsync(string projectId, string? userId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<NoiseCluster>>(Clusters);
        }

        public Task<long> UpsertClusterAsync(NoiseCluster cluster, CancellationToken cancellationToken = default)
        {
            var existing = Clusters.Find(c => c.Id == cluster.Id);
            if (existing is not null)
            {
                Clusters.Remove(existing);
            }
            Clusters.Add(cluster);
            return Task.FromResult(cluster.Id > 0 ? cluster.Id : Clusters.Count);
        }
    }
}
