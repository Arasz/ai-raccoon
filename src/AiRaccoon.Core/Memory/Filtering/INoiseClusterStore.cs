namespace AiRaccoon.Core.Memory.Filtering;

/// <summary>One content sample offered to the clustering service — not yet embedded into a cluster.</summary>
public sealed record NoiseCandidate(
    string ProjectId,
    string? UserId,
    string SampleContent,
    float[] Vector);

/// <summary>
///     A learned noise region: a running centroid over content the pipeline has seen labelled
///     noise. <see cref="Status" /> is one of "candidate" (never rejects writes), "active" (may
///     reject writes — see <c>LearnedNoiseClusterPolicy</c>) or "suppressed". Nothing in this build
///     promotes candidate to active (see docs/adr/0039) — that is deliberately left to whatever
///     governance the research lane's verdict calls for.
/// </summary>
public sealed record NoiseCluster(
    long Id,
    string ProjectId,
    string? UserId,
    string ClusterLabel,
    string SampleContent,
    int Frequency,
    string Status,
    float[] CentroidEmbedding);

/// <summary>Persistence for learned noise clusters, scoped per (project, user). See docs/adr/0039.</summary>
public interface INoiseClusterStore
{
    Task<IReadOnlyList<NoiseCluster>> GetClustersAsync(string projectId, string? userId, CancellationToken cancellationToken = default);

    Task<long> UpsertClusterAsync(NoiseCluster cluster, CancellationToken cancellationToken = default);
}
