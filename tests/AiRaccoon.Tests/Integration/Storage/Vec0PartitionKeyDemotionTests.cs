using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     WP5, ladder step v9: `ctx` stops being the vec0 partition key and becomes an ordinary vec0
///     metadata column. Measured trade (docs/plans/2026-08-14-project-scope-improvement-plan.md §WP5):
///     31.5 MB → 4.7 MB of chunk allocation against +1.4 ms per KNN. Demote, never remove — a table
///     with no `ctx` at all answers with the GLOBAL top-k and is not a replacement for this behaviour.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class Vec0PartitionKeyDemotionTests
{
    private const int Dimension = 384;

    [Fact]
    public async Task EnsureAsync_OnAV8Bank_RebuildsVecEntries_WithCtxAsAMetadataColumn()
    {
        await using var connection = await OpenAsync();
        await SeedV8BankAsync(connection, ("acme", 1), ("acme", 2));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var sql = await TableSqlAsync(connection, "vec_entries");
        Has(sql, "partition key").ShouldBeFalse(
            "v9 demotes ctx; 98% of the measured chunk waste is attributable to partitioning");
        Has(sql, "ctx").ShouldBeTrue(
            "demote, not remove — without ctx the KNN returns the global top-k and is unscoped");
        Has(sql, "distance_metric=cosine").ShouldBeTrue();
    }

    [Fact]
    public async Task EnsureAsync_OnAV8Bank_RebuildsVecStructure_WithCtxAsAMetadataColumn()
    {
        await using var connection = await OpenAsync();
        await SeedV8BankAsync(connection, ("acme", 1));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var sql = await TableSqlAsync(connection, "vec_structure");
        Has(sql, "partition key").ShouldBeFalse(
            "the structure table carries the same waste and takes the same step");
        Has(sql, "ctx").ShouldBeTrue();
    }

    [Fact]
    public async Task EnsureAsync_OnAFreshBank_DeclaresCtxAsAMetadataColumn()
    {
        await using var connection = await OpenAsync();

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        Has(await TableSqlAsync(connection, "vec_entries"), "partition key").ShouldBeFalse(
            "a bank created after v9 must not be born in the shape v9 exists to leave");
        Has(await TableSqlAsync(connection, "vec_structure"), "partition key").ShouldBeFalse();
    }

    /// <summary>
    ///     The acceptance criterion that matters: same top-k for a fixed query, verified against the
    ///     pre-migration bank rather than against the migration's own output. Captured BEFORE the
    ///     step runs, so the comparison cannot be satisfied by the migration agreeing with itself.
    /// </summary>
    [Fact]
    public async Task EnsureAsync_OnAV8Bank_LeavesTheTopKUnchanged_ForAFixedQuery()
    {
        await using var connection = await OpenAsync();
        await SeedV8BankAsync(connection,
            ("acme", 1), ("acme", 2), ("acme", 3), ("acme", 4), ("acme", 5));
        var query = Vector(2);
        var ctx = MemorySql.ContextKeyFor(ContextNaming.ProjectContext("acme"), "acme");

        var before = await TopKAsync(connection, ctx, query);
        before.Count.ShouldBe(3, "the fixture must return a real ranking, or this compares nothing");

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var after = await TopKAsync(connection, ctx, query);
        after.ShouldBe(before, "demoting the partition key must not change which rows a KNN returns, or their order");
    }

    /// <summary>
    ///     A metadata column still scopes. This is the check that separates option 1 from the
    ///     unpartitioned shape the first WP5 measurement wrongly used: rows in another context must
    ///     stay invisible even when they are the nearest vectors in the bank.
    /// </summary>
    [Fact]
    public async Task AfterTheMigration_AKnnScopedToOneContext_NeverReturnsAnother()
    {
        await using var connection = await OpenAsync();
        // The nearest vectors in the bank belong to `other`; only `acme` may come back.
        await SeedV8BankAsync(connection,
            ("acme", 40), ("acme", 41), ("other", 1), ("other", 2), ("other", 3));
        var acme = MemorySql.ContextKeyFor(ContextNaming.ProjectContext("acme"), "acme");

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var hits = await TopKAsync(connection, acme, Vector(1), k: 5);
        hits.Count.ShouldBe(2, "only the two acme rows are in scope, however near the others are");
        var scopes = await connection.QueryAsync<string>(new CommandDefinition(
            $"SELECT project_id FROM entries WHERE id IN ({string.Join(',', hits)})",
            cancellationToken: TestContext.Current.CancellationToken));
        scopes.ShouldAllBe(p => p == "acme");
    }

    [Fact]
    public async Task EnsureAsync_TwiceOnAV8Bank_IsIdempotent()
    {
        await using var connection = await OpenAsync();
        await SeedV8BankAsync(connection, ("acme", 1), ("acme", 2), ("acme", 3));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        var first = await RowCountAsync(connection, "vec_entries");
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        first.ShouldBe(3);
        (await RowCountAsync(connection, "vec_entries")).ShouldBe(first,
            "a re-run must not duplicate the rebuilt rows");
        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion);
    }

    /// <summary>
    ///     A crash between the v9 COMMIT and the stamp leaves the bank rebuilt but still reporting v8.
    ///     The next open re-runs the step, which must survive meeting its own output.
    /// </summary>
    [Fact]
    public async Task EnsureAsync_AfterASimulatedCrashBetweenRebuildAndStamp_ReRunsCleanly()
    {
        await using var connection = await OpenAsync();
        await SeedV8BankAsync(connection, ("acme", 1), ("acme", 2));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "PRAGMA user_version = 8", cancellationToken: TestContext.Current.CancellationToken));

        await Should.NotThrowAsync(() => MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken));

        (await RowCountAsync(connection, "vec_entries")).ShouldBe(2);
        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion);
    }

    /// <summary>
    ///     The rebuild frees pages into SQLite's free list; the FILE does not shrink until VACUUM.
    ///     Measured on a copy of the live bank: v9 freed 42.0 MB and the file stayed at exactly
    ///     183,099,392 bytes. Without this the whole package returns no disk to anyone — the saving
    ///     is real and permanently unrealised, because the maintenance service's vacuum clock is
    ///     per-process and seeded on first run, so a short-lived process never reaches it.
    /// </summary>
    [Fact]
    public async Task EnsureAsync_OnAV8Bank_DrainsTheFreeListTheRebuildCreated()
    {
        // FILE-BACKED and large enough on purpose. An in-memory bank reports a free list of 0
        // whatever the migration does, so the same assertion there passes against an implementation
        // with no VACUUM at all — measured, and the reason this test does not use OpenAsync().
        var root = TestData.CreateTempRoot("v9-vacuum");
        try
        {
            var options = TestData.CreateInfrastructureOptions(root);
            var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
            await using var connection = await factory.OpenBankAsync(TestContext.Current.CancellationToken);
            await SeedV8OnOpenBankAsync(connection, 60);

            await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

            var freelist = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition("PRAGMA freelist_count", cancellationToken: TestContext.Current.CancellationToken));
            freelist.ShouldBe(0,
                "v9 must VACUUM after the rebuild, or the pages it frees stay on the free list and no "
                + "user ever gets the disk back — the maintenance vacuum clock is per-process and a "
                + "short-lived process never reaches it");
        }
        finally
        {
            TestData.DeleteTempRoot(root);
        }
    }

    /// <summary>A bank on a non-384 model keeps its declared dimension through the v9 rebuild.</summary>
    [Fact]
    public async Task EnsureAsync_OnAV8Bank_WithANonDefaultDimension_PreservesItThroughTheRebuild()
    {
        await using var connection = await OpenAsync();
        await SeedV8BankAsync(connection, dimension: 256, rows: [("acme", 1)]);

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        Has(await TableSqlAsync(connection, "vec_entries"), "float[256]").ShouldBeTrue();
    }

    /// <summary>
    ///     Builds a bank in the pre-v9 shape: current schema everywhere else, but vec_entries and
    ///     vec_structure carrying the `ctx` partition key exactly as every bank opened by a 1.15.0 or
    ///     older binary does, stamped back to v8 so the ladder sees the step as pending.
    /// </summary>
    private static async Task SeedV8BankAsync(SqliteConnection connection,
        params (string ProjectId, int Seed)[] rows) =>
        await SeedV8BankAsync(connection, Dimension, rows);


    private static async Task SeedV8BankAsync(SqliteConnection connection, int dimension,
        (string ProjectId, int Seed)[] rows)
    {
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            $"""
             DROP TABLE IF EXISTS vec_entries;
             DROP TABLE IF EXISTS vec_structure;
             CREATE VIRTUAL TABLE vec_entries USING vec0(ctx TEXT partition key, embedding float[{dimension}] distance_metric=cosine);
             CREATE VIRTUAL TABLE vec_structure USING vec0(ctx TEXT partition key, embedding float[{dimension}] distance_metric=cosine);
             """,
            cancellationToken: TestContext.Current.CancellationToken));

        foreach (var (projectId, seed) in rows)
        {
            // Rows reach vec_entries/vec_structure only through the embed pass's UPDATE — there is no
            // AFTER INSERT trigger — so the fixture takes that same path rather than writing the vec
            // tables by hand. A hand-written fixture would prove nothing about the real population.
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, path, value, source_file, scope, project_id,
                                     created_at, updated_at, embed_state, embedding)
                VALUES (@hash, @path, 'seeded', @path, 'project', @projectId, 1, 1, 'pending', @vec);
                UPDATE entries SET embed_state = 'embedded' WHERE hash = @hash;
                UPDATE entries SET structure_embedding = @vec WHERE hash = @hash;
                """,
                new
                {
                    hash = $"h{projectId}-{seed}", path = $"{projectId}-{seed}.md", projectId,
                    vec = EmbeddingBlob.ToBytes(Vector(seed, dimension))
                },
                cancellationToken: TestContext.Current.CancellationToken));
        }

        // The triggers on `entries` populate vec_entries as rows land, so the partitioned table is
        // genuinely full before the step runs rather than empty and trivially migratable.
        (await RowCountAsync(connection, "vec_entries")).ShouldBe(rows.Length,
            "the pre-migration fixture must hold rows, or the rebuild has nothing to preserve");

        await connection.ExecuteAsync(new CommandDefinition(
            "PRAGMA user_version = 8", cancellationToken: TestContext.Current.CancellationToken));
    }

    private static bool Has(string sql, string fragment) =>
        sql.Contains(fragment, StringComparison.OrdinalIgnoreCase);

    /// <summary>Fills an already-open bank with enough embedded rows that dropping the vec0 tables frees pages.</summary>
    private static async Task SeedV8OnOpenBankAsync(SqliteConnection connection, int count)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            $"""
             DROP TABLE IF EXISTS vec_entries;
             DROP TABLE IF EXISTS vec_structure;
             CREATE VIRTUAL TABLE vec_entries USING vec0(ctx TEXT partition key, embedding float[{Dimension}] distance_metric=cosine);
             CREATE VIRTUAL TABLE vec_structure USING vec0(ctx TEXT partition key, embedding float[{Dimension}] distance_metric=cosine);
             """, cancellationToken: TestContext.Current.CancellationToken));

        for (var i = 0; i < count; i++)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, path, value, source_file, scope, project_id, context_label,
                                     created_at, updated_at, embed_state, embedding)
                VALUES (@hash, @path, 'seeded', @path, 'custom', 'acme', @ctx, 1, 1, 'pending', @vec);
                UPDATE entries SET embed_state = 'embedded' WHERE hash = @hash;
                UPDATE entries SET structure_embedding = @vec WHERE hash = @hash;
                """,
                // Many small partitions is the defect itself: vec0 chunks are fixed-capacity, so each
                // distinct ctx allocates a whole chunk however few rows it holds. One ctx -- which the
                // first draft of this fixture used -- wastes nothing, and the assertion below then
                // passes against an implementation with no VACUUM at all.
                new { hash = $"h{i}", path = $"doc-{i}.md", ctx = $"ctx-{i % 20}", vec = EmbeddingBlob.ToBytes(Vector(i)) },
                cancellationToken: TestContext.Current.CancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "PRAGMA user_version = 8", cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Vectors that are separated along one axis, so nearness is a function of the seed.</summary>
    private static float[] Vector(int seed, int dimension = Dimension)
    {
        var vector = new float[dimension];
        vector[0] = 1.0f;
        vector[1] = seed * 0.01f;
        return vector;
    }

    private static async Task<List<long>> TopKAsync(SqliteConnection connection, string ctx, float[] query, int k = 3) =>
    [
        .. await connection.QueryAsync<long>(new CommandDefinition(
            "SELECT rowid FROM vec_entries WHERE ctx = @ctx AND embedding MATCH @vec AND k = @k",
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
