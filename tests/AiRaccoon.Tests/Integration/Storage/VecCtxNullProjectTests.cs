using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     ADR-0124: the context key is total over a NULL project id. vec0's `ctx` metadata column
///     rejects NULL (v9+), so every path that keys a row whose project id is missing — the storage
///     trigger, the v9 rebuild, the chunk-column maintenance SQL — must normalize it to the same
///     key <c>ContextKeyFor(context, "")</c> builds. The pre-v9 partition-key column accepted NULL,
///     so a bank can already hold those rows and the upgrade open must not die on them.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class VecCtxNullProjectTests
{
    private const int Dimension = 384;

    /// <summary>
    ///     A fresh bank (v9+ metadata column): MarkEmbedded on a row with no project id must store
    ///     the normalized key, not let the trigger's INSERT die on a NULL metadata value.
    /// </summary>
    [RetryFact]
    public async Task MarkEmbedded_OnAProjectIdNullRow_StoresTheNormalizedCtx()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, cancellationToken);
        var id = await InsertAsync(connection, "n1", scope: "project", projectId: null, contextLabel: null, workspaceId: null);

        await MarkEmbeddedAsync(connection, id);

        (await VecCtxAsync(connection, "vec_entries", id)).ShouldBe("project:");
        (await VecCtxAsync(connection, "vec_structure", id)).ShouldBe("project:");
    }

    /// <summary>A workspace row with no project id takes the workspace branch's normalized key end to end, not just in the SQL theory.</summary>
    [RetryFact]
    public async Task MarkEmbedded_OnAProjectIdNullWorkspaceRow_StoresTheNormalizedWorkspaceCtx()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, cancellationToken);
        // The entries CHECK requires a workspace row to carry scope IS NULL (workspace_id IS NOT NULL),
        // and the FK requires the workspace itself to exist first.
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO workspaces (id, project_id, status, created_at) VALUES ('W', 'acme', 'Active', 1)",
            cancellationToken: cancellationToken));
        var id = await InsertAsync(connection, "w1", scope: null, projectId: null, contextLabel: null, workspaceId: "W");

        await MarkEmbeddedAsync(connection, id);

        (await VecCtxAsync(connection, "vec_entries", id)).ShouldBe("workspace:0::W");
        (await VecCtxAsync(connection, "vec_structure", id)).ShouldBe("workspace:0::W");
    }

    /// <summary>
    ///     The stored bodies on any bank written before this change still concatenate project_id
    ///     directly; CREATE TRIGGER IF NOT EXISTS can only create, never replace, so without the
    ///     every-open probe the fix never reaches an existing bank. This seeds the old bodies on an
    ///     already-current bank and proves the guard replaces them — the production harm is
    ///     MarkEmbedded throwing 'Expected text for TEXT metadata column ctx, received NULL', so it
    ///     runs before the replacement assertion.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_OnAnAlreadyCurrentBankWithOldTriggerBodies_ReplacesThemAndMarkEmbeddedThenSucceeds()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, cancellationToken);
        await InstallOldTriggerBodiesAsync(connection);
        (await StoredTriggerSqlAsync(connection, "vec_entries_au"))
            .Contains("COALESCE(NEW.project_id", StringComparison.Ordinal).ShouldBeFalse(
                "the fixture must hold the pre-fix bodies, or the guard is tested against itself");
        (await StoredTriggerSqlAsync(connection, "vec_structure_au"))
            .Contains("COALESCE(NEW.project_id", StringComparison.Ordinal).ShouldBeFalse();

        await MemorySchema.EnsureAsync(connection, cancellationToken);

        var id = await InsertAsync(connection, "n1", scope: "project", projectId: null, contextLabel: null, workspaceId: null);
        await MarkEmbeddedAsync(connection, id);
        (await VecCtxAsync(connection, "vec_entries", id)).ShouldBe("project:");
        (await VecCtxAsync(connection, "vec_structure", id)).ShouldBe("project:");

        Normalized(await StoredTriggerSqlAsync(connection, "vec_entries_au"))
            .ShouldContain("COALESCE(NEW.project_id, '')");
        Normalized(await StoredTriggerSqlAsync(connection, "vec_structure_au"))
            .ShouldContain("COALESCE(NEW.project_id, '')");
    }

    /// <summary>
    ///     A bank on the current schema everywhere except its vec tables, which are rolled back to
    ///     the pre-v9 partitioned shape and stamped user_version = 8 (the same hybrid fixture
    ///     Vec0PartitionKeyDemotionTests.SeedV8BankAsync documents), holding the row the old
    ///     expression wrote — partition-key `ctx` set to NULL — must survive the v9 rebuild. The vec
    ///     row is written directly rather than
    ///     through the trigger fixture so the NULL ctx is a property of the fixture, not of the
    ///     code under test: a trigger-driven fixture produces a non-NULL key once the fix is in, and
    ///     would stop exercising the upgrade of a NULL-key bank.
    /// </summary>
    [RetryFact]
    public async Task EnsureAsync_OnAV8BankWithAnEmbeddedProjectIdNullRow_UpgradesAndRebuildsVecEntries()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, cancellationToken);
        var vector = EmbeddingBlob.ToBytes(new float[Dimension]);
        var id = await InsertAsync(connection, "n1", scope: "custom", projectId: null, contextLabel: "L", workspaceId: null,
            embedState: "embedded", embedding: vector, structureEmbedding: vector);
        await InstallPartitionedV8VecTablesAsync(connection, id, vector);
        (await VecCtxAsync(connection, "vec_entries", id)).ShouldBeNull(
            "the fixture must reproduce the pre-v9 row: the old expression keyed a missing project id NULL");

        // Record rather than Should.NotThrow: the version assert below must still run in the RED
        // state, where the call throws — that is what makes "threw AND stayed at v8" observable.
        var thrown = await Record.ExceptionAsync(() => MemorySchema.EnsureAsync(connection, cancellationToken));
        thrown.ShouldBeNull();

        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion);
        (await VecCtxAsync(connection, "vec_entries", id)).ShouldBe(MemorySql.ContextKeyFor("L", ""));
        (await VecCtxAsync(connection, "vec_structure", id)).ShouldBe(MemorySql.ContextKeyFor("L", ""));
        (await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM vec_entries WHERE ctx IS NULL", cancellationToken: cancellationToken))).ShouldBe(0);
    }

    /// <summary>
    ///     The store's chunk-column recompute is keyed on the normalized key, so a row with no
    ///     project id must be reachable at the key ContextKeyFor builds for it. (The public store API
    ///     cannot pass this key — its ctx comes from ContextKeyFor with a non-empty project id — so
    ///     this pins the SQL constant's totality, not a production-reachable path.)
    /// </summary>
    [RetryFact]
    public async Task RecomputeChunkColumnsForContext_OnAProjectIdNullRow_GivenTheNormalizedCtx_RenumbersTheGroup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, cancellationToken);
        foreach (var hash in new[] { "c1", "c2", "c3" })
        {
            await InsertAsync(connection, hash, scope: "custom", projectId: null, contextLabel: null, workspaceId: null);
        }

        var changed = await connection.ExecuteAsync(new CommandDefinition(
            MemorySql.RecomputeChunkColumnsForContext,
            new { ctx = "custom:0::", sourceFile = "f.md" }, cancellationToken: cancellationToken));

        changed.ShouldBe(3, "the normalized key must reach the rows the nullable expression could not");
        var rows = await connection.QueryAsync<(long ChunkIndex, long TotalChunks)>(new CommandDefinition(
            "SELECT chunk_index AS ChunkIndex, total_chunks AS TotalChunks FROM entries WHERE source_file = 'f.md' ORDER BY id",
            cancellationToken: cancellationToken));
        rows.Select(r => (r.ChunkIndex, r.TotalChunks)).ShouldBe([(0L, 3L), (1L, 3L), (2L, 3L)]);
    }

    /// <summary>The delete-path compaction is keyed the same way, so it must shift the survivors of a group a missing project id keyed. (Same unreachable-from-the-public-API caveat as the recompute test above.)</summary>
    [RetryFact]
    public async Task CompactChunkColumnsAfterDelete_OnAProjectIdNullGroup_GivenTheNormalizedCtx_ShiftsSurvivors()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, cancellationToken);
        await InsertAsync(connection, "c1", scope: "project", projectId: null, contextLabel: null, workspaceId: null,
            chunkIndex: 0, totalChunks: 3);
        await InsertAsync(connection, "c2", scope: "project", projectId: null, contextLabel: null, workspaceId: null,
            chunkIndex: 1, totalChunks: 3);
        await InsertAsync(connection, "c3", scope: "project", projectId: null, contextLabel: null, workspaceId: null,
            chunkIndex: 2, totalChunks: 3);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM entries WHERE hash = 'c2'", cancellationToken: cancellationToken));

        var changed = await connection.ExecuteAsync(new CommandDefinition(
            MemorySql.CompactChunkColumnsAfterDelete,
            new { ctx = "project:", sourceFile = "f.md", deletedIndex = 1 }, cancellationToken: cancellationToken));

        changed.ShouldBe(2, "the normalized key must reach the survivors the nullable expression could not");
        var rows = await connection.QueryAsync<(string Hash, long ChunkIndex, long TotalChunks)>(new CommandDefinition(
            "SELECT hash AS Hash, chunk_index AS ChunkIndex, total_chunks AS TotalChunks FROM entries ORDER BY id",
            cancellationToken: cancellationToken));
        rows.Select(r => (r.Hash, r.ChunkIndex, r.TotalChunks))
            .ShouldBe([("c1", 0L, 2L), ("c3", 1L, 2L)]);
    }

    /// <summary>
    ///     The live benefit: two NULL-project rows with different contexts are one group under the
    ///     nullable expression (GROUP BY reads NULL as one key) and get cross-numbered; under the
    ///     normalized key each context is its own group and numbers from zero.
    /// </summary>
    [RetryFact]
    public async Task RecomputeChunkColumnsBankWide_SeparatesProjectIdNullRowsByNormalizedKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, cancellationToken);
        await InsertAsync(connection, "p1", scope: "project", projectId: null, contextLabel: null, workspaceId: null);
        await InsertAsync(connection, "p2", scope: "project", projectId: null, contextLabel: null, workspaceId: null);
        await InsertAsync(connection, "l1", scope: "custom", projectId: null, contextLabel: null, workspaceId: null);
        await InsertAsync(connection, "l2", scope: "custom", projectId: null, contextLabel: null, workspaceId: null);

        await connection.ExecuteAsync(new CommandDefinition(
            MemorySql.RecomputeChunkColumnsBankWide, cancellationToken: cancellationToken));

        var rows = await connection.QueryAsync<(string Scope, long ChunkIndex, long TotalChunks)>(new CommandDefinition(
            "SELECT scope AS Scope, chunk_index AS ChunkIndex, total_chunks AS TotalChunks FROM entries ORDER BY id",
            cancellationToken: cancellationToken));
        rows.Select(r => (r.Scope, r.ChunkIndex, r.TotalChunks))
            .ShouldBe([("project", 0L, 2L), ("project", 1L, 2L), ("custom", 0L, 2L), ("custom", 1L, 2L)],
                "each context must number its own NULL-project rows, not share one NULL-keyed group");
    }

    /// <summary>The ordered bank-wide form (SyncService, NoteChunkOrderRepair) partitions by the same expression: NULL-project rows must group at their normalized keys here too, not share one NULL-keyed partition.</summary>
    [RetryFact]
    public async Task RecomputeChunkColumnsBankWideKeepingOrder_SeparatesProjectIdNullRowsByNormalizedKey()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, cancellationToken);
        await InsertAsync(connection, "p1", scope: "project", projectId: null, contextLabel: null, workspaceId: null);
        await InsertAsync(connection, "p2", scope: "project", projectId: null, contextLabel: null, workspaceId: null);
        await InsertAsync(connection, "l1", scope: "custom", projectId: null, contextLabel: null, workspaceId: null);
        await InsertAsync(connection, "l2", scope: "custom", projectId: null, contextLabel: null, workspaceId: null);

        await connection.ExecuteAsync(new CommandDefinition(
            MemorySql.RecomputeChunkColumnsBankWideKeepingOrder, new { lastId = long.MaxValue },
            cancellationToken: cancellationToken));

        var rows = await connection.QueryAsync<(string Scope, long ChunkIndex, long TotalChunks)>(new CommandDefinition(
            "SELECT scope AS Scope, chunk_index AS ChunkIndex, total_chunks AS TotalChunks FROM entries ORDER BY id",
            cancellationToken: cancellationToken));
        rows.Select(r => (r.Scope, r.ChunkIndex, r.TotalChunks))
            .ShouldBe([("project", 0L, 2L), ("project", 1L, 2L), ("custom", 0L, 2L), ("custom", 1L, 2L)],
                "each context must number its own NULL-project rows, not share one NULL-keyed group");
    }

    private static async Task<long> InsertAsync(SqliteConnection connection, string hash, string? scope,
        string? projectId, string? contextLabel, string? workspaceId,
        string sourceFile = "f.md", long chunkIndex = -1, long totalChunks = 0,
        string embedState = "pending", byte[]? embedding = null, byte[]? structureEmbedding = null) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            """
            INSERT INTO entries (hash, path, value, source_file, scope, project_id, context_label, workspace_id,
                                 created_at, updated_at, chunk_index, total_chunks, embed_state, embedding, structure_embedding)
            VALUES (@hash, @hash, @hash, @sourceFile, @scope, @projectId, @contextLabel, @workspaceId,
                    1, 1, @chunkIndex, @totalChunks, @embedState, @embedding, @structureEmbedding)
            RETURNING id
            """,
            new { hash, sourceFile, scope, projectId, contextLabel, workspaceId, chunkIndex, totalChunks, embedState, embedding, structureEmbedding },
            cancellationToken: TestContext.Current.CancellationToken));

    private static async Task MarkEmbeddedAsync(SqliteConnection connection, long id)
    {
        var vector = EmbeddingBlob.ToBytes(new float[Dimension]);
        await connection.ExecuteAsync(new CommandDefinition(
            MemorySql.MarkEmbedded,
            new { id, embedding = vector, headingPath = "h", structureEmbedding = vector },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>The bodies exactly as an older build stored them: project_id concatenated directly.</summary>
    private static async Task InstallOldTriggerBodiesAsync(SqliteConnection connection) =>
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DROP TRIGGER IF EXISTS vec_entries_au;
            DROP TRIGGER IF EXISTS vec_structure_au;

            CREATE TRIGGER vec_entries_au AFTER UPDATE OF embed_state ON entries
            WHEN NEW.embed_state = 'embedded' AND NEW.embedding IS NOT NULL
            BEGIN
                DELETE FROM vec_entries WHERE rowid = NEW.id;
                INSERT INTO vec_entries(rowid, ctx, embedding) VALUES (NEW.id, CASE
                    WHEN NEW.workspace_id IS NOT NULL
                         THEN 'workspace:' || length(NEW.project_id) || ':' || NEW.project_id || ':' || NEW.workspace_id
                    WHEN NEW.scope = 'shared'  THEN 'shared'
                    WHEN NEW.scope = 'project' THEN 'project:' || NEW.project_id
                    ELSE 'custom:' || length(NEW.project_id) || ':' || NEW.project_id || ':' || COALESCE(NEW.context_label, '')
                END, NEW.embedding);
            END;

            CREATE TRIGGER vec_structure_au AFTER UPDATE OF structure_embedding ON entries
            WHEN NEW.structure_embedding IS NOT NULL
            BEGIN
                DELETE FROM vec_structure WHERE rowid = NEW.id;
                INSERT INTO vec_structure(rowid, ctx, embedding) VALUES (NEW.id, CASE
                    WHEN NEW.workspace_id IS NOT NULL
                         THEN 'workspace:' || length(NEW.project_id) || ':' || NEW.project_id || ':' || NEW.workspace_id
                    WHEN NEW.scope = 'shared'  THEN 'shared'
                    WHEN NEW.scope = 'project' THEN 'project:' || NEW.project_id
                    ELSE 'custom:' || length(NEW.project_id) || ':' || NEW.project_id || ':' || COALESCE(NEW.context_label, '')
                END, NEW.structure_embedding);
            END;
            """,
            cancellationToken: TestContext.Current.CancellationToken));

    private static async Task InstallPartitionedV8VecTablesAsync(SqliteConnection connection, long id, byte[] vector)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            $"""
             DROP TABLE IF EXISTS vec_entries;
             DROP TABLE IF EXISTS vec_structure;
             CREATE VIRTUAL TABLE vec_entries USING vec0(ctx TEXT partition key, embedding float[{Dimension}] distance_metric=cosine);
             CREATE VIRTUAL TABLE vec_structure USING vec0(ctx TEXT partition key, embedding float[{Dimension}] distance_metric=cosine);
             INSERT INTO vec_entries(rowid, ctx, embedding) VALUES (@id, NULL, @vector);
             INSERT INTO vec_structure(rowid, ctx, embedding) VALUES (@id, NULL, @vector);
             PRAGMA user_version = 8;
             """,
            new { id, vector }, cancellationToken: TestContext.Current.CancellationToken));
    }

    private static async Task<string?> VecCtxAsync(SqliteConnection connection, string table, long id) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT ctx FROM {table} WHERE rowid = @id", new { id },
            cancellationToken: TestContext.Current.CancellationToken));

    private static async Task<string> StoredTriggerSqlAsync(SqliteConnection connection, string name) =>
        await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = @name", new { name },
            cancellationToken: TestContext.Current.CancellationToken))
        ?? throw new InvalidOperationException($"trigger {name} does not exist");

    private static string Normalized(string sql) =>
        string.Join(' ', sql.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

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
