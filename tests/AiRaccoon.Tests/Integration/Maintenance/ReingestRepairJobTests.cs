using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     ADR-0075 amendment: ReingestRepairJob is the relay half of the repair outbox — on-demand
///     (<see cref="IMaintenanceJob.Interval" /> is null), it only ever runs because a CLI-submitted
///     repair_requests row exists, never on a clock. It applies the same
///     <see cref="ReingestRepair" /> logic `repair reingest --apply` used to run in the CLI process.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ReingestRepairJobTests : IDisposable
{
    private const string ProjectId = "acme";
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot = TestData.CreateTempRoot("reingest-repair-job");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMemoryStore _memoryStore;

    public ReingestRepairJobTests()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _memoryStore = TestData.CreateMemoryStore(_factory, NullLogger<SqliteMemoryStore>.Instance,
            new SqliteMemorySourceStore(_factory), new StubChunker(), new FakeTimeProvider(FixedNow),
            TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private ReingestRepairJob NewJob() =>
        new(new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]), TestData.CreateEmbeddingService(), _memoryStore,
            new FakeTimeProvider(FixedNow));

    [RetryFact]
    public async Task HasWorkAsync_WithNoOpenRequest_IsFalse()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        (await NewJob().HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [RetryFact]
    public async Task HasWorkAsync_AfterARequest_IsTrue()
    {
        await RequestRepairAsync();
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        (await NewJob().HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [RetryFact]
    public async Task RunAsync_WithNoOpenRequest_IsANoOp() =>
        await Should.NotThrowAsync(async () =>
        {
            await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
            await NewJob().RunAsync(connection, TestContext.Current.CancellationToken);
        });

    [RetryFact]
    public async Task RunAsync_AppliesTheRepair_AndMarksTheRequestFinished()
    {
        var staleHash = await SeedStaleFileAsync();
        await RequestRepairAsync();

        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await NewJob().RunAsync(connection, TestContext.Current.CancellationToken);
        }

        await using var verify = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        // The stale row was deleted and replaced by ReplaceAsync's atomic reingest — its hash is gone.
        (await verify.ExecuteScalarAsync<long>("SELECT count(*) FROM entries WHERE hash = @staleHash", new { staleHash }))
            .ShouldBe(0);
        (await verify.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM repair_requests WHERE kind = 'reingest' AND finished_at IS NOT NULL"))
            .ShouldBe(1);
    }

    [RetryFact]
    public async Task HasWorkAsync_AfterRunAsync_IsFalseAgain()
    {
        await SeedStaleFileAsync();
        await RequestRepairAsync();
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var job = NewJob();
        await job.RunAsync(connection, TestContext.Current.CancellationToken);

        (await job.HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    private async Task RequestRepairAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(MemorySql.RequestRepair,
            new { kind = RepairKinds.Reingest, requestedAt = FixedNow.ToUnixTimeSeconds() });
    }

    /// <summary>Seeds one ingested file, then corrupts its hash the way a chunker-version change would — returns the corrupted hash so a caller can assert it is gone after the repair.</summary>
    private async Task<string> SeedStaleFileAsync()
    {
        var file = Path.Combine(_dataRoot, "stale.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two", TestContext.Current.CancellationToken);
        await _memoryStore.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]),
            TestContext.Current.CancellationToken);
        await _memoryStore.IngestFileAsync(ProjectId, file, null, TestContext.Current.CancellationToken);

        var staleHash = "stale-" + Guid.NewGuid().ToString("N");
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var id = await connection.ExecuteScalarAsync<long>(
            "SELECT id FROM entries WHERE source_file = @file ORDER BY id LIMIT 1", new { file });
        await connection.ExecuteAsync(
            "UPDATE entries SET hash = @staleHash, chunk_index = -1 WHERE id = @id",
            new { staleHash, id });
        return staleHash;
    }
}
