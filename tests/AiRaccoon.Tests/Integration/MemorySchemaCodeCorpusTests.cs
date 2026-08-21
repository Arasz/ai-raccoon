using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     WP1 (docs/work/2026-08-21-code-search-implementation-plan.md §3.1): the code corpus
///     (<c>code_entries</c>/<c>code_fts</c>/<c>vec_code</c>) lives in the same digest-gated
///     additive <c>Ddl</c> block as <c>metrics</c> — purely additive, no ladder step, no
///     <see cref="MemorySchema.CurrentVersion" /> bump.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MemorySchemaCodeCorpusTests
{
    [Fact]
    public async Task EnsureAsync_OnAFreshBank_CreatesCodeTablesAt768()
    {
        await using var connection = await OpenAsync();

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM code_entries", cancellationToken: TestContext.Current.CancellationToken)))
            .ShouldBe(0L);

        var codeFtsSql = await SqlOfAsync(connection, "code_fts");
        codeFtsSql.ShouldNotBeNull();

        var vecCodeSql = await SqlOfAsync(connection, "vec_code");
        vecCodeSql.ShouldNotBeNull();
        vecCodeSql.ShouldContain("float[768]");
        vecCodeSql.ShouldContain("cosine");

        // v1 has no structure modality for code (§3.1: heading_path/section/source_id absent).
        (await TableExistsAsync(connection, "vec_code_structure")).ShouldBeFalse();
    }

    [Fact]
    public async Task EnsureAsync_OnAnExistingBank_AddsCodeTables_AndPreservesMemoryTables()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO entries (hash, path, value, project_id, scope, created_at, updated_at)
            VALUES ('h1', 'p1', 'a memory row', 'acme', 'project', 1, 1)
            """, cancellationToken: TestContext.Current.CancellationToken));

        // Simulate a pre-feature bank: the code Ddl already ran once (fresh-bank path always
        // creates it), so roll the digest back and drop the code corpus objects — the shape a
        // v10 bank stamped before this task's Ddl change actually has.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DROP TABLE code_entries;
            DROP TABLE code_fts;
            DROP TABLE vec_code;
            """, cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA application_id = {MemorySchema.SchemaDigest + 1}",
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await TableExistsAsync(connection, "code_entries")).ShouldBeTrue();
        (await TableExistsAsync(connection, "code_fts")).ShouldBeTrue();
        (await TableExistsAsync(connection, "vec_code")).ShouldBeTrue();

        (await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM entries", cancellationToken: TestContext.Current.CancellationToken)))
            .ShouldBe(1L, "existing memory rows must survive the code Ddl addition");
        var vecEntriesSql = await SqlOfAsync(connection, "vec_entries");
        vecEntriesSql.ShouldNotBeNull();
        vecEntriesSql.ShouldContain("float[384]");
        (await TableExistsAsync(connection, "vec_structure")).ShouldBeTrue();
    }

    [Fact]
    public async Task SchemaDigest_ChangesWhenCodeDdlIsAdded_AndSecondOpenIsANoOp()
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA application_id = {MemorySchema.SchemaDigest + 1}",
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        (await TableExistsAsync(connection, "code_entries")).ShouldBeTrue("first open after upgrade must create the code tables");
        (await ReadApplicationIdAsync(connection)).ShouldBe(MemorySchema.SchemaDigest);

        // Second open: digest already matches, so the Ddl block must be a no-op — proven the
        // same way MemorySchemaDigestTests proves it, by dropping an object only Ddl creates.
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP TABLE code_entries", cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA application_id = {MemorySchema.SchemaDigest}",
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await TableExistsAsync(connection, "code_entries")).ShouldBeFalse(
            "a digest-matched bank must skip the Ddl block entirely, even though code_entries was dropped underneath it");
    }

    [Fact]
    public async Task CodeEntriesRow_HasExactlyTheDesignedColumns_AndNoMemoryOnlyColumns()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var columns = (await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT name FROM pragma_table_info('code_entries')",
                cancellationToken: TestContext.Current.CancellationToken)))
            .ToHashSet(StringComparer.Ordinal);

        columns.ShouldBe(
            [
                "id", "hash", "path", "value", "source_file", "line_start", "line_end", "project_id",
                "created_at", "updated_at", "embed_state", "embedding", "chunk_index", "total_chunks",
                "embed_attempts"
            ],
            ignoreOrder: true);

        columns.ShouldNotContain("scope");
        columns.ShouldNotContain("workspace_id");
        columns.ShouldNotContain("agent_id");
        columns.ShouldNotContain("rating");
        columns.ShouldNotContain("ttl_days");
        columns.ShouldNotContain("heading_path");
    }

    /// <summary>
    ///     Integration review: <c>uq_code_chunk(project_id, path, hash)</c> is defeated by NULL
    ///     rows — SQLite treats NULLs as distinct in a unique index, so two rows with a NULL
    ///     <c>hash</c> (or <c>path</c>) for the same project/path never collide. CodeIngestor
    ///     always supplies these columns, so a NULL there is a bug, not a legitimate state.
    /// </summary>
    [Fact]
    public async Task CodeEntries_DedupAndAlwaysSuppliedColumns_AreNotNull()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var notNullColumns = (await connection.QueryAsync<(string Name, long NotNull)>(new CommandDefinition(
                """SELECT name, "notnull" FROM pragma_table_info('code_entries')""",
                cancellationToken: TestContext.Current.CancellationToken)))
            .Where(c => c.NotNull == 1)
            .Select(c => c.Name)
            .ToHashSet(StringComparer.Ordinal);

        notNullColumns.ShouldContain("hash");
        notNullColumns.ShouldContain("path");
        notNullColumns.ShouldContain("value");
        notNullColumns.ShouldContain("source_file");
        notNullColumns.ShouldContain("line_start");
        notNullColumns.ShouldContain("line_end");
    }

    [Fact]
    public async Task CodeEntries_InsertingANullHash_ViolatesNotNullConstraint()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<SqliteException>(() => connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO code_entries (hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
            VALUES (NULL, 'p1.cs', 'v', 'p1.cs', 1, 1, 'acme', 1, 1)
            """, cancellationToken: TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task CodeEntries_InsertingANullPath_ViolatesNotNullConstraint()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<SqliteException>(() => connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO code_entries (hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
            VALUES ('h1', NULL, 'v', 'p1.cs', 1, 1, 'acme', 1, 1)
            """, cancellationToken: TestContext.Current.CancellationToken)));
    }

    /// <summary>
    ///     Acceptance criterion 4: adding the code corpus to the digest-gated Ddl block must not
    ///     change a single memory object's stored SQL — the two corpora share only the file.
    /// </summary>
    [Fact]
    public async Task EnsureAsync_AddingCodeDdl_LeavesMemoryObjectsSqlByteIdentical()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        var before = await MemoryObjectDumpAsync(connection);
        before.ShouldNotBeEmpty();

        // Roll back to the shape a v10 bank stamped before this task's Ddl change actually has,
        // then re-run EnsureAsync so it adds the code corpus back.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DROP TABLE code_entries;
            DROP TABLE code_fts;
            DROP TABLE vec_code;
            """, cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA application_id = {MemorySchema.SchemaDigest + 1}",
            cancellationToken: TestContext.Current.CancellationToken));
        var afterDrop = await MemoryObjectDumpAsync(connection);
        afterDrop.ShouldBe(before, "dropping only the code objects must not touch a memory object's stored SQL");

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        var afterReAdd = await MemoryObjectDumpAsync(connection);

        afterReAdd.ShouldBe(before, "re-adding the code corpus must not change any memory object's stored SQL");
    }

    /// <summary>Every non-code, non-sqlite_ object's exact stored SQL, ordered so two dumps compare byte-for-byte.</summary>
    private static async Task<IReadOnlyList<(string Type, string Name, string? Sql)>> MemoryObjectDumpAsync(SqliteConnection connection) =>
        [.. (await connection.QueryAsync<(string Type, string Name, string? Sql)>(new CommandDefinition(
                """
                SELECT type, name, sql FROM sqlite_master
                WHERE name NOT LIKE 'sqlite_%'
                  AND name NOT LIKE 'code\_%' ESCAPE '\'
                  AND name NOT LIKE 'idx\_code\_%' ESCAPE '\'
                  AND name NOT LIKE 'uq\_code\_%' ESCAPE '\'
                  AND name NOT LIKE 'vec\_code%' ESCAPE '\'
                ORDER BY type, name
                """, cancellationToken: TestContext.Current.CancellationToken)))];

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string name) =>
        await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name",
            new { name }, cancellationToken: TestContext.Current.CancellationToken)) is not null;

    private static async Task<string?> SqlOfAsync(SqliteConnection connection, string name) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT sql FROM sqlite_master WHERE name = @name",
            new { name }, cancellationToken: TestContext.Current.CancellationToken));

    private static async Task<int> ReadApplicationIdAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "PRAGMA application_id", cancellationToken: TestContext.Current.CancellationToken));

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
