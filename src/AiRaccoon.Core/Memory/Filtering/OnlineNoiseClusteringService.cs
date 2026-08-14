using CommunityToolkit.Diagnostics;

namespace AiRaccoon.Core.Memory.Filtering;

/// <summary>
///     Leader-follower online clustering (ADR-0039): assign a candidate to the nearest existing
///     centroid within <c>clusterDistanceThreshold</c>, updating a running mean; otherwise spawn a
///     new candidate cluster. The threshold is a caller-supplied parameter, not a constructor
///     default, so it can be read fresh from settings (<see cref="NoiseConfigKeys.LearnerClusterDistanceThresholdGlobal" />)
///     on every call rather than fixed at DI-graph build time.
/// </summary>
public sealed class OnlineNoiseClusteringService(INoiseClusterStore store)
{
    public async Task<long> ProcessCandidateAsync(NoiseCandidate candidate, double clusterDistanceThreshold, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(candidate);

        var existingClusters = await store.GetClustersAsync(candidate.ProjectId, candidate.UserId, cancellationToken).ConfigureAwait(false);
        var (nearestCluster, distance) = NoiseClusterMatcher.FindNearest(existingClusters, candidate.Vector);

        if (nearestCluster is not null && distance < clusterDistanceThreshold)
        {
            var updatedCluster = nearestCluster with
            {
                Frequency = nearestCluster.Frequency + 1,
                CentroidEmbedding = ComputeRunningCentroid(nearestCluster.CentroidEmbedding, candidate.Vector, nearestCluster.Frequency)
            };

            return await store.UpsertClusterAsync(updatedCluster, cancellationToken).ConfigureAwait(false);
        }

        var label = "noise_cluster_" + Guid.NewGuid().ToString("N")[..8];
        var newCluster = new NoiseCluster(
            Id: 0,
            ProjectId: candidate.ProjectId,
            UserId: candidate.UserId,
            ClusterLabel: label,
            SampleContent: candidate.SampleContent,
            Frequency: 1,
            Status: "candidate",
            CentroidEmbedding: candidate.Vector);

        return await store.UpsertClusterAsync(newCluster, cancellationToken).ConfigureAwait(false);
    }

    private static float[] ComputeRunningCentroid(float[] currentCentroid, float[] newVector, int currentWeight)
    {
        var updated = new float[currentCentroid.Length];
        double totalWeight = currentWeight + 1;

        for (var i = 0; i < currentCentroid.Length; i++)
        {
            updated[i] = (float)((currentCentroid[i] * currentWeight + newVector[i]) / totalWeight);
        }

        double norm = 0;
        for (var i = 0; i < updated.Length; i++)
        {
            norm += updated[i] * updated[i];
        }

        norm = Math.Sqrt(norm);
        if (norm > 0)
        {
            for (var i = 0; i < updated.Length; i++)
            {
                updated[i] = (float)(updated[i] / norm);
            }
        }

        return updated;
    }
}
