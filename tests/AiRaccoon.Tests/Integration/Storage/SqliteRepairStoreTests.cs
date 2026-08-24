using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     ADR-0075 amendment: the server side of <see cref="IRepairStore" />. Reports scan the bank
///     read-only, exactly like the CLI used to do locally before this change — only the process that
///     does it moved. Requests write the repair_requests outbox row, mirroring model_migration's
///     shape (ADR-0076) but keyed by kind since chunk-index and reingest repairs are independent.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteRepairStoreTests : IDisposable
{
    private const string ProjectId = "acme";
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot = TestData.CreateTempRoot("repair-store");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _memoryStore;
    private readonly SqliteRepairStore _store;

    public SqliteRepairStoreTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _memoryStore = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), new StubChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
        var matcher = new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]);
        _store = new SqliteRepairStore(_factory, matcher, TestData.CreateEmbeddingService(), _memoryStore, new FakeTimeProvider(FixedNow));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task ReportReingestAsync_NeverWrites_OnAnUnaffectedBank()
    {
        var report = await _store.ReportReingestAsync(TestContext.Current.CancellationToken);

        report.FilesToReingest.ShouldBe(0);
    }

    [Fact]
    public async Task ReportChunkIndexAsync_NeverWrites_OnAnUnaffectedBank()
    {
        var report = await _store.ReportChunkIndexAsync(TestContext.Current.CancellationToken);

        report.RowsRepositioned.ShouldBe(0);
    }

    [Fact]
    public async Task ReportReingestAsync_FindsAFileWithAStaleHash()
    {
        var file = Path.Combine(_dataRoot, "stale.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two", TestContext.Current.CancellationToken);
        await _memoryStore.SetSettingAsync(IngestScopeKeys.ScopeGlobal,
            IngestScopeKeys.Serialize([_dataRoot]), TestContext.Current.CancellationToken);
        await _memoryStore.IngestFileAsync(ProjectId, file, null, TestContext.Current.CancellationToken);
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            var id = await connection.ExecuteScalarAsync<long>(
                "SELECT id FROM entries WHERE source_file = @file ORDER BY id LIMIT 1", new { file });
            await connection.ExecuteAsync(
                "UPDATE entries SET hash = @staleHash, chunk_index = -1 WHERE id = @id",
                new { staleHash = "stale-" + Guid.NewGuid().ToString("N"), id });
        }

        var report = await _store.ReportReingestAsync(TestContext.Current.CancellationToken);

        report.FilesToReingest.ShouldBe(1);
    }

    [Fact]
    public async Task RequestRepairAsync_InsertsAnOpenRequestRow()
    {
        await _store.RequestRepairAsync(RepairKind.Reingest, TestContext.Current.CancellationToken);

        (await OpenRequestCountAsync("reingest")).ShouldBe(1);
    }

    [Fact]
    public async Task RequestRepairAsync_IsScopedToItsOwnKind()
    {
        await _store.RequestRepairAsync(RepairKind.Reingest, TestContext.Current.CancellationToken);

        (await OpenRequestCountAsync("chunk-index")).ShouldBe(0);
    }

    [Fact]
    public async Task RequestRepairAsync_CalledTwice_StaysOneRow()
    {
        await _store.RequestRepairAsync(RepairKind.Reingest, TestContext.Current.CancellationToken);
        await _store.RequestRepairAsync(RepairKind.Reingest, TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM repair_requests WHERE kind = 'reingest'"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task RequestRepairAsync_AfterAPreviousRequestFinished_ReopensIt()
    {
        await _store.RequestRepairAsync(RepairKind.ChunkIndex, TestContext.Current.CancellationToken);
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync(
                "UPDATE repair_requests SET finished_at = 1 WHERE kind = 'chunk-index'");
        }

        await _store.RequestRepairAsync(RepairKind.ChunkIndex, TestContext.Current.CancellationToken);

        (await OpenRequestCountAsync("chunk-index")).ShouldBe(1);
    }

    private async Task<long> OpenRequestCountAsync(string kind)
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM repair_requests WHERE kind = @kind AND finished_at IS NULL", new { kind });
    }
}
