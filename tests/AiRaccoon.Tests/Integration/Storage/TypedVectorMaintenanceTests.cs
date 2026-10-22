using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     The typed vector layout has to be MAINTAINED, not just created (ADR-0141): a row that is
///     embedded again is placed again, under the kind its row carries now; the dimension reconciler
///     recreates the tables in the typed shape; and the code corpus keeps its own layout, because a
///     code chunk is not a row of <c>entries</c> and carries no row kind.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class TypedVectorMaintenanceTests
{
    private const int Dimension = 384;

    /// <summary>One seeded row: its kind, and the vector it carries.</summary>
    private sealed record SeedRow(string Hash, string Path, string? SourceFile, EntryKind Kind);

    /// <summary>
    ///     A row embedded again is placed again: the trigger deletes the old vector and inserts the
    ///     new one under the row's CURRENT kind. A vector left under a kind the row no longer has
    ///     would be invisible to every typed query that asks for the row's kind.
    /// </summary>
    [RetryFact]
    public async Task TheEmbedTransition_PlacesTheVectorUnderTheRowsCurrentKind()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        await SeedAsync(connection, new SeedRow("h1", "docs/guide.md", "docs/guide.md", EntryKind.File));

        var kinds = await ReadVectorKindsAsync(connection);
        kinds["h1"].ShouldBe(EntryKindWireNames.File);

        // The row's kind changes: a note that cited a file becomes a row that mirrors one.
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET entry_type = 'memory' WHERE hash = 'h1'",
            cancellationToken: TestContext.Current.CancellationToken));

        // The next embed places the vector again, under the kind the row carries NOW.
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET embed_state = 'pending' WHERE hash = 'h1'",
            cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE entries SET embed_state = 'embedded' WHERE hash = 'h1'",
            cancellationToken: TestContext.Current.CancellationToken));

        var after = await ReadVectorKindsAsync(connection);
        after["h1"].ShouldBe(EntryKindWireNames.Memory,
            "the vector is placed under the row's current kind, not the kind it was first embedded with");
    }

    /// <summary>
    ///     The code corpus keeps its own layout: a code chunk lives in <c>code_entries</c>, is not a
    ///     row of <c>entries</c>, and carries no row kind — so its vector table has no kind partition.
    /// </summary>
    [RetryFact]
    public async Task TheCodeCorpus_KeepsItsOwnLayout()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var codeSql = await TableSqlAsync(connection, "vec_code");
        codeSql.ShouldNotBeNull();
        codeSql.Contains("type TEXT partition key", StringComparison.Ordinal).ShouldBeFalse(
            "a code chunk carries no row kind, so its vector table has no kind partition");
        codeSql.Contains("ctx TEXT partition key", StringComparison.Ordinal).ShouldBeTrue(
            "the code corpus keeps its own partition key");
    }

    /// <summary>
    ///     The dimension reconciler recreates the memory tables in the typed shape: the triggers
    ///     insert the row's kind as the second partition key, and a body that names a column its
    ///     table does not have fails the moment a row is embedded.
    /// </summary>
    [RetryFact]
    public async Task TheDimensionReconciler_RecreatesTheMemoryTablesTyped()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        // A table in the shape the reconciler has to fix: the wrong dimension, and no kind
        // partition. The dimension is what tells the reconciler there is work to do.
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP TABLE vec_entries; CREATE VIRTUAL TABLE vec_entries USING vec0(ctx TEXT partition key, embedding float[256] distance_metric=cosine)",
            cancellationToken: TestContext.Current.CancellationToken));

        var reconciler = new VecDimensionReconciler();
        await reconciler.ReconcileAsync(connection, null, Dimension, VecDimensionReconciler.MemoryVecTables,
            TestContext.Current.CancellationToken);

        var sql = await TableSqlAsync(connection, "vec_entries");
        sql.ShouldNotBeNull();
        sql.Contains("type TEXT partition key", StringComparison.Ordinal).ShouldBeTrue(
            "the reconciler recreates the memory tables in the typed shape");
    }

    /// <summary>Seeds a row and embeds it, so the vec0 trigger places the vector.</summary>
    private static async Task SeedAsync(SqliteConnection connection, params SeedRow[] rows)
    {
        foreach (var row in rows)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, path, value, source_file, scope, project_id, created_at, updated_at,
                                      embed_state, embedding, entry_type)
                VALUES (@hash, @path, 'seeded', @sourceFile, 'project', 'acme', 1, 1, 'pending', @embedding, @kind)
                """,
                new
                {
                    hash = row.Hash,
                    path = row.Path,
                    sourceFile = row.SourceFile,
                    embedding = new byte[Dimension * sizeof(float)],
                    kind = new EntryKindClassifier().ToStoredValue(row.Kind)
                },
                cancellationToken: TestContext.Current.CancellationToken));

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE entries SET embed_state = 'embedded' WHERE hash = @hash",
                new { hash = row.Hash }, cancellationToken: TestContext.Current.CancellationToken));
        }
    }

    /// <summary>The kind each vector is filed under, read back from the partition column.</summary>
    private static async Task<Dictionary<string, string>> ReadVectorKindsAsync(SqliteConnection connection)
    {
        var rows = await connection.QueryAsync<(string Hash, string Kind)>(
            "SELECT e.hash, v.type FROM vec_entries v JOIN entries e ON e.id = v.rowid");
        return rows.ToDictionary(r => r.Hash, r => r.Kind);
    }

    private static async Task<string?> TableSqlAsync(SqliteConnection connection, string table) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @table",
            new { table }, cancellationToken: TestContext.Current.CancellationToken));

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }
}
