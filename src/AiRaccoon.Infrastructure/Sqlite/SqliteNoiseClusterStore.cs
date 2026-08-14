using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Embedding;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Extensions.Logging;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>INoiseClusterStore over noise_clusters + vec_noise (ADR-0039) — the implementation the original subsystem never had.</summary>
public sealed partial class SqliteNoiseClusterStore(ISqliteConnectionFactory factory, ILogger<SqliteNoiseClusterStore> logger) : INoiseClusterStore
{
    public async Task<IReadOnlyList<NoiseCluster>> GetClustersAsync(string projectId, string? userId, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNullOrWhiteSpace(projectId);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<NoiseClusterRow>(
            new CommandDefinition(NoiseClusterSql.SelectByProjectAndUser,
                new { ProjectId = projectId, UserId = userId }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        return rows.Select(ToCluster).ToList();
    }

    public async Task<long> UpsertClusterAsync(NoiseCluster cluster, CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(cluster);

        await using var connection = await factory.OpenBankAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var blob = EmbeddingBlob.ToBytes(cluster.CentroidEmbedding);

        var id = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(NoiseClusterSql.Upsert,
                new
                {
                    cluster.ProjectId,
                    cluster.UserId,
                    cluster.ClusterLabel,
                    cluster.SampleContent,
                    cluster.Frequency,
                    cluster.Status,
                    CentroidEmbeddingBlob = blob,
                    Now = now
                }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(NoiseClusterSql.DeleteVecNoiseRow, new { Id = id }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(NoiseClusterSql.InsertVecNoiseRow,
                new { Id = id, Ctx = cluster.ProjectId, Embedding = blob }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        Log.ClusterUpserted(logger, cluster.ProjectId, cluster.ClusterLabel, id, cluster.Frequency);
        return id;
    }

    private static NoiseCluster ToCluster(NoiseClusterRow row) =>
        new(row.Id, row.ProjectId, row.UserId, row.ClusterLabel, row.SampleContent, row.Frequency, row.Status,
            EmbeddingBlob.ToFloats(row.CentroidEmbeddingBlob));

    private sealed class NoiseClusterRow
    {
        public long Id { get; set; }
        public string ProjectId { get; set; } = "";
        public string? UserId { get; set; }
        public string ClusterLabel { get; set; } = "";
        public string SampleContent { get; set; } = "";
        public int Frequency { get; set; }
        public string Status { get; set; } = "";
        public byte[] CentroidEmbeddingBlob { get; set; } = [];
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 950, Level = LogLevel.Debug,
            Message = "Noise cluster upserted for project {ProjectId}: {ClusterLabel} (id={ClusterId}, frequency={Frequency})")]
        public static partial void ClusterUpserted(ILogger logger, string projectId, string clusterLabel, long clusterId, int frequency);
    }
}
