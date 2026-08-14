using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     Persistence contract for noise_clusters/vec_noise (ADR-0039): schema presence on a fresh
///     bank, round-trip create/update, and per-project retrieval. Against a real scratch bank, not a
///     fake — the round-trip is the acceptance evidence for INoiseClusterStore having a working
///     implementation.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteNoiseClusterStoreTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-noise-cluster-store");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteNoiseClusterStore _store;

    public SqliteNoiseClusterStoreTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = new SqliteNoiseClusterStore(_factory, NullLogger<SqliteNoiseClusterStore>.Instance);
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private static NoiseCluster Candidate(string projectId, string? userId, string label, float[] centroidHead, string status = "candidate") =>
        new(Id: 0, ProjectId: projectId, UserId: userId, ClusterLabel: label, SampleContent: "sample",
            Frequency: 1, Status: status, CentroidEmbedding: Vector384(centroidHead));

    /// <summary>vec_noise is declared float[384] (matching the bundled MiniLM dimension) — pads a short, readable test vector out to the schema's required length.</summary>
    private static float[] Vector384(float[] head)
    {
        var vector = new float[384];
        head.CopyTo(vector, 0);
        return vector;
    }

    [Fact]
    public async Task FreshBank_CreatesNoiseClusterSchema()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var table = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'noise_clusters'");
        table.ShouldBe(1);

        var vecTable = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE name = 'vec_noise'");
        vecTable.ShouldBe(1);
    }

    [Fact]
    public async Task UpsertThenGet_RoundTripsANewCluster()
    {
        var id = await _store.UpsertClusterAsync(
            Candidate("proj-1", "agent-1", "noise_cluster_abc", [1f, 0f, 0f]),
            TestContext.Current.CancellationToken);

        id.ShouldBeGreaterThan(0);

        var clusters = await _store.GetClustersAsync("proj-1", "agent-1", TestContext.Current.CancellationToken);

        clusters.Count.ShouldBe(1);
        clusters[0].Id.ShouldBe(id);
        clusters[0].ClusterLabel.ShouldBe("noise_cluster_abc");
        clusters[0].Status.ShouldBe("candidate");
        clusters[0].Frequency.ShouldBe(1);
        clusters[0].CentroidEmbedding.ShouldBe(Vector384([1f, 0f, 0f]));
    }

    [Fact]
    public async Task Upsert_SameProjectAndLabel_UpdatesTheCentroidInsteadOfDuplicating()
    {
        var id = await _store.UpsertClusterAsync(
            Candidate("proj-1", "agent-1", "noise_cluster_abc", [1f, 0f, 0f]),
            TestContext.Current.CancellationToken);

        var updatedId = await _store.UpsertClusterAsync(
            new NoiseCluster(id, "proj-1", "agent-1", "noise_cluster_abc", "sample updated", 2, "active", Vector384([0.9f, 0.1f, 0f])),
            TestContext.Current.CancellationToken);

        updatedId.ShouldBe(id);

        var clusters = await _store.GetClustersAsync("proj-1", "agent-1", TestContext.Current.CancellationToken);

        clusters.Count.ShouldBe(1, "the second upsert must update the existing row, not add a second one");
        clusters[0].Frequency.ShouldBe(2);
        clusters[0].Status.ShouldBe("active");
        clusters[0].CentroidEmbedding.ShouldBe(Vector384([0.9f, 0.1f, 0f]));
    }

    [Fact]
    public async Task GetClusters_IsScopedPerProject()
    {
        await _store.UpsertClusterAsync(Candidate("proj-1", null, "noise_cluster_a", [1f, 0f]), TestContext.Current.CancellationToken);
        await _store.UpsertClusterAsync(Candidate("proj-2", null, "noise_cluster_b", [0f, 1f]), TestContext.Current.CancellationToken);

        var proj1Clusters = await _store.GetClustersAsync("proj-1", null, TestContext.Current.CancellationToken);
        var proj2Clusters = await _store.GetClustersAsync("proj-2", null, TestContext.Current.CancellationToken);

        proj1Clusters.Count.ShouldBe(1);
        proj1Clusters[0].ClusterLabel.ShouldBe("noise_cluster_a");
        proj2Clusters.Count.ShouldBe(1);
        proj2Clusters[0].ClusterLabel.ShouldBe("noise_cluster_b");
    }

    [Fact]
    public async Task GetClusters_NullUserId_DoesNotMatchAClusterWithAnExplicitUserId()
    {
        await _store.UpsertClusterAsync(Candidate("proj-1", "agent-1", "noise_cluster_a", [1f, 0f]), TestContext.Current.CancellationToken);
        await _store.UpsertClusterAsync(Candidate("proj-1", null, "noise_cluster_b", [0f, 1f]), TestContext.Current.CancellationToken);

        var forNullUser = await _store.GetClustersAsync("proj-1", null, TestContext.Current.CancellationToken);
        var forAgent1 = await _store.GetClustersAsync("proj-1", "agent-1", TestContext.Current.CancellationToken);

        forNullUser.Count.ShouldBe(1);
        forNullUser[0].ClusterLabel.ShouldBe("noise_cluster_b");
        forAgent1.Count.ShouldBe(1);
        forAgent1[0].ClusterLabel.ShouldBe("noise_cluster_a");
    }
}
