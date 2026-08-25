using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     `promotion_discards` and `search_quality` both grew without a reaper — 965 and 424 rows on the
///     live bank at review time, with no DELETE for either anywhere in src/ (2026-08-14 project-scope
///     review, data-access F5/F6). `noise_entries` already had one; this generalises it.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class RetentionReaperTests : IDisposable
{
    private const string ProjectId = "retention";
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-retention");
    private readonly SqliteConnectionFactory _factory;

    public RetentionReaperTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private static long DaysAgo(int days) => Now.AddDays(-days).ToUnixTimeSeconds();

    [RetryFact]
    public async Task PurgeOldDiscards_RemovesOnlyDiscardsPastRetentionWhoseEntryIsGone()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var store = new SqlitePromotionQueueStore(_factory, new FakeTimeProvider(Now));

        // An entry that still exists — its discard must survive at any age, or the candidate it
        // rejected is proposed again.
        await connection.ExecuteAsync(
            "INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at) " +
            "VALUES ('live', 'p.md', 'v', 'project', @p, @t, @t)", new { p = ProjectId, t = DaysAgo(400) });

        await connection.ExecuteAsync(
            "INSERT INTO promotion_discards (project_id, hash, discarded_at) VALUES " +
            "(@p, 'live', @old), (@p, 'orphan-old', @old), (@p, 'orphan-recent', @recent)",
            new { p = ProjectId, old = DaysAgo(400), recent = DaysAgo(10) });

        var purged = await store.PurgeOldDiscardsAsync(
            Now.ToUnixTimeSeconds(), retentionDays: 180, TestContext.Current.CancellationToken);

        purged.ShouldBe(1, "only the aged discard whose entry is gone may go");
        var remaining = (await connection.QueryAsync<string>(
            "SELECT hash FROM promotion_discards ORDER BY hash")).ToList();
        remaining.ShouldBe(["live", "orphan-recent"]);
    }

    [RetryFact]
    public async Task PurgeOldSearchQuality_RemovesOnlyRowsPastRetention()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var service = new SqliteSearchQualityService(_factory, Microsoft.Extensions.Logging.Abstractions.NullLogger<SqliteSearchQualityService>.Instance);

        await connection.ExecuteAsync(
            "INSERT INTO search_quality (correlation_id, query, scope, project_id, created_at) VALUES " +
            "('old', 'q', 'all', @p, @old), ('recent', 'q', 'all', @p, @recent)",
            new { p = ProjectId, old = DaysAgo(200), recent = DaysAgo(10) });

        var purged = await service.PurgeOlderThanAsync(
            Now.ToUnixTimeSeconds(), retentionDays: 90, TestContext.Current.CancellationToken);

        purged.ShouldBe(1);
        var remaining = (await connection.QueryAsync<string>(
            "SELECT correlation_id FROM search_quality")).ToList();
        remaining.ShouldBe(["recent"]);
    }
}
