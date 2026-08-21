using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     ADR-0075 amendment: ChunkIndexRepairJob is the relay half of the repair outbox — on-demand
///     (<see cref="IMaintenanceJob.Interval" /> is null), it only ever runs because a CLI-submitted
///     repair_requests row exists, never on a clock. It applies the same
///     <see cref="ChunkIndexRepair" /> logic `repair chunk-index --apply` used to run in the CLI
///     process.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ChunkIndexRepairJobTests : IDisposable
{
    private const string ProjectId = "acme";
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot = TestData.CreateTempRoot("chunk-index-repair-job");
    private readonly SqliteConnectionFactory _factory;

    public ChunkIndexRepairJobTests()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private static ChunkIndexRepairJob NewJob() =>
        new(new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]), TestData.CreateEmbeddingService(),
            new FakeTimeProvider(FixedNow));

    [Fact]
    public async Task HasWorkAsync_WithNoOpenRequest_IsFalse()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        (await NewJob().HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task HasWorkAsync_AfterARequest_IsTrue()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await RequestRepairAsync(connection);

        (await NewJob().HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_WithNoOpenRequest_IsANoOp()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        await Should.NotThrowAsync(() => NewJob().RunAsync(connection, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RunAsync_RepositionsTheRows_AndMarksTheRequestFinished()
    {
        var file = Path.Combine(_dataRoot, "doc.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync(
            (file, "para one", 0), (file, "para three", 1), (file, "para two", 2));
        await RequestRepairAsync(connection);

        await NewJob().RunAsync(connection, TestContext.Current.CancellationToken);

        var positions = await PositionsByValueAsync(connection, file);
        positions["para two"].ShouldBe(1);
        positions["para three"].ShouldBe(2);
        (await connection.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM repair_requests WHERE kind = 'chunk-index' AND finished_at IS NOT NULL"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task HasWorkAsync_AfterRunAsync_IsFalseAgain()
    {
        var file = Path.Combine(_dataRoot, "doc.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two", TestContext.Current.CancellationToken);
        await using var connection = await OpenSeededAsync((file, "para one", 0), (file, "para two", 5));
        await RequestRepairAsync(connection);
        var job = NewJob();
        await job.RunAsync(connection, TestContext.Current.CancellationToken);

        (await job.HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    private static Task RequestRepairAsync(SqliteConnection connection) =>
        connection.ExecuteAsync(MemorySql.RequestRepair,
            new { kind = RepairKinds.ChunkIndex, requestedAt = FixedNow.ToUnixTimeSeconds() });

    private static async Task<Dictionary<string, long>> PositionsByValueAsync(SqliteConnection connection, string sourceFile) =>
        (await connection.QueryAsync<(string Value, long ChunkIndex)>(
                "SELECT value AS Value, chunk_index AS ChunkIndex FROM entries WHERE source_file = @sourceFile",
                new { sourceFile }))
            .ToDictionary(r => r.Value, r => r.ChunkIndex, StringComparer.Ordinal);

    private async Task<SqliteConnection> OpenSeededAsync(params (string SourceFile, string Value, int ChunkIndex)[] rows)
    {
        var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        foreach (var (sourceFile, value, chunkIndex) in rows)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO entries (hash, path, value, source_file, scope, project_id,
                                     created_at, updated_at, embed_state, chunk_index, total_chunks)
                VALUES (@hash, @path, @value, @sourceFile, 'project', @projectId, 1, 1, 'embedded', @chunkIndex, @totalChunks)
                """,
                new
                {
                    hash = ContentHash.Of(sourceFile, value),
                    path = sourceFile, value, sourceFile, projectId = ProjectId, chunkIndex, totalChunks = rows.Length
                });
        }

        return connection;
    }
}
