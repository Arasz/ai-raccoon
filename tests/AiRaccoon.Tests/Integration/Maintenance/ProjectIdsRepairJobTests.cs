using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     Air-merge P2 gate: ProjectIdsRepairJob is the relay half of the project-ids repair outbox —
///     on-demand (<see cref="IMaintenanceJob.Interval" /> is null), it only ever runs because a
///     CLI-submitted repair_requests row exists, never on a clock. It folds loser ids into their
///     canonical winners across every id-keyed surface, invalidates renamed vec rows for the
///     existing embed drain, and re-derives chunk positions under the winner groups.
///     <para>
///         Honesty ledger (mutation : filter : fixture): skip-entries-rewrite : Repair_FirstRunMovesRows
///         : loser-labeled-rows; skip-queue-merge : Repair_FirstRunMovesRows (queue leg) : split
///         queue; skip-tombstone-rewrite : TombstonePkRewritten (sync lane) : loser tombstone;
///         skip-vec-invalidation : FtsVecResync_AfterRename : embedded loser row; skip-chunk-step :
///         ChunkRenumber_Contiguous : duplicate positions; drop-BEGIN-IMMEDIATE : RepairVsWrite :
///         two contending writers.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectIdsRepairJobTests : IDisposable
{
    private const string Winner = "jsaa";
    private const string Loser = "job-search-ai-assistant";
    private const string GuidLoser = "01a062f4-0000-7000-8000-000000000001";
    private const string RetiredGuid = "01a03024-0000-7000-8000-000000000002";
    private const string DroppedQa = "qa-noise-project";
    private const string DroppedSweep = "manual-sweep";
    private const string Typo = "jsaaa";

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot = TestData.CreateTempRoot("project-ids-repair-job");
    private readonly SqliteConnectionFactory _factory;

    public ProjectIdsRepairJobTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private ProjectIdsRepairJob NewJob() =>
        new(new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]),
            TestData.CreateEmbeddingService(), new FakeTimeProvider(FixedNow));

    [RetryFact]
    public async Task HasWorkAsync_WithNoOpenRequest_IsFalse()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        (await NewJob().HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [RetryFact]
    public async Task HasWorkAsync_AfterARequest_IsTrue()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await RequestRepairAsync(connection);

        (await NewJob().HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [RetryFact]
    public async Task RunAsync_WithNoOpenRequest_IsANoOp() =>
        await Should.NotThrowAsync(async () =>
        {
            await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
            await NewJob().RunAsync(connection, TestContext.Current.CancellationToken);
        });

    /// <summary>
    ///     First run folds every moveable surface and reports it; the immediate second run changes
    ///     nothing. Fixture: ≥2 ids × ≥2 rows, both queue fragments populated, plus the
    ///     tombstone/code/quality/watch/settings legs so an entries-only rewrite still fails.
    /// </summary>
    [RetryFact]
    public async Task Repair_FirstRunMovesRows_SecondRunNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await SeedClusterAsync(connection, ct);
        await RequestRepairAsync(connection);

        var created = await NewJob().RunAsync(connection, ct);

        created.ShouldBeTrue("the seeded bank owns loser rows on every moveable surface");
        (await CountAsync(connection, "entries", Loser, "scope = 'project' AND context_label IS NOT NULL", ct))
            .ShouldBe(0, "every labeled loser project row folds into the winner");
        (await CountAsync(connection, "entries", Winner, "scope = 'project' AND context_label IS NOT NULL", ct))
            .ShouldBe(6, "w1, w2, w-dup, l1, l2 and g1 meet under the winner");
        (await CountAsync(connection, "promotion_queue", Loser, "1 = 1", ct)).ShouldBe(0);
        (await CountAsync(connection, "promotion_queue", Winner, "1 = 1", ct)).ShouldBe(3);
        var merged = await connection.QuerySingleAsync<(double Score, long Created, long Updated)>(
            "SELECT score AS Score, created_at AS Created, updated_at AS Updated FROM promotion_queue " +
            "WHERE project_id = @winner AND hash = 'q-share'", new { winner = Winner });
        merged.Score.ShouldBe(0.8, "the queue conflict keeps the max score");
        merged.Created.ShouldBe(8, "the queue conflict keeps the min created_at");
        merged.Updated.ShouldBe(14, "the queue conflict keeps the max updated_at");
        (await CountAsync(connection, "promotion_discards", Winner, "1 = 1", ct)).ShouldBe(2);
        (await CountAsync(connection, "search_quality", Winner, "1 = 1", ct)).ShouldBe(2);
        (await CountAsync(connection, "code_entries", Loser, "1 = 1", ct)).ShouldBe(0);
        (await CountAsync(connection, "code_entries", Winner, "1 = 1", ct)).ShouldBe(2);
        (await CountAsync(connection, "sync_tombstones", Loser, "1 = 1", ct)).ShouldBe(0,
            "every surviving loser tombstone rewrites to the winner — including the dedup-created one, " +
            "which is what the pull-side folded compare suppresses the unrepaired replica's push against");
        (await CountAsync(connection, "sync_tombstones", Winner, "1 = 1", ct)).ShouldBe(3,
            "t-gone, the rewritten t-loser-gone, and the dedup-created dup-hash-1 meet under the winner");
        (await CountAsync(connection, "sync_tombstones", DroppedQa, "1 = 1", ct)).ShouldBe(1);
        (await CountAsync(connection, "sync_tombstones", DroppedSweep, "1 = 1", ct)).ShouldBe(1);
        (await ScalarAsync(connection, "SELECT value FROM settings WHERE key = 'ingest.scope.jsaa'", ct))
            .ShouldBe("[\"/x\"]", "on a settings collision the winner's value stands");
        (await ScalarAsync(connection, "SELECT count(*) FROM settings WHERE key = 'ingest.scope.job-search-ai-assistant'", ct))
            .ShouldBe(0);
        (await ScalarAsync(connection, "SELECT value FROM settings WHERE key = 'watch.enabled.jsaa'", ct))
            .ShouldBe("true");
        (await ScalarAsync(connection, "SELECT value FROM settings WHERE key = 'access.mode.project:jsaa'", ct))
            .ShouldBe("full");
        (await ScalarAsync(connection, "SELECT name FROM projects WHERE id = 'jsaa'", ct))
            .ShouldBe("jsaa", "the unregistered winner canonical gains its registry row");
        (await ScalarAsync(connection, "SELECT count(*) FROM projects WHERE id IN ('" + GuidLoser + "','" + RetiredGuid + "','" + DroppedSweep + "')", ct))
            .ShouldBe(0);
        (await CountAsync(connection, "watches", Winner, "1 = 1", ct)).ShouldBe(2);
        (await ScalarAsync(connection, "SELECT scan_owner FROM watches WHERE project_id = 'jsaa' AND path = '/repo/a'", ct))
            .ShouldBe("owner-w", "watch renames preserve the scan lease columns");
        // Untouched partitions: R1 leaves NULL-context bulk rows, custom/shared rows, workspace
        // scratch, metrics and typo rows byte-identical.
        (await CountAsync(connection, "entries", Loser, "context_label IS NULL AND workspace_id IS NULL", ct)).ShouldBe(1);
        (await CountAsync(connection, "entries", Loser, "scope = 'custom'", ct)).ShouldBe(1);
        (await CountAsync(connection, "entries", Loser, "scope = 'shared'", ct)).ShouldBe(1);
        (await CountAsync(connection, "entries", Loser, "workspace_id IS NOT NULL", ct)).ShouldBe(1);
        (await CountAsync(connection, "entries", Typo, "1 = 1", ct)).ShouldBe(1);
        (await CountAsync(connection, "metrics", Loser, "1 = 1", ct)).ShouldBe(1);

        var second = await NewJob().RunAsync(connection, ct);

        second.ShouldBeFalse("a converged bank schedules no further work");
        (await NewJob().HasWorkAsync(connection, ct)).ShouldBeFalse();
        (await CountAsync(connection, "entries", Winner, "scope = 'project' AND context_label IS NOT NULL", ct))
            .ShouldBe(6, "the second run moves nothing further");
        (await CountAsync(connection, "promotion_queue", Winner, "1 = 1", ct)).ShouldBe(3);
    }

    /// <summary>
    ///     MUST-2: renamed rows invalidate (pending + NULLs, stale vec legs gone) while the winner's
    ///     own embedded rows stay embedded; FTS still finds the moved content without any resync;
    ///     and a re-embed lands under the winner ctx the KNN filters on.
    /// </summary>
    [RetryFact]
    public async Task FtsVecResync_AfterRename()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await SeedClusterAsync(connection, ct);
        await RequestRepairAsync(connection);

        await NewJob().RunAsync(connection, ct);

        (await ScalarAsync(connection, "SELECT embed_state FROM entries WHERE hash = 'l1'", ct))
            .ShouldBe("pending", "the renamed row invalidates for the embed drain");
        (await ScalarAsync(connection, "SELECT embedding FROM entries WHERE hash = 'l1'", ct))
            .ShouldBeNull();
        (await ScalarAsync(connection,
                "SELECT count(*) FROM vec_entries v JOIN entries e ON e.id = v.rowid WHERE e.hash = 'l1'", ct))
            .ShouldBe(0, "the stale ctx-tagged vec legs drop the instant the invalidation commits");
        (await ScalarAsync(connection,
                "SELECT count(*) FROM vec_structure v JOIN entries e ON e.id = v.rowid WHERE e.hash = 'l1'", ct))
            .ShouldBe(0);
        (await ScalarAsync(connection,
                "SELECT count(*) FROM vec_code v JOIN code_entries e ON e.id = v.rowid WHERE e.project_id = 'jsaa' AND e.hash = 'c-l'", ct))
            .ShouldBe(0, "the folded code row's stale vec leg drops too");
        (await ScalarAsync(connection, "SELECT embed_state FROM entries WHERE hash = 'w1'", ct))
            .ShouldBe("embedded", "invalidation is renamed-rows-only: winner rows never flicker");
        (await ScalarAsync(connection,
                "SELECT count(*) FROM vec_entries v JOIN entries e ON e.id = v.rowid WHERE e.hash = 'w1'", ct))
            .ShouldBe(1, "the winner's vec leg survives untouched");
        var ftsHits = await connection.QueryAsync<string>(
            "SELECT e.hash FROM entries_fts f JOIN entries e ON e.id = f.rowid " +
            "WHERE e.project_id = 'jsaa' AND entries_fts MATCH 'gamma'");
        ftsHits.ShouldContain("l1", "a project_id rewrite is an FTS trigger no-op — no resync step exists");

        // The drain's own write (same trigger the embedder fires): the row becomes visible under
        // the winner ctx the vector search filters on.
        var reembed = EmbeddingBlob.ToBytes(new float[384]);
        await connection.ExecuteAsync(
            "UPDATE entries SET embedding = @reembed, embed_state = 'embedded' WHERE hash = 'l1'",
            new { reembed });
        (await ScalarAsync(connection,
                "SELECT v.ctx FROM vec_entries v JOIN entries e ON e.id = v.rowid WHERE e.hash = 'l1'", ct))
            .ShouldBe("project:jsaa");
    }

    /// <summary>
    ///     The fold merges two chunk groups into one; the ordered ChunkIndexRepair job step
    ///     re-derives positions from the document, so no duplicate or gapped positions survive.
    ///     Mutation "skip the chunk step" leaves both rows at 5.
    /// </summary>
    [RetryFact]
    public async Task ChunkRenumber_Contiguous()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = Path.Combine(_dataRoot, "doc.md");
        await File.WriteAllTextAsync(file, "para one\n\npara two\n\npara three", ct);
        await using var connection = await _factory.OpenBankAsync(ct);
        var now = FixedNow.ToUnixTimeSeconds();
        await connection.ExecuteAsync(
            "INSERT INTO entries (hash, path, value, source_file, scope, project_id, context_label, created_at, updated_at, embed_state, chunk_index, total_chunks) " +
            "VALUES (@hash, @file, 'para one', @file, 'project', 'jsaa', 'ctx-a', @now, @now, 'pending', 5, 9)",
            new { hash = ContentHash.Of(file, "para one"), file, now });
        await connection.ExecuteAsync(
            "INSERT INTO entries (hash, path, value, source_file, scope, project_id, context_label, created_at, updated_at, embed_state, chunk_index, total_chunks) " +
            "VALUES (@hash, @file, 'para two', @file, 'project', 'job-search-ai-assistant', 'ctx-a', @now, @now, 'pending', 5, 9)",
            new { hash = ContentHash.Of(file, "para two"), file, now });
        await RequestRepairAsync(connection);

        await NewJob().RunAsync(connection, ct);

        var positions = (await connection.QueryAsync<(string Value, long ChunkIndex, long TotalChunks)>(
                "SELECT value AS Value, chunk_index AS ChunkIndex, total_chunks AS TotalChunks " +
                "FROM entries WHERE source_file = @file ORDER BY chunk_index",
                new { file })).ToList();
        positions.Select(p => p.Value).ShouldBe(["para one", "para two"]);
        positions.Select(p => p.ChunkIndex).ShouldBe([0L, 1L]);
        positions.Select(p => p.TotalChunks).ShouldBe([2L, 2L]);
    }

    private static async Task RequestRepairAsync(SqliteConnection connection) =>
        await connection.ExecuteAsync(MemorySql.RequestRepair,
            new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds() });

    private static async Task<long> CountAsync(SqliteConnection connection, string table, string projectId,
        string predicate, CancellationToken ct) =>
        // Table names are this test's own constants, never bank content.
        await connection.ExecuteScalarAsync<long>(
            $"SELECT count(*) FROM {table} WHERE project_id = @projectId AND ({predicate})",
            new { projectId });

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<object?>(new CommandDefinition(sql, cancellationToken: ct));

    private static async Task SeedClusterAsync(SqliteConnection connection, CancellationToken ct)
    {
        var now = FixedNow.ToUnixTimeSeconds();
        var contentVector = EmbeddingBlob.ToBytes(new float[384]);
        var structureVector = EmbeddingBlob.ToBytes(new float[384]);
        var codeVector = EmbeddingBlob.ToBytes(new float[768]);

        async Task Entry(string hash, string scope, string projectId, string? label, string value)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state) " +
                    "VALUES (@hash, @hash, @value, 'seed.md', 's', @scope, @projectId, @label, @now, @now, 'pending')",
                    new { hash, value, scope, projectId, label, now }, cancellationToken: ct));
        }

        async Task EmbedEntry(string hash)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE entries SET heading_path = 'h', structure_embedding = @structureVector WHERE hash = @hash",
                    new { structureVector, hash }, cancellationToken: ct));
            await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE entries SET embedding = @contentVector, embed_state = 'embedded' WHERE hash = @hash",
                    new { contentVector, hash }, cancellationToken: ct));
        }

        async Task Queue(string projectId, string hash, double score, long created, long updated)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO promotion_queue (project_id, hash, value, score, created_at, updated_at) " +
                    "VALUES (@projectId, @hash, @hash, @score, @created, @updated)",
                    new { projectId, hash, score, created, updated }, cancellationToken: ct));
        }

        async Task Code(string projectId, string hash, bool embedded)
        {
            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                    "INSERT INTO code_entries (hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at, embed_state) " +
                    "VALUES (@hash, 'a.cs', @hash, 'a.cs', 1, 9, @projectId, @now, @now, 'pending') RETURNING id",
                    new { hash, projectId, now }, cancellationToken: ct));
            if (embedded)
            {
                await connection.ExecuteAsync(new CommandDefinition(
                        "UPDATE code_entries SET embedding = @embedding, embed_state = 'embedded' WHERE id = @id",
                        new { embedding = codeVector, id }, cancellationToken: ct));
            }
        }

        // Winner canonical: two labeled rows (one embedded with vec legs), a dup-collision row, a bulk row.
        await Entry("w1", "project", Winner, "ctx-a", "winner one alpha");
        await Entry("w2", "project", Winner, "ctx-a", "winner two beta");
        await Entry("dup-hash-1", "project", Winner, "ctx-a", "dup content");
        await Entry("w-bulk", "project", Winner, null, "winner bulk");
        await EmbedEntry("w1");
        await Queue(Winner, "q-w1", 0.9, 10, 12);
        await Queue(Winner, "q-share", 0.6, 10, 10);
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO promotion_discards (project_id, hash, discarded_at) VALUES ('jsaa', 'd-w', @now)",
                new { now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO search_quality (correlation_id, query, project_id, created_at) VALUES ('q-w', 'q', 'jsaa', @now)",
                new { now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO watches (project_id, path, created_at, last_change_ts, scan_owner, scan_lease_expires_at) " +
                "VALUES ('jsaa', '/repo/a', @now, @now, 'owner-w', 999)",
                new { now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO watch_files (project_id, path, file_hash, updated_at) VALUES ('jsaa', '/repo/a', 'h', @now)",
                new { now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO watch_digest_claims (project_id, path, claimed_at) VALUES ('jsaa', '/repo/a', @now)",
                new { now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO settings (key, value) VALUES ('ingest.scope.jsaa', '[\"/x\"]')", cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO sync_tombstones (project_id, hash, scope, deleted_at) VALUES ('jsaa', 't-gone', 'project', @now)",
                new { now }, cancellationToken: ct));
        await Code(Winner, "c-w", true);

        // Loser: labeled rows (one embedded), a dup colliding with the winner, bulk/custom/shared
        // rows the S2 predicate deliberately leaves, queue + split-hash queue, pending code.
        await Entry("l1", "project", Loser, "ctx-a", "loser one gamma");
        await Entry("l2", "project", Loser, "ctx-a", "loser two delta");
        await Entry("dup-hash-1", "project", Loser, "ctx-a", "dup content");
        await Entry("l-bulk", "project", Loser, null, "loser bulk");
        await Entry("l-custom", "custom", Loser, "ctx-a", "loser custom");
        await Entry("l-shared", "shared", Loser, "ctx-a", "loser shared");
        await EmbedEntry("l1");
        await Queue(Loser, "q-l1", 0.7, 9, 11);
        await Queue(Loser, "q-share", 0.8, 8, 14);
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO promotion_discards (project_id, hash, discarded_at) VALUES (@loser, 'd-l', @now)",
                new { loser = Loser, now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO search_quality (correlation_id, query, project_id, created_at) VALUES ('q-l', 'q', @loser, @now)",
                new { loser = Loser, now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO watches (project_id, path, created_at, last_change_ts) VALUES (@loser, '/repo/b', @now, @now)",
                new { loser = Loser, now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO watch_files (project_id, path, file_hash, updated_at) VALUES (@loser, '/repo/b', 'h', @now)",
                new { loser = Loser, now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO watch_digest_claims (project_id, path, claimed_at) VALUES (@loser, '/repo/b', @now)",
                new { loser = Loser, now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO settings (key, value) VALUES " +
                "('ingest.scope.job-search-ai-assistant', '[\"/y\"]'), ('watch.enabled.job-search-ai-assistant', 'true'), " +
                "('access.mode.project:job-search-ai-assistant', 'full')", cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO sync_tombstones (project_id, hash, scope, deleted_at) VALUES (@loser, 't-loser-gone', 'project', @now)",
                new { loser = Loser, now }, cancellationToken: ct));
        await Code(Loser, "c-l", true);
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO metrics (name, kind, value, unit, project_id, recorded_at) VALUES ('m', 'k', 1, 'u', @loser, @now)",
                new { loser = Loser, now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO workspaces (id, project_id, status, created_at) VALUES ('ws-1', @loser, 'open', @now)",
                new { loser = Loser, now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, workspace_id, created_at, updated_at, embed_state) " +
                "VALUES ('ws-1', 'ws-1', 'ws-1', 'seed.md', 's', NULL, @loser, NULL, 'ws-1', @now, @now, 'pending')",
                new { loser = Loser, now }, cancellationToken: ct));

        // Guid loser registered under its pre-guid name: folds via the name, like live 01a062f4.
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO projects (id, name, created_at) VALUES (@id, @loser, @now)",
                new { id = GuidLoser, loser = Loser, now }, cancellationToken: ct));
        await Entry("g1", "project", GuidLoser, "ctx-a", "guid one epsilon");

        // Drop candidates and a retired zero-row guid.
        await Entry("n1", "project", DroppedQa, "ctx-a", "noise residue");
        await Queue(DroppedQa, "nq", 0.5, 1, 1);
        await Entry("s1", "project", DroppedSweep, "ctx-a", "sweep residue");
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO watches (project_id, path, created_at, last_change_ts) VALUES (@sweep, '/repo', @now, @now)",
                new { sweep = DroppedSweep, now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO projects (id, name, created_at) VALUES (@sweep, @sweep, @now)",
                new { sweep = DroppedSweep, now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO projects (id, name, created_at) VALUES (@id, 'ai-badger', @now)",
                new { id = RetiredGuid, now }, cancellationToken: ct));

        // True-typo control: never attributed, never touched.
        await Entry("t1", "project", Typo, "ctx-a", "typo control");
    }
}
