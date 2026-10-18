using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     Ladder step v19 (ADR-0133): `ctx` is the vec0 partition key again on vec_entries, vec_structure
///     and vec_code, so a KNN reads only its own context's chunks instead of every project's.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class Vec0PartitionKeyRestoreTests
{
    private const int Dimension = 384;
    private const int CodeDimension = 768;

    public static TheoryData<string> VecTables => ["vec_entries", "vec_structure", "vec_code"];

    [RetryTheory]
    [MemberData(nameof(VecTables))]
    public async Task EnsureAsync_OnAV18Bank_RebuildsTheTable_WithCtxAsThePartitionKey(string table)
    {
        await using var connection = await OpenAsync();
        await SeedV18BankAsync(connection, ("acme", 1), ("acme", 2));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var sql = await TableSqlAsync(connection, table);
        Has(sql, "partition key").ShouldBeTrue("v19 partitions by ctx so a KNN prunes other contexts' chunks before reading");
        Has(sql, "distance_metric=cosine").ShouldBeTrue();
    }

    [RetryTheory]
    [MemberData(nameof(VecTables))]
    public async Task EnsureAsync_OnAFreshBank_DeclaresCtxAsThePartitionKey(string table)
    {
        await using var connection = await OpenAsync();

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        Has(await TableSqlAsync(connection, table), "partition key").ShouldBeTrue(
            "a bank created after v19 must be born in the shape v19 migrates to");
    }

    [RetryFact]
    public async Task EnsureAsync_OnAV18Bank_KeepsEveryVectorRow()
    {
        await using var connection = await OpenAsync();
        await SeedV18BankAsync(connection, ("acme", 1), ("acme", 2), ("other", 3));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await RowCountAsync(connection, "vec_entries")).ShouldBe(3);
        (await RowCountAsync(connection, "vec_structure")).ShouldBe(3);
        (await RowCountAsync(connection, "vec_code")).ShouldBe(3);
    }

    /// <summary>Captured before the step runs, so the comparison cannot pass by the migration agreeing with itself.</summary>
    [RetryFact]
    public async Task EnsureAsync_OnAV18Bank_LeavesTheTopKUnchanged_ForAFixedQuery()
    {
        await using var connection = await OpenAsync();
        await SeedV18BankAsync(connection, ("acme", 1), ("acme", 2), ("acme", 3), ("acme", 4), ("other", 2));
        var ctx = MemorySql.ContextKeyFor(ContextNaming.ProjectContext("acme"), "acme");

        var before = await TopKAsync(connection, "vec_entries", ctx, Vector(2));
        var codeBefore = await TopKAsync(connection, "vec_code", "acme", Vector(2, CodeDimension));
        before.Count.ShouldBe(3, "the fixture must return a real ranking, or this compares nothing");
        codeBefore.Count.ShouldBe(3);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await TopKAsync(connection, "vec_entries", ctx, Vector(2))).ShouldBe(before);
        (await TopKAsync(connection, "vec_code", "acme", Vector(2, CodeDimension))).ShouldBe(codeBefore);
    }

    [RetryFact]
    public async Task AfterTheMigration_AKnnScopedToOneContext_NeverReturnsAnother()
    {
        await using var connection = await OpenAsync();
        await SeedV18BankAsync(connection, ("acme", 40), ("acme", 41), ("other", 1), ("other", 2), ("other", 3));
        var acme = MemorySql.ContextKeyFor(ContextNaming.ProjectContext("acme"), "acme");

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await TopKAsync(connection, "vec_entries", acme, Vector(1), k: 5)).Count.ShouldBe(2,
            "only the two acme rows are in scope, however near the others are");
        (await TopKAsync(connection, "vec_code", "acme", Vector(1, CodeDimension), k: 5)).Count.ShouldBe(2);
    }

    /// <summary>A crash between the v19 COMMIT and the stamp re-runs the step, which must survive meeting its own output.</summary>
    [RetryFact]
    public async Task EnsureAsync_AfterASimulatedCrashBetweenRebuildAndStamp_ReRunsCleanly()
    {
        await using var connection = await OpenAsync();
        await SeedV18BankAsync(connection, ("acme", 1), ("acme", 2));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await SetVersionAsync(connection, 18);

        await Should.NotThrowAsync(() => MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken));

        (await RowCountAsync(connection, "vec_entries")).ShouldBe(2);
        (await RowCountAsync(connection, "vec_code")).ShouldBe(2);
        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion);
    }

    /// <summary>A dimension change recreates the table; it must come back partitioned, not in the pre-v19 shape.</summary>
    [RetryTheory]
    [MemberData(nameof(VecTables))]
    public async Task Reconcile_ToANewDimension_RecreatesThePartitionedShape(string table)
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        await new VecDimensionReconciler().ReconcileAsync(connection, null, 256, [table], TestContext.Current.CancellationToken);

        var sql = await TableSqlAsync(connection, table);
        Has(sql, "float[256]").ShouldBeTrue();
        Has(sql, "partition key").ShouldBeTrue();
    }

    [RetryFact]
    public async Task EnsureAsync_OnAV18Bank_WithANonDefaultDimension_PreservesItThroughTheRebuild()
    {
        await using var connection = await OpenAsync();
        await SeedV18BankAsync(connection, ("acme", 1));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DELETE FROM entries;
            DROP TABLE vec_entries;
            CREATE VIRTUAL TABLE vec_entries USING vec0(ctx TEXT, embedding float[256] distance_metric=cosine);
            """, cancellationToken: TestContext.Current.CancellationToken));
        await SetVersionAsync(connection, 18);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        Has(await TableSqlAsync(connection, "vec_entries"), "float[256]").ShouldBeTrue();
    }

    /// <summary>
    ///     A bank in the v18 shape: current schema everywhere else, but every vec0 table declaring `ctx`
    ///     as a metadata column the way ADR-0068 left it, filled through the real embed triggers.
    /// </summary>
    private static async Task SeedV18BankAsync(SqliteConnection connection, params (string ProjectId, int Seed)[] rows)
    {
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            $"""
             DROP TABLE IF EXISTS vec_entries;
             DROP TABLE IF EXISTS vec_structure;
             DROP TABLE IF EXISTS vec_code;
             CREATE VIRTUAL TABLE vec_entries USING vec0(ctx TEXT, embedding float[{Dimension}] distance_metric=cosine);
             CREATE VIRTUAL TABLE vec_structure USING vec0(ctx TEXT, embedding float[{Dimension}] distance_metric=cosine);
             CREATE VIRTUAL TABLE vec_code USING vec0(ctx TEXT, embedding float[{CodeDimension}] distance_metric=cosine);
             """,
            cancellationToken: TestContext.Current.CancellationToken));

        foreach (var (projectId, seed) in rows)
        {
            var hash = $"h{projectId}-{seed}";
            var path = $"{projectId}-{seed}.md";
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, path, value, source_file, scope, project_id,
                                     created_at, updated_at, embed_state, embedding)
                VALUES (@hash, @path, 'seeded', @path, 'project', @projectId, 1, 1, 'pending', @vec);
                UPDATE entries SET embed_state = 'embedded' WHERE hash = @hash;
                UPDATE entries SET structure_embedding = @vec WHERE hash = @hash;

                INSERT INTO code_entries (hash, path, value, source_file, line_start, line_end, project_id,
                                          created_at, updated_at, embed_state, embedding)
                VALUES (@hash, @path, 'seeded', @path, 1, 2, @projectId, 1, 1, 'pending', @codeVec);
                UPDATE code_entries SET embed_state = 'embedded' WHERE hash = @hash;
                """,
                new
                {
                    hash,
                    path,
                    projectId,
                    vec = EmbeddingBlob.ToBytes(Vector(seed)),
                    codeVec = EmbeddingBlob.ToBytes(Vector(seed, CodeDimension))
                },
                cancellationToken: TestContext.Current.CancellationToken));
        }

        (await RowCountAsync(connection, "vec_entries")).ShouldBe(rows.Length,
            "the pre-migration fixture must hold rows, or the rebuild has nothing to preserve");
        (await RowCountAsync(connection, "vec_code")).ShouldBe(rows.Length);

        await SetVersionAsync(connection, 18);
    }

    private static bool Has(string sql, string fragment) =>
        sql.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    /// <summary>Vectors separated along one axis, so nearness is a function of the seed.</summary>
    private static float[] Vector(int seed, int dimension = Dimension)
    {
        var vector = new float[dimension];
        vector[0] = 1.0f;
        vector[1] = seed * 0.01f;
        return vector;
    }

    private static async Task<List<long>> TopKAsync(SqliteConnection connection, string table, string ctx, float[] query, int k = 3) =>
    [
        .. await connection.QueryAsync<long>(new CommandDefinition(
            $"SELECT rowid FROM {table} WHERE ctx = @ctx AND embedding MATCH @vec AND k = @k",
            new { ctx, vec = EmbeddingBlob.ToBytes(query), k },
            cancellationToken: TestContext.Current.CancellationToken))
    ];

    private static async Task<string> TableSqlAsync(SqliteConnection connection, string table) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @table",
            new { table }, cancellationToken: TestContext.Current.CancellationToken))
        ?? throw new InvalidOperationException($"{table} does not exist");

    private static async Task<long> RowCountAsync(SqliteConnection connection, string table) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT count(*) FROM {table}", cancellationToken: TestContext.Current.CancellationToken));

    private static async Task SetVersionAsync(SqliteConnection connection, int version) =>
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA user_version = {version}", cancellationToken: TestContext.Current.CancellationToken));

    private static async Task<long> ReadVersionAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "PRAGMA user_version", cancellationToken: TestContext.Current.CancellationToken));

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }
}
