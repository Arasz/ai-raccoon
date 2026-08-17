using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     ADR-0075 amendment: the server side of <see cref="IPromotionQueuePruneStore" />, the same
///     report/request split <see cref="IRepairStore" /> already has. The report scans read-only,
///     exactly like `extract prune` used to run in the CLI process; the request writes the
///     promotion_queue_prune_requests outbox row instead of deleting anything itself.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqlitePromotionQueueStoreOutboxTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot = TestData.CreateTempRoot("promotion-queue-prune-outbox");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqlitePromotionQueueStore _store;

    public SqlitePromotionQueueStoreOutboxTests()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = new SqlitePromotionQueueStore(_factory, new FakeTimeProvider(FixedNow));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private static QueueCandidate Candidate(string hash) => new(hash, $"{hash}.md", "gone", null, 1.0, ["organic-write"]);

    [Fact]
    public async Task ReportPruneOrphansAsync_OnAnUnaffectedBank_ReportsNothing()
    {
        var report = await _store.ReportPruneOrphansAsync(TestContext.Current.CancellationToken);

        report.TotalOrphans.ShouldBe(0);
    }

    [Fact]
    public async Task ReportPruneOrphansAsync_FindsAnOrphan_AndNeverDeletesIt()
    {
        await _store.UpsertAsync("acme", [Candidate("orphan-1")], TestContext.Current.CancellationToken);

        var report = await _store.ReportPruneOrphansAsync(TestContext.Current.CancellationToken);

        report.TotalOrphans.ShouldBe(1);
        (await _store.ListAsync(null, TestContext.Current.CancellationToken)).Count.ShouldBe(1,
            "a report must never delete anything");
    }

    [Fact]
    public async Task RequestPruneOrphansAsync_InsertsAnOpenRequestRow()
    {
        await _store.RequestPruneOrphansAsync(TestContext.Current.CancellationToken);

        (await OpenRequestCountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task RequestPruneOrphansAsync_NeverDeletesTheQueueItself()
    {
        await _store.UpsertAsync("acme", [Candidate("orphan-1")], TestContext.Current.CancellationToken);

        await _store.RequestPruneOrphansAsync(TestContext.Current.CancellationToken);

        (await _store.ListAsync(null, TestContext.Current.CancellationToken)).Count.ShouldBe(1,
            "a request commits the outbox row; the actual delete is the maintenance job's job");
    }

    [Fact]
    public async Task RequestPruneOrphansAsync_CalledTwice_StaysOneRow()
    {
        await _store.RequestPruneOrphansAsync(TestContext.Current.CancellationToken);
        await _store.RequestPruneOrphansAsync(TestContext.Current.CancellationToken);

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        (await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM promotion_queue_prune_requests"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task RequestPruneOrphansAsync_AfterAPreviousRequestFinished_ReopensIt()
    {
        await _store.RequestPruneOrphansAsync(TestContext.Current.CancellationToken);
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await connection.ExecuteAsync("UPDATE promotion_queue_prune_requests SET finished_at = 1 WHERE id = 1");
        }

        await _store.RequestPruneOrphansAsync(TestContext.Current.CancellationToken);

        (await OpenRequestCountAsync()).ShouldBe(1);
    }

    private async Task<long> OpenRequestCountAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        return await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM promotion_queue_prune_requests WHERE id = 1 AND finished_at IS NULL");
    }
}
