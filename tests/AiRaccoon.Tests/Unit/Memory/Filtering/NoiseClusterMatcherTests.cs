using AiRaccoon.Core.Memory.Filtering;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Memory.Filtering;

/// <summary>
///     Pure nearest-cluster lookup shared by the clustering service (which writes) and the
///     write-path policy (which only reads) — see ADR-0039.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class NoiseClusterMatcherTests
{
    private static NoiseCluster Cluster(string label, float[] centroid) =>
        new(Id: 0, ProjectId: "proj-1", UserId: null, ClusterLabel: label, SampleContent: "sample",
            Frequency: 1, Status: "candidate", CentroidEmbedding: centroid);

    [Fact]
    public void FindNearest_NoClusters_ReturnsNullClusterAndInfiniteDistance()
    {
        var (cluster, distance) = NoiseClusterMatcher.FindNearest([], [1f, 0f]);

        cluster.ShouldBeNull();
        distance.ShouldBe(double.PositiveInfinity);
    }

    [Fact]
    public void FindNearest_PicksTheClosestCentroidByCosineDistance()
    {
        var near = Cluster("near", [1f, 0f]);
        var far = Cluster("far", [0f, 1f]);

        var (cluster, distance) = NoiseClusterMatcher.FindNearest([far, near], [0.99f, 0.01f]);

        cluster.ShouldBe(near);
        distance.ShouldBeLessThan(0.1);
    }
}
