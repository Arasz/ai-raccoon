using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     The `metrics` table's reaper (docs/plans/2026-08-15-performance-metrics-implementation.md,
///     WP4), re-cast as an <see cref="IMaintenanceJob" /> per ADR-0070 — the plan's original shape
///     (a purge inline in <c>BankMaintenanceHostedService</c>, alongside <c>PurgeExpiredRetentionAsync</c>)
///     no longer exists. <c>promotion_discards</c> (965 rows) and <c>search_quality</c> (424) both
///     grew unreaped until ADR-0055; a per-measurement table grows far faster than either.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MetricsRetentionJobTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("metrics-retention-job");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeTimeProvider _time = new(Now);

    public MetricsRetentionJobTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private static long DaysAgo(int days) => Now.AddDays(-days).ToUnixTimeSeconds();

    /// <summary>
    ///     G3 — the reaper deletes past the window and nothing inside it. Both directions asserted
    ///     on the surviving rows, not just a count (ADR-0055's reaper gate).
    /// </summary>
    [RetryFact]
    public async Task RunAsync_DeletesRowsOlderThanTheWindow_AndKeepsRowsInsideIt()
    {
        await using var connection = await OpenAsync();
        await SeedMetricAsync(connection, "outside-window", DaysAgo(29));
        await SeedMetricAsync(connection, "inside-window", DaysAgo(27));

        await Runner().RunDueAsync(connection, [new MetricsRetentionJob(_time)], TestContext.Current.CancellationToken);

        var survivors = await SurvivorsAsync(connection);
        survivors.ShouldBe(["inside-window"]);
    }

    /// <summary>
    ///     Finding 4: the reaper deletes on recorded_at, but the only two indexes on `metrics` lead
    ///     with name/project_id, not recorded_at — so every reap was a full-table SCAN. Asserted via
    ///     the same EXPLAIN QUERY PLAN the SQLite planner actually uses, not an assumption about it.
    /// </summary>
    [RetryFact]
    public async Task RunAsync_UsesAnIndexOnRecordedAt_NotAFullTableScan()
    {
        await using var connection = await OpenAsync();

        var rows = await connection.QueryAsync<dynamic>(
            "EXPLAIN QUERY PLAN DELETE FROM metrics WHERE recorded_at < @cutoff", new { cutoff = DaysAgo(28) });
        var plan = string.Join(" | ", rows.Select(r => (string)r.detail));

        plan.ShouldNotContain("SCAN metrics", Case.Sensitive,
            $"the reaper's DELETE must use an index on recorded_at, not a full-table scan; actual plan: {plan}");
    }

    /// <summary>AC1 — the reaper runs as a registered job and is stamped in the ledger like the others.</summary>
    [RetryFact]
    public async Task TheJob_AppearsInTheMaintenanceLedgerAfterRunning()
    {
        await using var connection = await OpenAsync();

        await Runner().RunDueAsync(connection, [new MetricsRetentionJob(_time)], TestContext.Current.CancellationToken);

        var runCount = await connection.ExecuteScalarAsync<long?>(
            "SELECT run_count FROM maintenance_jobs WHERE name = @name", new { name = MetricsRetentionJob.JobName });
        runCount.ShouldBe(1);
    }

    /// <summary>AC3 — an invalid retention setting falls back to the default rather than throwing.</summary>
    [RetryFact]
    public async Task AnInvalidRetentionSetting_FallsBackToTheDefault()
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync("INSERT OR REPLACE INTO settings (key, value) VALUES (@key, 'not-a-number')",
            new { key = MetricsConfigKeys.RetentionDaysGlobal });
        // Default is 28 days: one row just past it, one row just inside it.
        await SeedMetricAsync(connection, "outside-default", DaysAgo(29));
        await SeedMetricAsync(connection, "inside-default", DaysAgo(27));

        // No throw is itself the assertion: an exception here would fail the test before the next line runs.
        await Runner().RunDueAsync(connection, [new MetricsRetentionJob(_time)], TestContext.Current.CancellationToken);

        var survivors = await SurvivorsAsync(connection);
        survivors.ShouldBe(["inside-default"], "an unparsable setting must fall back to the 28-day default, not throw");
    }

    /// <summary>AC4 — a reaper failure never fails the maintenance pass, and does not stamp the ledger.</summary>
    [RetryFact]
    public async Task AFailingReaper_DoesNotFailThePass_AndIsNotStamped()
    {
        await using var connection = await OpenAsync();
        // A schema the reaper cannot work against — a realistic failure mode, not a fabricated one.
        await connection.ExecuteAsync("DROP TABLE metrics");

        // No throw is itself the assertion: RunDueAsync propagating here would fail the test.
        var outcomes = await Runner()
            .RunDueAsync(connection, [new MetricsRetentionJob(_time)], TestContext.Current.CancellationToken);

        outcomes.Single().Ran.ShouldBeFalse();
        outcomes.Single().Error.ShouldNotBeNull();
        var stamped = await connection.ExecuteScalarAsync<long?>(
            "SELECT run_count FROM maintenance_jobs WHERE name = @name", new { name = MetricsRetentionJob.JobName });
        stamped.ShouldBeNull("an unstamped failure retries on the next pass");
    }

    private MaintenanceJobRunner Runner() => new(_time, new NoOpMeasurementRecorder(), NullLogger<MaintenanceJobRunner>.Instance);

    private async Task<SqliteConnection> OpenAsync() => await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

    private static async Task<List<string>> SurvivorsAsync(SqliteConnection connection) => [.. await connection.QueryAsync<string>("SELECT name FROM metrics ORDER BY name")];

    private static async Task SeedMetricAsync(SqliteConnection connection, string name, long recordedAt) =>
        await connection.ExecuteAsync(
            "INSERT INTO metrics (name, kind, value, unit, recorded_at) VALUES (@name, 'counter', 1, 'ms', @recordedAt)",
            new { name, recordedAt });
}
