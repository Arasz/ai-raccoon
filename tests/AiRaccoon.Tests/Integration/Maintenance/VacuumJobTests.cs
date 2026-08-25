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
///     The vacuum contracts, re-homed from <c>BankMaintenanceHostedServiceRunOnceTests</c> when
///     vacuum became a job (ADR-0070). Three survived the move because they were the contract:
///     VACUUM+ANALYZE runs, a contended bank defers and retries, and an explicitly configured
///     interval is honoured.
///     <para>
///         One did not. <c>RunOnce_VacuumNotDue_OnFirstTick_SeedsTheClock</c> asserted that the first
///         pass deliberately does nothing — which is the defect ADR-0070 removes, not a contract. A
///         bank only ever opened by short-lived processes never got past that seeding pass, so 42 MB
///         of free pages on a real 183 MB bank were unreachable forever. It is deleted, not ported.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class VacuumJobTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("vacuum-job");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeTimeProvider _time = new(Start);

    public VacuumJobTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>The behaviour that replaces the seeding pass: a bank that has never vacuumed does so now.</summary>
    [RetryFact]
    public async Task TheFirstPass_Vacuums_RatherThanSeedingAClock()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await SeedFreelistAsync(connection);
        (await FreelistAsync(connection)).ShouldBeGreaterThan(0, "the fixture must create free pages, or this proves nothing");

        await Runner().RunDueAsync(connection, [new VacuumJob()], TestContext.Current.CancellationToken);

        (await FreelistAsync(connection)).ShouldBe(0);
    }

    /// <summary>VACUUM drops sqlite_stat1, so a job that skipped ANALYZE would leave the planner blind.</summary>
    [RetryFact]
    public async Task Vacuum_LeavesTheAnalyzeStatisticsInPlace()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await SeedFreelistAsync(connection);
        // ANALYZE writes no statistics for an empty table, and SeedFreelistAsync deletes everything
        // it inserts — so a surviving row is what makes this assertion able to fail at all.
        await connection.ExecuteAsync(
            """
            INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
            VALUES ('keep', 'keep.md', 'kept', 'project', 'other', 0, 0)
            """);

        await Runner().RunDueAsync(connection, [new VacuumJob()], TestContext.Current.CancellationToken);

        var stat = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM sqlite_master WHERE name = 'sqlite_stat1'");
        stat.ShouldBe(1);
    }

    /// <summary>An explicitly configured interval still wins; only the DEFAULT moved 7 days → 2 hours.</summary>
    [RetryFact]
    public async Task AConfiguredIntervalInDays_IsHonoured()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("INSERT OR REPLACE INTO settings (key, value) VALUES (@key, '1')",
            new { key = BankMaintenanceConfigKeys.VacuumIntervalDaysGlobal });
        var job = new VacuumJob();
        await Runner().RunDueAsync(connection, [job], TestContext.Current.CancellationToken);

        // Asserted on the ledger rather than on freelist_count: page accounting depends on when the
        // WAL checkpoints, which is not what this test is about. run_count says whether it ran.
        _time.Advance(TimeSpan.FromHours(3));
        await Runner().RunDueAsync(connection, [job], TestContext.Current.CancellationToken);
        (await RunCountAsync(connection)).ShouldBe(1,
            "three hours is past the two-hour DEFAULT but inside the configured one day");

        _time.Advance(TimeSpan.FromDays(1));
        await Runner().RunDueAsync(connection, [job], TestContext.Current.CancellationToken);
        (await RunCountAsync(connection)).ShouldBe(2);
    }

    /// <summary>Without configuration the cadence is two hours, not the old seven days.</summary>
    [RetryFact]
    public async Task TheDefaultCadence_IsTwoHours()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var job = new VacuumJob();
        var first = await Runner().RunDueAsync(connection, [job], TestContext.Current.CancellationToken);
        first.Single().Error.ShouldBeNull("the job must actually run — a bare 'VACUUM' reaches Dapper as a stored-procedure name");

        _time.Advance(TimeSpan.FromHours(2));
        await Runner().RunDueAsync(connection, [job], TestContext.Current.CancellationToken);

        (await RunCountAsync(connection)).ShouldBe(2, "two hours must be enough; seven days was the old default");
    }

    private MaintenanceJobRunner Runner() => new(_time, new NoOpMeasurementRecorder(), NullLogger<MaintenanceJobRunner>.Instance);

    private static async Task<long> RunCountAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<long>(
            "SELECT run_count FROM maintenance_jobs WHERE name = @name", new { name = VacuumJob.JobName });

    private static async Task<long> FreelistAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<long>("PRAGMA freelist_count");

    /// <summary>Writes then deletes rows so the free list grows.</summary>
    private static async Task SeedFreelistAsync(SqliteConnection connection)
    {
        for (var i = 0; i < 2000; i++)
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                VALUES (@hash, @path, @value, 'project', 'acme', 0, 0)
                """,
                new { hash = $"h{i}", path = $"p{i}.md", value = new string('x', 200) });
        }

        await connection.ExecuteAsync("DELETE FROM entries WHERE project_id = 'acme'");
    }
}
