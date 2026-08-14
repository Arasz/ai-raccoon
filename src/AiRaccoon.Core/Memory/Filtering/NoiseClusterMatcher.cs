namespace AiRaccoon.Core.Memory.Filtering;

/// <summary>Pure nearest-centroid lookup shared by the clustering writer and the write-path reader (ADR-0039).</summary>
public static class NoiseClusterMatcher
{
    public static (NoiseCluster? Cluster, double Distance) FindNearest(IReadOnlyList<NoiseCluster> clusters, float[] vector)
    {
        NoiseCluster? nearest = null;
        var minDistance = double.PositiveInfinity;

        foreach (var cluster in clusters)
        {
            var distance = ZeroShotEmbeddingFilter.CosineDistance(vector, cluster.CentroidEmbedding);
            if (distance < minDistance)
            {
                minDistance = distance;
                nearest = cluster;
            }
        }

        return (nearest, minDistance);
    }
}
