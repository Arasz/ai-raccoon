using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     WP4 / plan D3: the migration drain reconciles vec0 to the target dimension in ONE
///     `BEGIN IMMEDIATE` transaction — create-if-missing-or-mismatch, both tables, and never a
///     repopulate. `RebuildVecTableAsync` refills from the `entries` blob columns, which still hold
///     OLD-dim vectors after `MarkAllEmbeddedPending`, so repopulating wedges the bank (plan §1).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class VecDimensionReconcileTests
{
    [Fact]
    public async Task ReconcileAsync_TargetDiffersFromDeclared_RecreatesBothTablesAtTheTarget()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, Ct);

        var changed = await new VecDimensionReconciler().ReconcileAsync(connection, 1024, Ct);

        changed.ShouldBeTrue("384 → 1024 is a mismatch and must be reconciled");
        (await TableSqlAsync(connection, "vec_entries")).ShouldContain("float[1024]");
        (await TableSqlAsync(connection, "vec_structure")).ShouldContain("float[1024]",
            customMessage: "both tables move together — vec_structure is the one G4 caught missing");
    }

    [Fact]
    public async Task ReconcileAsync_TargetMatchesDeclared_IsANoOp()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, Ct);

        var changed = await new VecDimensionReconciler().ReconcileAsync(connection, 384, Ct);

        changed.ShouldBeFalse("a matching dimension must not drop and recreate a populated index");
    }

    /// <summary>
    ///     `ReadVecDimensionAsync` returns 384 for a MISSING table, so a dimension comparison alone
    ///     reports "already correct" and never creates it. Presence must be read explicitly.
    /// </summary>
    [Fact]
    public async Task ReconcileAsync_TableMissingAtTheTargetDimension_CreatesIt()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, Ct);
        await connection.ExecuteAsync(new CommandDefinition("DROP TABLE vec_entries", cancellationToken: Ct));

        var changed = await new VecDimensionReconciler().ReconcileAsync(connection, 384, Ct);

        changed.ShouldBeTrue("a missing table is not a matching table");
        (await TableSqlAsync(connection, "vec_entries")).ShouldContain("float[384]");
    }

    /// <summary>The trap D3 exists to avoid: old-dimension blobs must never be inserted into the
    /// fresh table. The drain's MarkEmbedded refills it through the triggers instead.</summary>
    [Fact]
    public async Task ReconcileAsync_DoesNotRepopulateFromTheEntriesBlobColumns()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, Ct);
        await SeedEmbeddedEntryAsync(connection);
        (await RowCountAsync(connection, "vec_entries")).ShouldBe(1, "seed must land a vec row first");

        await new VecDimensionReconciler().ReconcileAsync(connection, 1024, Ct);

        (await RowCountAsync(connection, "vec_entries")).ShouldBe(0,
            "repopulating from entries.embedding would insert 384-dim blobs into a float[1024] table");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task SeedEmbeddedEntryAsync(SqliteConnection connection)
    {
        var vector = new byte[384 * sizeof(float)];
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO entries(hash, value, project_id, scope, created_at, updated_at, embed_state)
            VALUES ('h1', 'v', 'p', 'project', 0, 0, 'pending')
            """, cancellationToken: Ct));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET embed_state = 'embedded', embedding = @vector WHERE hash = 'h1'",
            new { vector }, cancellationToken: Ct));
    }

    private static async Task<string> TableSqlAsync(SqliteConnection connection, string table) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @table",
            new { table }, cancellationToken: Ct))
        ?? throw new InvalidOperationException($"{table} does not exist");

    private static async Task<long> RowCountAsync(SqliteConnection connection, string table) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT count(*) FROM {table}", cancellationToken: Ct));

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(Ct);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }
}
