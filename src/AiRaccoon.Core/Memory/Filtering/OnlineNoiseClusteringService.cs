using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AiRaccoon.Core.Memory;

namespace AiRaccoon.Core.Memory.Filtering;

public sealed class OnlineNoiseClusteringService(INoiseClusterStore store, float clusterDistanceThreshold = 0.12f)
{
    public async Task<long> ProcessCandidateAsync(NoiseCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var existingClusters = await store.GetClustersAsync(candidate.ProjectId, candidate.UserId, cancellationToken).ConfigureAwait(false);

        NoiseCluster? nearestCluster = null;
        double minDistance = double.MaxValue;

        foreach (var cluster in existingClusters)
        {
            var distance = ZeroShotEmbeddingFilter.CosineDistance(candidate.Vector, cluster.CentroidEmbedding);
            if (distance < minDistance)
            {
                minDistance = distance;
                nearestCluster = cluster;
            }
        }

        if (nearestCluster is not null && minDistance < clusterDistanceThreshold)
        {
            // Assign to existing cluster & update running centroid
            var updatedFrequency = nearestCluster.Frequency + 1;
            var updatedCentroid = ComputeRunningCentroid(nearestCluster.CentroidEmbedding, candidate.Vector, nearestCluster.Frequency);

            var updatedCluster = nearestCluster with
            {
                Frequency = updatedFrequency,
                CentroidEmbedding = updatedCentroid
            };

            return await store.UpsertClusterAsync(updatedCluster, cancellationToken).ConfigureAwait(false);
        }

        // Spawn new candidate cluster
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

        for (int i = 0; i < currentCentroid.Length; i++)
        {
            updated[i] = (float)((currentCentroid[i] * currentWeight + newVector[i]) / totalWeight);
        }

        // L2 normalize
        double norm = 0;
        for (int i = 0; i < updated.Length; i++)
        {
            norm += updated[i] * updated[i];
        }

        norm = Math.Sqrt(norm);
        if (norm > 0)
        {
            for (int i = 0; i < updated.Length; i++)
            {
                updated[i] = (float)(updated[i] / norm);
            }
        }

        return updated;
    }
}
