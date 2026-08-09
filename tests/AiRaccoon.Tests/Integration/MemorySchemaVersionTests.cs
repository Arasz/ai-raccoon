using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     ADR-0011: the bank carries its schema shape in `PRAGMA user_version` instead of
///     re-deriving it from a column-and-index probe on every open.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MemorySchemaVersionTests
{
    [Fact]
    public async Task EnsureAsync_OnAFreshBank_StampsTheCurrentVersion()
    {
        await using var connection = await OpenAsync();

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion);
    }

    [Fact]
    public async Task EnsureAsync_OnAnUnstampedBank_RunsTheLadder_ThenStamps()
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(new CommandDefinition("""
            CREATE TABLE entries (
                id INTEGER PRIMARY KEY,
                hash TEXT,
                path TEXT,
                value TEXT,
                scope TEXT NULL,
                project_id TEXT NULL,
                context_label TEXT NULL,
                workspace_id TEXT NULL,
                agent_id TEXT NULL,
                created_at INTEGER NOT NULL,
                updated_at INTEGER NOT NULL,
                access_count INTEGER NOT NULL DEFAULT 0,
                last_accessed_at INTEGER NULL,
                rating REAL NOT NULL DEFAULT 0.5,
                ttl_days INTEGER NULL,
                embed_state TEXT NOT NULL DEFAULT 'pending',
                embedding BLOB NULL
            );
            """, cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var columns = await ColumnsAsync(connection, "entries");
        columns.ShouldContain("source_file");
        columns.ShouldContain("structure_embedding");
        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion);
    }

    /// <summary>Observed through the bucket index, which only the ladder creates on an existing bank.</summary>
    [Fact]
    public async Task EnsureAsync_OnAStampedBank_SkipsTheLadder()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP INDEX uq_entries_shared_bucket",
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await IndexExistsAsync(connection, "uq_entries_shared_bucket"))
            .ShouldBeFalse("a stamped bank must not pay for the ladder's probes again");
    }

    /// <summary>The same witness from the other side: an unstamped bank does run the step.</summary>
    [Fact]
    public async Task EnsureAsync_OnAnUnstampedBank_MovesTheLegacyIngestScopeKeys()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO settings(key, value) VALUES ('watch.scope.acme', '[]');
            PRAGMA user_version = 0;
            """, cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await SettingExistsAsync(connection, "watch.scope.acme")).ShouldBeFalse();
        (await SettingExistsAsync(connection, "ingest.scope.acme")).ShouldBeTrue();
    }

    /// <summary>docs/plans/2026-08-08-search-knn-perf.md §3.2: the v2 ladder step persists chunk_index/total_chunks and repartitions vec_entries/vec_structure.</summary>
    [Fact]
    public async Task EnsureAsync_OnAV1Bank_AddsChunkColumns_AndBackfillsThem()
    {
        await using var connection = await OpenAsync();
        await SeedV1BankAsync(connection,
            ("h1", "doc.md", "project", "acme", null, null, "doc.md", true),
            ("h2", "doc.md", "project", "acme", null, null, "doc.md", true));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var columns = await ColumnsAsync(connection, "entries");
        columns.ShouldContain("chunk_index");
        columns.ShouldContain("total_chunks");

        var rows = (await connection.QueryAsync<(string Hash, int ChunkIndex, int TotalChunks)>(
                new CommandDefinition(
                    "SELECT hash AS Hash, chunk_index AS ChunkIndex, total_chunks AS TotalChunks FROM entries ORDER BY id",
                    cancellationToken: TestContext.Current.CancellationToken)))
            .ToList();
        rows.ShouldBe([("h1", 0, 2), ("h2", 1, 2)]);
        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion);
    }

    [Fact]
    public async Task EnsureAsync_OnAV1Bank_RebuildsVecEntries_WithPartitionKeyAndCosine_HoldingEmbeddedRowsOnly()
    {
        await using var connection = await OpenAsync();
        await SeedV1BankAsync(connection,
            ("h1", "a.md", "project", "acme", null, null, "a.md", true),
            ("h2", "b.md", "project", "acme", null, null, "b.md", false));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var sql = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'vec_entries'",
                cancellationToken: TestContext.Current.CancellationToken));
        sql.ShouldNotBeNull();
        sql.ShouldContain("partition key");
        sql.ShouldContain("distance_metric=cosine");

        var vecCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition("SELECT count(*) FROM vec_entries", cancellationToken: TestContext.Current.CancellationToken));
        vecCount.ShouldBe(1, "only the embedded row should have made it into the rebuilt table");
    }

    [Fact]
    public async Task EnsureAsync_OnAV1Bank_WithANonDefaultDimension_PreservesItThroughTheRebuild()
    {
        await using var connection = await OpenAsync();
        await SeedV1BankAsync(connection, dimension: 256, ("h1", "a.md", "project", "acme", null, null, "a.md", true));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var sql = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'vec_entries'",
                cancellationToken: TestContext.Current.CancellationToken));
        sql.ShouldNotBeNull();
        sql.ShouldContain("float[256]");
    }

    /// <summary>The encoding lives in two languages (SQL trigger, C# builder); this is the check that they cannot silently diverge.</summary>
    [Fact]
    public async Task EnsureAsync_OnAV1Bank_TriggerComputedCtx_MatchesTheCSharpBuilder_ForEveryContextShape()
    {
        await using var connection = await OpenAsync();
        await SeedV1BankAsync(connection,
            ("h-shared", "shared/a.md", "shared", "acme", null, null, null, true),
            ("h-project", "p.md", "project", "acme", null, null, null, true),
            ("h-custom", "c.md", "custom", "acme", "my-label", null, null, true),
            ("h-workspace", "w.md", null, "acme", null, "ws-1", null, true));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var rows = (await connection.QueryAsync<(string Hash, string Ctx)>(
                new CommandDefinition(
                    "SELECT e.hash AS Hash, v.ctx AS Ctx FROM vec_entries v JOIN entries e ON e.id = v.rowid",
                    cancellationToken: TestContext.Current.CancellationToken)))
            .ToDictionary(r => r.Hash, r => r.Ctx, StringComparer.Ordinal);

        rows["h-shared"].ShouldBe(MemorySql.ContextKeyFor(ContextNaming.SharedContext, "acme"));
        rows["h-project"].ShouldBe(MemorySql.ContextKeyFor(ContextNaming.ProjectContext("acme"), "acme"));
        rows["h-custom"].ShouldBe(MemorySql.ContextKeyFor("my-label", "acme"));
        rows["h-workspace"].ShouldBe(MemorySql.ContextKeyFor(ContextNaming.WorkspaceContext("ws-1"), "acme"));
    }

    [Fact]
    public async Task EnsureAsync_TwiceOnAV1Bank_TheSecondCallSkipsTheLadder()
    {
        await using var connection = await OpenAsync();
        await SeedV1BankAsync(connection, ("h1", "a.md", "project", "acme", null, null, "a.md", true));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        // Witness a v2-shaped bank whose ladder truly stopped running: replace vec_entries with
        // a stub table a second ladder pass would repopulate, and prove it stays untouched.
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP TABLE vec_entries; CREATE TABLE vec_entries (rowid INTEGER PRIMARY KEY, ctx TEXT, embedding BLOB);",
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var count = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition("SELECT count(*) FROM vec_entries", cancellationToken: TestContext.Current.CancellationToken));
        count.ShouldBe(0, "a stamped v2 bank must not re-run the rebuild");
    }

    [Fact]
    public async Task EnsureAsync_V2Migration_IsReentrant_AfterASimulatedMidRebuildCrash()
    {
        await using var connection = await OpenAsync();
        await SeedV1BankAsync(connection, ("h1", "a.md", "project", "acme", null, null, "a.md", true));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        // A crash between the v2 step's COMMIT and the stamp would leave the bank exactly here:
        // rebuilt, but still reporting version 1 on the next open.
        await connection.ExecuteAsync(new CommandDefinition(
            "PRAGMA user_version = 1", cancellationToken: TestContext.Current.CancellationToken));

        await Should.NotThrowAsync(() => MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken));

        var vecCount = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition("SELECT count(*) FROM vec_entries", cancellationToken: TestContext.Current.CancellationToken));
        vecCount.ShouldBe(1, "a re-run of the rebuild must not duplicate rows");
        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion);
    }

    /// <summary>
    ///     An old binary opening a bank a newer binary already migrated must refuse to write
    ///     rather than silently no-op through write paths that skip that schema's maintenance.
    /// </summary>
    [Fact]
    public async Task EnsureAsync_WhenStoredVersionIsAheadOfCurrent_ThrowsNamingBothVersions()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        var aheadVersion = MemorySchema.CurrentVersion + 1;
        await connection.ExecuteAsync(new CommandDefinition(
            $"PRAGMA user_version = {aheadVersion}", cancellationToken: TestContext.Current.CancellationToken));

        var exception = await Should.ThrowAsync<UnsupportedSchemaVersionException>(
            () => MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken));

        exception.Message.ShouldContain(aheadVersion.ToString());
        exception.Message.ShouldContain(MemorySchema.CurrentVersion.ToString());
    }

    /// <summary>
    ///     docs/plans/2026-08-08-search-knn-perf.md §3.3: the v3 ladder step heals rows an old
    ///     pre-guard binary wrote into a v2 bank without running the write-path chunk recompute,
    ///     leaving chunk_index/total_chunks at their 0/0 defaults.
    /// </summary>
    [Fact]
    public async Task EnsureAsync_OnAV2Bank_WithDriftedChunkColumns_HealsThemBankWide()
    {
        await using var connection = await OpenAsync();
        // Simulate the drift: three chunks land with their column defaults untouched, and the
        // bank is forced back to reporting v2 so EnsureAsync sees it as needing the v3 step.
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO entries (hash, path, value, source_file, scope, project_id, created_at, updated_at, embed_state)
            VALUES ('h1', 'doc.md', 'chunk 1', 'doc.md', 'project', 'acme', 1, 1, 'pending'),
                   ('h2', 'doc.md', 'chunk 2', 'doc.md', 'project', 'acme', 1, 1, 'pending'),
                   ('h3', 'doc.md', 'chunk 3', 'doc.md', 'project', 'acme', 1, 1, 'pending');
            PRAGMA user_version = 2;
            """,
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var rows = (await connection.QueryAsync<(string Hash, int ChunkIndex, int TotalChunks)>(
                new CommandDefinition(
                    "SELECT hash AS Hash, chunk_index AS ChunkIndex, total_chunks AS TotalChunks FROM entries WHERE source_file = 'doc.md' ORDER BY id",
                    cancellationToken: TestContext.Current.CancellationToken)))
            .ToList();
        rows.ShouldBe([("h1", 0, 3), ("h2", 1, 3), ("h3", 2, 3)]);
        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion);
    }

    [Fact]
    public async Task EnsureAsync_OnAStampedV3Bank_SkipsTheRecomputeStep()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO entries (hash, path, value, source_file, scope, project_id, created_at, updated_at, embed_state)
            VALUES ('h1', 'doc.md', 'chunk 1', 'doc.md', 'project', 'acme', 1, 1, 'pending'),
                   ('h2', 'doc.md', 'chunk 2', 'doc.md', 'project', 'acme', 1, 1, 'pending');
            UPDATE entries SET chunk_index = 99, total_chunks = 99 WHERE hash = 'h1';
            """,
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var chunkIndex = await connection.ExecuteScalarAsync<long>(
            new CommandDefinition(
                "SELECT chunk_index FROM entries WHERE hash = 'h1'",
                cancellationToken: TestContext.Current.CancellationToken));
        chunkIndex.ShouldBe(99, "a stamped v3 bank must not re-run the bank-wide recompute");
    }

    /// <summary>A v1 bank migrates straight through v2 and v3 in one open — reuses the v1 seed harness rather than hand-writing a v2 fixture.</summary>
    [Fact]
    public async Task EnsureAsync_OnAV1Bank_RunsTheFullLadderThroughV3()
    {
        await using var connection = await OpenAsync();
        await SeedV1BankAsync(connection,
            ("h1", "doc.md", "project", "acme", null, null, "doc.md", true),
            ("h2", "doc.md", "project", "acme", null, null, "doc.md", true));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion);
        var rows = (await connection.QueryAsync<(string Hash, int ChunkIndex, int TotalChunks)>(
                new CommandDefinition(
                    "SELECT hash AS Hash, chunk_index AS ChunkIndex, total_chunks AS TotalChunks FROM entries ORDER BY id",
                    cancellationToken: TestContext.Current.CancellationToken)))
            .ToList();
        rows.ShouldBe([("h1", 0, 2), ("h2", 1, 2)]);
    }

    /// <summary>
    ///     ADR-0018: promotion_queue rows carry the version of the scorer that produced them, so a
    ///     retired scorer's rows can be told apart from current ones. DEFAULT 0 is deliberate — every
    ///     pre-existing row was scored by no versioned scorer at all, so it must read as stale.
    /// </summary>
    [Fact]
    public async Task EnsureAsync_OnAV3Bank_AddsScorerVersionColumn_AndExistingRowsDefaultToZero()
    {
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE promotion_queue (
                id          INTEGER PRIMARY KEY,
                project_id  TEXT NOT NULL,
                hash        TEXT NOT NULL,
                path        TEXT NULL,
                value       TEXT NOT NULL,
                source_file TEXT NULL,
                score       REAL NOT NULL,
                reasons     TEXT NOT NULL DEFAULT '[]',
                created_at  INTEGER NOT NULL,
                updated_at  INTEGER NOT NULL,
                UNIQUE (project_id, hash)
            );
            INSERT INTO promotion_queue (project_id, hash, path, value, score, created_at, updated_at)
            VALUES ('acme', 'h1', 'h1.md', 'v1-scored value', 2.5, 1, 1);
            PRAGMA user_version = 3;
            """,
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var columns = await ColumnsAsync(connection, "promotion_queue");
        columns.ShouldContain("scorer_version");
        var scorerVersion = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT scorer_version FROM promotion_queue WHERE hash = 'h1'",
            cancellationToken: TestContext.Current.CancellationToken));
        scorerVersion.ShouldBe(0, "a pre-migration row was never scored by a versioned scorer, so it must read as stale");
        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion);
    }

    /// <summary>A bank already stamped at the current version must not re-run the column-add step —
    /// witnessed by an explicit non-default value surviving a second EnsureAsync untouched.</summary>
    [Fact]
    public async Task EnsureAsync_OnAnAlreadyMigratedBank_DoesNotReMigrate_PromotionQueue()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO promotion_queue (project_id, hash, path, value, score, scorer_version, created_at, updated_at)
            VALUES ('acme', 'h1', 'h1.md', 'v', 1.0, 7, 1, 1)
            """,
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var scorerVersion = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT scorer_version FROM promotion_queue WHERE hash = 'h1'",
            cancellationToken: TestContext.Current.CancellationToken));
        scorerVersion.ShouldBe(7, "a bank already at the current version must not be re-migrated");
    }

    /// <summary>
    ///     H4 migration trap, amended: `CREATE TRIGGER IF NOT EXISTS` never replaces a trigger body
    ///     already on disk, so a body *replacement* cannot ride the same additive mechanism the
    ///     original trigger did. ADR-0023 explicitly rejects a `CurrentVersion` bump for this trigger
    ///     (bumping would make every older binary that already touched a migrated bank refuse to open
    ///     it read-write, per ADR-0019's forward-version guard — costly on a bank shared across
    ///     concurrent sessions). The fix instead stays inside the unconditional `Ddl` script (a
    ///     `DROP TRIGGER IF EXISTS` immediately before an unguarded `CREATE TRIGGER`), which already
    ///     runs in full on every open regardless of stamped version — so this case needs no
    ///     `PRAGMA user_version` manipulation at all, unlike the superseded version of this test.
    /// </summary>
    [Fact]
    public async Task EnsureAsync_WhenTheTriggerIsMissingEntirely_RecreatesItWithTheScopeAwareGuard()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DROP TRIGGER promotion_queue_entries_ad",
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var sql = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = 'promotion_queue_entries_ad'",
                cancellationToken: TestContext.Current.CancellationToken));
        sql.ShouldNotBeNull();
        sql.ShouldContain("scope");
        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion,
            "the trigger fix must not require or trigger any version-ladder step");
    }

    /// <summary>
    ///     The case that actually matters: not a missing trigger, but the *old* scope-blind body still
    ///     on disk — exactly what every bank opened by a pre-fix binary carries. No `PRAGMA
    ///     user_version` write here either; the unconditional `Ddl` path must correct this regardless
    ///     of what version the bank is stamped at. Asserting on the SQL text (not just the trigger's
    ///     name existing) is deliberate — a name-only assertion passes against the old body too and
    ///     would let the split ship.
    /// </summary>
    [Fact]
    public async Task EnsureAsync_WhenTheTriggerCarriesTheOldScopeBlindBody_ReplacesItOnReopen()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        // What every bank opened by a pre-fix binary has on disk: the pre-H4 scope-blind body.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DROP TRIGGER promotion_queue_entries_ad;
            CREATE TRIGGER promotion_queue_entries_ad AFTER DELETE ON entries BEGIN
                DELETE FROM promotion_queue
                WHERE project_id = OLD.project_id AND hash = OLD.hash
                  AND NOT EXISTS (SELECT 1 FROM entries e
                                  WHERE e.project_id = OLD.project_id AND e.hash = OLD.hash);
            END;
            """,
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var sql = await connection.ExecuteScalarAsync<string?>(
            new CommandDefinition(
                "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = 'promotion_queue_entries_ad'",
                cancellationToken: TestContext.Current.CancellationToken));
        sql.ShouldNotBeNull();
        // A name-only assertion (the trigger merely exists) would pass against the old
        // scope-blind body too and would let the split ship — the SQL text is the real witness.
        sql.ShouldContain("scope");
        (await ReadVersionAsync(connection)).ShouldBe(MemorySchema.CurrentVersion,
            "the trigger fix must not require or trigger any version-ladder step");
    }

    // The pre-v2 shape: no chunk_index/total_chunks, no ctx partition key on vec_entries/vec_structure.
    private const string V1Ddl = """
                                 CREATE TABLE workspaces (
                                     id TEXT PRIMARY KEY,
                                     project_id TEXT NOT NULL,
                                     status TEXT NOT NULL,
                                     created_at INTEGER NOT NULL
                                 );

                                 CREATE TABLE entries (
                                     id INTEGER PRIMARY KEY,
                                     hash TEXT,
                                     path TEXT,
                                     value TEXT,
                                     source_file TEXT,
                                     section TEXT,
                                     scope TEXT CHECK(scope IN ('shared','project','custom')) NULL,
                                     project_id TEXT NULL,
                                     context_label TEXT NULL,
                                     workspace_id TEXT NULL,
                                     agent_id TEXT NULL,
                                     created_at INTEGER NOT NULL,
                                     updated_at INTEGER NOT NULL,
                                     access_count INTEGER NOT NULL DEFAULT 0,
                                     last_accessed_at INTEGER NULL,
                                     rating REAL NOT NULL DEFAULT 0.5,
                                     ttl_days INTEGER NULL,
                                     embed_state TEXT NOT NULL DEFAULT 'pending' CHECK(embed_state IN ('pending','embedded')),
                                     embedding BLOB NULL,
                                     heading_path TEXT NULL,
                                     structure_embedding BLOB NULL
                                 );

                                 CREATE VIRTUAL TABLE entries_fts USING fts5(
                                     value, source_file, section, content='entries', content_rowid='id'
                                 );

                                 CREATE TRIGGER entries_fts_ai AFTER INSERT ON entries BEGIN
                                     INSERT INTO entries_fts(rowid, value, source_file, section)
                                     VALUES (new.id, new.value, new.source_file, new.section);
                                 END;

                                 CREATE UNIQUE INDEX uq_entries_shared_bucket
                                     ON entries(path, hash) WHERE scope = 'shared';
                                 CREATE UNIQUE INDEX uq_entries_committed_bucket
                                     ON entries(path, hash, project_id, scope, COALESCE(context_label, ''))
                                     WHERE scope IN ('project', 'custom');
                                 """;

    /// <summary>Builds a v1-shaped bank (stamped user_version = 1) with vec_entries at the given dimension and the given rows.</summary>
    private static async Task SeedV1BankAsync(SqliteConnection connection,
        params (string Hash, string Path, string? Scope, string ProjectId, string? ContextLabel, string? WorkspaceId, string? SourceFile, bool Embedded)[] rows) =>
        await SeedV1BankAsync(connection, dimension: 384, rows);

    private static async Task SeedV1BankAsync(SqliteConnection connection, int dimension,
        params (string Hash, string Path, string? Scope, string ProjectId, string? ContextLabel, string? WorkspaceId, string? SourceFile, bool Embedded)[] rows)
    {
        await connection.ExecuteAsync(new CommandDefinition(V1Ddl, cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            $"CREATE VIRTUAL TABLE vec_entries USING vec0(embedding float[{dimension}])",
            cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            $"CREATE VIRTUAL TABLE vec_structure USING vec0(embedding float[{dimension}])",
            cancellationToken: TestContext.Current.CancellationToken));

        var workspaceIds = rows.Select(r => r.WorkspaceId).Where(id => id is not null).Distinct().ToList();
        foreach (var workspaceId in workspaceIds)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO workspaces (id, project_id, status, created_at) VALUES (@id, 'acme', 'Active', 1)",
                new { id = workspaceId }, cancellationToken: TestContext.Current.CancellationToken));
        }

        foreach (var row in rows)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO entries (hash, path, value, source_file, scope, project_id, context_label, workspace_id,
                                      created_at, updated_at, embed_state, embedding)
                VALUES (@hash, @path, 'seeded', @sourceFile, @scope, @projectId, @contextLabel, @workspaceId,
                        1, 1, @embedState, @embedding)
                """,
                new
                {
                    hash = row.Hash,
                    path = row.Path,
                    sourceFile = row.SourceFile,
                    scope = row.Scope,
                    projectId = row.ProjectId,
                    contextLabel = row.ContextLabel,
                    workspaceId = row.WorkspaceId,
                    embedState = row.Embedded ? "embedded" : "pending",
                    embedding = row.Embedded ? EmbeddingBlob.ToBytes(new float[dimension]) : null
                },
                cancellationToken: TestContext.Current.CancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "PRAGMA user_version = 1", cancellationToken: TestContext.Current.CancellationToken));
    }

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

    private static async Task<long> ReadVersionAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "PRAGMA user_version", cancellationToken: TestContext.Current.CancellationToken));

    private static async Task<IReadOnlyCollection<string>> ColumnsAsync(SqliteConnection connection, string table) =>
        (await connection.QueryAsync<string>(new CommandDefinition(
            $"SELECT name FROM pragma_table_info('{table}')",
            cancellationToken: TestContext.Current.CancellationToken))).ToList();

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string name) =>
        await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = @name",
            new { name }, cancellationToken: TestContext.Current.CancellationToken)) is not null;

    private static async Task<bool> SettingExistsAsync(SqliteConnection connection, string key) =>
        await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT 1 FROM settings WHERE key = @key",
            new { key }, cancellationToken: TestContext.Current.CancellationToken)) is not null;
}
