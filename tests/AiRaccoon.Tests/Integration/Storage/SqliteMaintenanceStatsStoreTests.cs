using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     ADR-0075 amendment: the server side of <see cref="IMaintenanceStatsStore" /> — the same
///     page_size/page_count/freelist_count pragmas and PASSIVE checkpoint `settings maintenance
///     list` used to read locally before this change, only the process that reads them moved.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteMaintenanceStatsStoreTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("maintenance-stats-store");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteMaintenanceStatsStore _store;

    public SqliteMaintenanceStatsStoreTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = new SqliteMaintenanceStatsStore(_factory);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task GetStatsAsync_OnAFreshBank_ReportsNonZeroDbBytes()
    {
        var stats = await _store.GetStatsAsync(TestContext.Current.CancellationToken);

        stats.DbBytes.ShouldBeGreaterThan(0);
    }

    [RetryFact]
    public async Task GetStatsAsync_AfterDeletingRows_ReportsAFreelist()
    {
        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                                  INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                                  VALUES (@hash, @path, @value, 'project', 'acme', 0, 0)
                                  """;
            var hash = insert.Parameters.Add("@hash", SqliteType.Text);
            var path = insert.Parameters.Add("@path", SqliteType.Text);
            var value = insert.Parameters.Add("@value", SqliteType.Text);
            for (var i = 0; i < 2000; i++)
            {
                hash.Value = $"h{i}";
                path.Value = $"p{i}.md";
                value.Value = new string('x', 200);
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM entries WHERE project_id = 'acme'";
            await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var stats = await _store.GetStatsAsync(TestContext.Current.CancellationToken);

        stats.ReclaimableBytes.ShouldBeGreaterThan(0);
    }

    [RetryFact]
    public async Task GetStatsAsync_NeverWritesEntries()
    {
        var before = await BankContent.SnapshotAsync(_factory, TestContext.Current.CancellationToken);

        await _store.GetStatsAsync(TestContext.Current.CancellationToken);

        var after = await BankContent.SnapshotAsync(_factory, TestContext.Current.CancellationToken);
        BankContent.Changed(before, after).ShouldNotContain("entries");
    }
}
