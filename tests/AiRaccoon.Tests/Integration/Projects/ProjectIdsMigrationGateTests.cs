using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Projects;

/// <summary>
///     Air-merge P3's mechanical gate (review M1): enforcement is conditional on the P2
///     repair_requests finished row for kind=project-ids — never the maintenance_jobs ledger
///     stamp (the runner writes that after every RunAsync call, including gated no-ops).
///     <para>
///         Honesty ledger (mutation : filter : fixture): finished-NULL-check :
///         --filter IsMigratedAsync_WithOpenRequest_IsFalse : requested bank; skip-kind-predicate :
///         --filter IsMigratedAsync_WithFinishedRequest_IsTrue : finished bank; ignore-reopen :
///         --filter IsMigratedAsync_AfterASecondRequest_ReopensToFalse : reopened bank.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectIdsMigrationGateTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("project-ids-migration-gate");
    private readonly SqliteConnectionFactory _factory;

    public ProjectIdsMigrationGateTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private SqliteProjectIdsMigrationGate NewGate() => new(_factory);

    [RetryFact]
    public async Task IsMigratedAsync_WithNoRequestRow_IsFalse()
    {
        await using var _ = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        // Ledger — default-true : --filter IsMigratedAsync_WithNoRequestRow_IsFalse : fresh bank, no request row.
        (await NewGate().IsMigratedAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [RetryFact]
    public async Task IsMigratedAsync_WithOpenRequest_IsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.RequestRepair,
            new { kind = RepairKinds.ProjectIds, requestedAt = 1L, mapJson = (string?)null }, cancellationToken: ct));

        // Ledger — finished-NULL-check : --filter IsMigratedAsync_WithOpenRequest_IsFalse : requested (open) bank.
        (await NewGate().IsMigratedAsync(ct)).ShouldBeFalse("an open request is not a completed migration");
    }

    [RetryFact]
    public async Task IsMigratedAsync_WithFinishedRequest_IsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.RequestRepair,
            new { kind = RepairKinds.ProjectIds, requestedAt = 1L, mapJson = (string?)null }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.FinishRepairRequest,
            new { kind = RepairKinds.ProjectIds, finishedAt = 2L }, cancellationToken: ct));

        // Ledger — skip-kind-predicate : --filter IsMigratedAsync_WithFinishedRequest_IsTrue : finished bank.
        (await NewGate().IsMigratedAsync(ct)).ShouldBeTrue();
    }

    [RetryFact]
    public async Task IsMigratedAsync_AfterASecondRequest_ReopensToFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.RequestRepair,
            new { kind = RepairKinds.ProjectIds, requestedAt = 1L, mapJson = (string?)null }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.FinishRepairRequest,
            new { kind = RepairKinds.ProjectIds, finishedAt = 2L }, cancellationToken: ct));

        // A second request re-opens the kind (ON CONFLICT resets finished_at): a re-migration in
        // flight must not read as migrated.
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.RequestRepair,
            new { kind = RepairKinds.ProjectIds, requestedAt = 3L, mapJson = (string?)null }, cancellationToken: ct));

        // Ledger — ignore-reopen : --filter IsMigratedAsync_AfterASecondRequest_ReopensToFalse : finished then re-requested bank.
        (await NewGate().IsMigratedAsync(ct)).ShouldBeFalse();
    }
}
