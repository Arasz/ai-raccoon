using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     WP1 (docs/work/2026-08-21-code-search-implementation-plan.md §3.1): table-level round-trip
///     coverage for the code corpus objects — no <c>CodeIngestor</c>/store exists yet (WP3), so
///     these drive <c>code_entries</c>/<c>code_fts</c>/<c>vec_code</c> directly with raw SQL,
///     mirroring <see cref="AiRaccoon.Tests.Integration.Storage.SqliteVectorTests" />.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteCodeStoreTests
{
    [Fact]
    public async Task VecCode_Accepts768DimVectors_AndRejects384()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var vector768 = EmbeddingBlob.ToBytes(new float[768]);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO vec_code(rowid, ctx, embedding) VALUES (1, 'acme', @embedding)",
            new { embedding = vector768 }, cancellationToken: TestContext.Current.CancellationToken));

        var hit = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            SELECT rowid FROM vec_code
            WHERE ctx = @ctx AND embedding MATCH @query AND k = 1
            """,
            new { ctx = "acme", query = vector768 }, cancellationToken: TestContext.Current.CancellationToken));
        hit.ShouldBe(1L);

        var vector384 = EmbeddingBlob.ToBytes(new float[384]);
        await Should.ThrowAsync<SqliteException>(async () =>
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO vec_code(rowid, ctx, embedding) VALUES (2, 'acme', @embedding)",
                new { embedding = vector384 }, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task CodeFts_ExternalContent_MatchReturnsCodeRows()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        await InsertCodeEntryAsync(connection, id: 1, path: "src/foo.cs",
            value: "public sealed class QuokkaFinder { }", sourceFile: "src/foo.cs");

        var rowid = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT rowid FROM code_fts WHERE code_fts MATCH 'QuokkaFinder'",
            cancellationToken: TestContext.Current.CancellationToken));
        rowid.ShouldBe(1L);
    }

    [Fact]
    public async Task PerCorpusDimension_IsIndependent_768And384Coexist()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO entries (hash, path, value, project_id, scope, created_at, updated_at)
            VALUES ('h1', 'p1', 'memory row', 'acme', 'project', 1, 1)
            """, cancellationToken: TestContext.Current.CancellationToken));
        var memoryVector = EmbeddingBlob.ToBytes(new float[384]);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO vec_entries(rowid, ctx, embedding) VALUES (1, 'project:acme', @embedding)",
            new { embedding = memoryVector }, cancellationToken: TestContext.Current.CancellationToken));

        await InsertCodeEntryAsync(connection, id: 1, path: "src/foo.cs", value: "code row", sourceFile: "src/foo.cs");
        var codeVector = EmbeddingBlob.ToBytes(new float[768]);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO vec_code(rowid, ctx, embedding) VALUES (1, 'acme', @embedding)",
            new { embedding = codeVector }, cancellationToken: TestContext.Current.CancellationToken));

        var memoryDimensionBytes = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT length(embedding) FROM vec_entries WHERE rowid = 1", cancellationToken: TestContext.Current.CancellationToken));
        var codeDimensionBytes = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT length(embedding) FROM vec_code WHERE rowid = 1", cancellationToken: TestContext.Current.CancellationToken));

        memoryDimensionBytes.ShouldBe(384L * sizeof(float));
        codeDimensionBytes.ShouldBe(768L * sizeof(float));
    }

    /// <summary>
    ///     Full round-trip: insert a pending code row → the FTS trigger indexes it → marking it
    ///     embedded upserts <c>vec_code</c> → marking it pending again empties <c>vec_code</c>
    ///     (the row itself stays; <c>code_fts</c> keeps tracking it via <c>value</c>).
    /// </summary>
    [Fact]
    public async Task CodeRow_RoundTrips_ThroughFtsAndVecCodeViaEmbedStateTriggers()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        await InsertCodeEntryAsync(connection, id: 1, path: "src/bar.cs",
            value: "sealed class NarwhalTusk { }", sourceFile: "src/bar.cs");

        (await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT rowid FROM code_fts WHERE code_fts MATCH 'NarwhalTusk'",
                cancellationToken: TestContext.Current.CancellationToken)))
            .ShouldBe(1L, "the code_fts_ai trigger must index the row on insert");

        var vector = EmbeddingBlob.ToBytes(Enumerable.Repeat(0.5f, 768).ToArray());
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE code_entries SET embed_state = 'embedded', embedding = @embedding WHERE id = 1",
            new { embedding = vector }, cancellationToken: TestContext.Current.CancellationToken));

        (await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM vec_code WHERE rowid = 1", cancellationToken: TestContext.Current.CancellationToken)))
            .ShouldBe(1L, "the vec_code_au trigger must upsert the vector when embed_state becomes 'embedded'");

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE code_entries SET embed_state = 'pending' WHERE id = 1",
            cancellationToken: TestContext.Current.CancellationToken));

        (await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM vec_code WHERE rowid = 1", cancellationToken: TestContext.Current.CancellationToken)))
            .ShouldBe(0L, "the vec_code_pending trigger must empty the row's vector when embed_state reverts to 'pending'");

        (await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM code_entries WHERE id = 1", cancellationToken: TestContext.Current.CancellationToken)))
            .ShouldBe(1L, "reverting to pending must not delete the code_entries row itself");
    }

    private static async Task InsertCodeEntryAsync(SqliteConnection connection, long id, string path, string value, string sourceFile) =>
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO code_entries (id, hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
            VALUES (@id, @hash, @path, @value, @sourceFile, 1, 1, 'acme', 1, 1)
            """,
            new { id, hash = $"hash-{id}", path, value, sourceFile }, cancellationToken: TestContext.Current.CancellationToken));

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        // The DDL declares vec0 virtual tables, so the module has to be loaded exactly as
        // SqliteConnectionFactory.InitializeAsync does before the schema can be applied.
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }
}
