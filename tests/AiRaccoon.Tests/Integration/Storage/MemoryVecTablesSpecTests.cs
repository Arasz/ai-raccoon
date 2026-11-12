using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     The memory vec0 table set is declared once (<see cref="MemoryVecTables" />); the v21 step, the
///     dimension reconciler and the every-open trigger-body guard all iterate it. These tests are the
///     drift guard: the list must equal what a fresh bank creates, and the guard must read the table
///     shape structurally rather than by sniffing DDL text.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MemoryVecTablesSpecTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [RetryFact]
    public async Task Names_PlusTheCodeTable_EqualTheVec0TablesAFreshBankCreates()
    {
        await using var connection = await OpenCurrentBankAsync();

        var actual = (await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT name FROM sqlite_master WHERE sql LIKE '%USING vec0%' ORDER BY name", cancellationToken: Ct))).ToList();

        var declared = MemoryVecTables.Names.Concat(VecDimensionReconciler.CodeVecTables).Order().ToList();
        declared.ShouldBe(actual, "a vec0 table added to or dropped from the fresh DDL must change the spec list too");
    }

    [RetryFact]
    public async Task EveryEntry_HasItsTableItsTriggerAndTheExpectedColumnsOnAFreshBank()
    {
        await using var connection = await OpenCurrentBankAsync();

        foreach (var spec in MemoryVecTables.All)
        {
            var columns = (await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT name FROM pragma_table_xinfo(@table)", new { table = spec.Table }, cancellationToken: Ct))).ToList();
            foreach (var column in MemoryVecTables.ExpectedColumns)
            {
                columns.ShouldContain(column, $"{spec.Table} must expose {column} to the structural check");
            }

            var trigger = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
                "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = @name",
                new { name = spec.UpdateTrigger }, cancellationToken: Ct));
            trigger.ShouldNotBeNull($"{spec.UpdateTrigger} is declared by the spec");
        }
    }

    [RetryFact]
    public async Task EnsureAsync_AStaleBodyOnTypedTables_IsReplacedForEverySpecEntry()
    {
        await using var connection = await OpenCurrentBankAsync();
        foreach (var spec in MemoryVecTables.All)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                $"DROP TRIGGER {spec.UpdateTrigger}; CREATE TRIGGER {spec.UpdateTrigger} AFTER UPDATE OF embed_state ON entries BEGIN SELECT 1; END;",
                cancellationToken: Ct));
        }

        await MemorySchema.EnsureAsync(connection, Ct);

        foreach (var spec in MemoryVecTables.All)
        {
            (await TriggerSqlAsync(connection, spec.UpdateTrigger)).ShouldContain("INSERT INTO " + spec.Table);
        }
    }

    [RetryFact]
    public async Task EnsureAsync_ATableMissingTheTypeColumn_LeavesTheStoredBodiesAlone()
    {
        await using var connection = await OpenCurrentBankAsync();
        var spec = MemoryVecTables.All[0];
        await connection.ExecuteAsync(new CommandDefinition(
            $"DROP TABLE {spec.Table}; {MemorySchema.VecTableDdl(spec.Table, 384)};" +
            $"DROP TRIGGER {spec.UpdateTrigger}; CREATE TRIGGER {spec.UpdateTrigger} AFTER UPDATE OF embed_state ON entries BEGIN SELECT 1; END;",
            cancellationToken: Ct));

        await MemorySchema.EnsureAsync(connection, Ct);

        (await TriggerSqlAsync(connection, spec.UpdateTrigger)).ShouldNotContain("INSERT INTO",
            customMessage: "an untyped table must keep its body: the typed body names a column it does not have");
    }

    private static async Task<string> TriggerSqlAsync(SqliteConnection connection, string name) =>
        (await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = @name", new { name }, cancellationToken: Ct)))!;

    private static async Task<SqliteConnection> OpenCurrentBankAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        connection.EnableExtensions();
        connection.LoadVector();
        await MemorySchema.EnsureAsync(connection, Ct);
        return connection;
    }
}
