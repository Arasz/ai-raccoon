using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AiRaccoon.Core.Memory.Filtering;

public record NoiseCandidate(
    string ProjectId,
    string? UserId,
    string SampleContent,
    float[] Vector);

public record NoiseCluster(
    long Id,
    string ProjectId,
    string? UserId,
    string ClusterLabel,
    string SampleContent,
    int Frequency,
    string Status,
    float[] CentroidEmbedding);

public interface INoiseClusterStore
{
    Task<IReadOnlyList<NoiseCluster>> GetClustersAsync(string projectId, string? userId, CancellationToken cancellationToken = default);
    Task<long> UpsertClusterAsync(NoiseCluster cluster, CancellationToken cancellationToken = default);
}
