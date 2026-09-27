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
///     #784: a bank whose ledger holds the pre-fix job's stamp must run the fixed repair again. The
///     runner's due-check reads <c>maintenance_jobs</c> by <see cref="IMaintenanceJob.Name" />, so the
///     retry is earned by giving the job a new name — the ledger row under the old name is simply
///     never found.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class NoteChunkOrderRepairJobLedgerTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("note-chunk-order-ledger");
    private readonly SqliteConnectionFactory _factory;

    public NoteChunkOrderRepairJobLedgerTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task ABankStampedWithTheOldJobName_RunsTheRenamedJobAgain()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(
            "INSERT INTO maintenance_jobs (name, last_run_at, run_count) VALUES ('note-chunk-order-v1', 0, 1)");
        var job = new NoteChunkOrderRepairJob(NullLogger<NoteChunkOrderRepairJob>.Instance);
        var runner = new MaintenanceJobRunner(new FakeTimeProvider(DateTimeOffset.UtcNow), new NoOpMeasurementRecorder(),
            NullLogger<MaintenanceJobRunner>.Instance);

        var outcomes = await runner.RunDueAsync(connection, [job], TestContext.Current.CancellationToken);

        outcomes.Single().Ran.ShouldBeTrue(
            "the ledger holds the old name; the runner finds no row for the new one and treats the job as never run");
        var runCount = await connection.ExecuteScalarAsync<long>(
            "SELECT run_count FROM maintenance_jobs WHERE name = @name", new { name = NoteChunkOrderRepairJob.JobName });
        runCount.ShouldBe(1);
    }
}
