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
    // Open-workspace pin control (D3, review #614): attributed to the winner but never folded.
    private const string PinnedWs = "pinned-workspace-id";

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot = TestData.CreateTempRoot("project-ids-repair-job");
    private readonly SqliteConnectionFactory _factory;

    public ProjectIdsRepairJobTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private ProjectIdsRepairJob NewJob(Microsoft.Extensions.Logging.ILogger<ProjectIdsRepairJob>? logger = null) =>
        new(new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]),
            TestData.CreateEmbeddingService(), new FakeTimeProvider(FixedNow), logger);

    /// <summary>
    ///     Package F result-reporting hunk: a requested run logs a per-pass receipt naming the
    ///     applied plan counts and the rows the applier moved — the server-side half of the
    ///     CLI loop's per-pass receipts (the CLI re-derives its own half from census reads).
    ///     Mutation ledger — drop-pass-receipt-log : --filter RequestedRun_LogsPerPassReceiptWithMovedCounts : seeded cluster + open request.
    /// </summary>
    [RetryFact]
    public async Task RequestedRun_LogsPerPassReceiptWithMovedCounts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await SeedClusterAsync(connection, ct);
        await RequestRepairAsync(connection);
        var sink = new CapturingLogger<ProjectIdsRepairJob>();

        var created = await NewJob(sink).RunAsync(connection, ct);

        created.ShouldBeTrue("the seeded bank owns loser rows on every moveable surface");
        var receipt = sink.Entries.Single(
            entry => entry.EventId == ProjectIdsRepairJob.PassReceiptEventId);
        receipt.Message.ShouldContain("moved");
    }

    /// <summary>Captures <see cref="Microsoft.Extensions.Logging.ILogger{T}" /> output for receipt assertions.</summary>
    private sealed class CapturingLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
    {
        public List<(int EventId, string Message)> Entries { get; } = [];

        IDisposable? Microsoft.Extensions.Logging.ILogger.BeginScope<TState>(TState state) => null;

        bool Microsoft.Extensions.Logging.ILogger.IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        void Microsoft.Extensions.Logging.ILogger.Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Add((eventId.Id, formatter(state, exception)));
        }
    }

    [RetryFact]
    public async Task HasWorkAsync_WithNoOpenRequest_IsFalse()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        (await NewJob().HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    /// <summary>Outbox probe: a committed request row is work. Ledger — wrong-kind-check : --filter HasWorkAsync_AfterARequest_IsTrue : requested bank.</summary>
    [RetryFact]
    public async Task HasWorkAsync_AfterARequest_IsTrue()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await RequestRepairAsync(connection);

        (await NewJob().HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    /// <summary>
    ///     Gating decision (review SHOULD-1, gate-on-open-request): the runner also invokes RunAsync on
    ///     the IsDue first pass (missing ledger row), and that pass must not fold — the dropped path
    ///     permanently deletes rows, so folding without a human --apply would contradict the CLI contract.
    ///     Mutation ledger — no-request-run-folds : --filter RunAsync_WithoutAnOpenRequest_LeavesTheBankUntouched :
    ///     seeded cluster with no request row (any fold, delete, or tombstone fails the untouched asserts).
    /// </summary>
    [RetryFact]
    public async Task RunAsync_WithoutAnOpenRequest_LeavesTheBankUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await SeedClusterAsync(connection, ct);

        var changed = await NewJob().RunAsync(connection, ct);

        changed.ShouldBeFalse("with no open request there is nothing the job may do");
        (await CountAsync(connection, "entries", Loser, "scope = 'project' AND context_label IS NOT NULL", ct))
            .ShouldBe(4, "loser rows stay put — l1, l2 and the two dup-collision rows");
        (await CountAsync(connection, "entries", DroppedQa, "1 = 1", ct))
            .ShouldBe(1, "dropped rows especially must never delete without a human --apply");
        (await ScalarAsync(connection, "SELECT count(*) FROM sync_tombstones", ct))
            .ShouldBe(4, "no tombstone is created or rewritten either");
        (await ScalarAsync(connection, "SELECT count(*) FROM repair_requests WHERE kind = 'project-ids'", ct))
            .ShouldBe(0, "the gated run finishes nothing and requests nothing");
    }

    /// <summary>
    ///     The P3 enforcement gate's migration marker: a requested run stamps the repair_requests finished
    ///     row (never the maintenance_jobs ledger stamp, which the runner writes after every RunAsync call
    ///     including gated no-ops). Mutation ledger — skip-FinishRepairRequest :
    ///     --filter RequestedRun_StampsTheFinishedRequestMarker : empty bank + open request.
    /// </summary>
    [RetryFact]
    public async Task RequestedRun_StampsTheFinishedRequestMarker()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await RequestRepairAsync(connection);

        await NewJob().RunAsync(connection, ct);

        (await ScalarAsync(connection,
                "SELECT finished_at FROM repair_requests WHERE kind = 'project-ids'", ct))
            .ShouldNotBeNull("a completed requested run leaves the finished marker P3 gates on");
        (await NewJob().HasWorkAsync(connection, ct)).ShouldBeFalse();
    }

    /// <summary>
    ///     ADR-0099: a request row with a null map behaves as the empty map — entries and drops
    ///     stay put, but the content-free registry retire still fires (the only mutation an empty
    ///     map can schedule) and the run still stamps the finished marker (an --apply without
    ///     --map on a clean bank is a legal no-op, never a crash).
    /// </summary>
    [RetryFact]
    public async Task RequestedRun_WithNullMapJson_PlansEmptyButStampsFinished()
    {
        var ct = TestContext.Current.CancellationToken;
        var retiredGuid = "01a03024-0000-7000-8000-000000000099";
        await using var connection = await _factory.OpenBankAsync(ct);
        await SeedClusterAsync(connection, ct);
        await connection.ExecuteAsync(
            "INSERT INTO projects (id, name, created_at) VALUES (@id, 'retired-zero-row', @now)",
            new { id = retiredGuid, now = FixedNow.ToUnixTimeSeconds() });
        await connection.ExecuteAsync(MemorySql.RequestRepair,
            new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = (string?)null });

        var changed = await NewJob().RunAsync(connection, ct);

        _ = changed; // chunk re-derivation may still report work; the map assertions are below.
        (await CountAsync(connection, "entries", Loser, "scope = 'project' AND context_label IS NOT NULL", ct))
            .ShouldBeGreaterThan(0, "loser rows stay put without a map");
        (await CountAsync(connection, "entries", DroppedQa, "1 = 1", ct))
            .ShouldBeGreaterThan(0, "dropped rows especially must never delete without a map");
        (await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM projects WHERE id = @id", new { id = retiredGuid }, cancellationToken: ct)))
            .ShouldBe(0, "the content-free registry row still retires under the empty map");
        (await ScalarAsync(connection,
                "SELECT finished_at FROM repair_requests WHERE kind = 'project-ids'", ct))
            .ShouldNotBeNull();
    }

    /// <summary>
    ///     A stored map that bypassed endpoint validation (direct SQL) must not crash the poll:
    ///     the run refuses to fold, leaves the request open (a corrected --apply re-runs it),
    ///     and stamps nothing.
    /// </summary>
    [RetryFact]
    public async Task RequestedRun_WithGarbageMapJson_RefusesWithoutStamping()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await SeedClusterAsync(connection, ct);
        await connection.ExecuteAsync(MemorySql.RequestRepair,
            new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = "{ not json" });

        (await NewJob().RunAsync(connection, ct)).ShouldBeFalse();
        (await CountAsync(connection, "entries", Loser, "scope = 'project' AND context_label IS NOT NULL", ct))
            .ShouldBeGreaterThan(0, "loser rows stay put when the map is garbage");
        (await ScalarAsync(connection,
                "SELECT finished_at FROM repair_requests WHERE kind = 'project-ids'", ct))
            .ShouldBeNull("no stamp — the corrected request must still be owed");
        (await NewJob().HasWorkAsync(connection, ct)).ShouldBeTrue();
    }

    /// <summary>
    ///     First run folds every moveable surface and reports it; the immediate second run changes
    ///     nothing. Fixture: ≥2 ids × ≥2 rows, both queue fragments populated, plus the
    ///     tombstone/code/quality/watch/settings legs so an entries-only rewrite still fails.
    ///     Mutation ledger — mutation : narrowest filter : fixture row: skip-entries-rewrite :
    ///     --filter Repair_FirstRunMovesRows_SecondRunNoOp : loser-labeled-rows (assertion 1);
    ///     entries-before-queue : same : qdup-1 queue+entries pair (candidate eaten by the trigger);
    ///     reintroduce-dedup-tombstone : same : dup-hash-1/qdup-1 absence asserts; skip-queue-merge :
    ///     same : split queue (merged score/created/updated); skip-tombstone-rewrite : same :
    ///     t-loser-gone; skip-tombstone-max-merge : same : t-collide pair; skip-discards-follow :
    ///     same : d-l/d-share; skip-dropped-quality-delete : same : q-n; skip-dropped-watch-delete :
    ///     same : sweep /repo watch; skip-settings-prefix : same : watch.scope/watch.concurrency keys.
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
            .ShouldBe(7, "w1, w2, w-dup, qdup-1, l1, l2 and g1 meet under the winner");
        (await CountAsync(connection, "promotion_queue", Loser, "1 = 1", ct)).ShouldBe(0);
        (await CountAsync(connection, "promotion_queue", Winner, "1 = 1", ct)).ShouldBe(4);
        (await ScalarAsync(connection,
                "SELECT count(*) FROM promotion_queue WHERE project_id = 'jsaa' AND hash = 'qdup-1'", ct))
            .ShouldBe(1, "the same-hash candidate survives: the queue fold runs before the entries-dedup " +
                "DELETE whose promotion_queue_entries_ad trigger would otherwise eat it");
        var merged = await connection.QuerySingleAsync<(double Score, long Created, long Updated)>(
            "SELECT score AS Score, created_at AS Created, updated_at AS Updated FROM promotion_queue " +
            "WHERE project_id = @winner AND hash = 'q-share'", new { winner = Winner });
        merged.Score.ShouldBe(0.8, "the queue conflict keeps the max score");
        merged.Created.ShouldBe(8, "the queue conflict keeps the min created_at");
        merged.Updated.ShouldBe(14, "the queue conflict keeps the max updated_at");
        (await CountAsync(connection, "promotion_discards", Winner, "1 = 1", ct)).ShouldBe(3);
        (await ScalarAsync(connection,
                "SELECT discarded_at FROM promotion_discards WHERE project_id = 'jsaa' AND hash = 'd-share'", ct))
            .ShouldBe(FixedNow.ToUnixTimeSeconds(), "on a same-hash discards collision the winner's row stands");
        (await CountAsync(connection, "search_quality", Winner, "1 = 1", ct)).ShouldBe(2);
        (await CountAsync(connection, "code_entries", Loser, "1 = 1", ct)).ShouldBe(0);
        (await CountAsync(connection, "code_entries", Winner, "1 = 1", ct)).ShouldBe(2);
        (await CountAsync(connection, "sync_tombstones", Loser, "1 = 1", ct)).ShouldBe(0,
            "every surviving loser tombstone rewrites to the winner");
        (await CountAsync(connection, "sync_tombstones", Winner, "1 = 1", ct)).ShouldBe(3,
            "t-gone, the rewritten t-loser-gone, and the merged t-collide meet under the winner");
        (await ScalarAsync(connection,
                "SELECT deleted_at FROM sync_tombstones WHERE project_id = 'jsaa' AND hash = 't-collide'", ct))
            .ShouldBe(FixedNow.ToUnixTimeSeconds(), "on a same-(hash, scope) tombstone collision the later delete wins");
        (await ScalarAsync(connection,
                "SELECT count(*) FROM sync_tombstones WHERE project_id = 'jsaa' AND hash IN ('dup-hash-1', 'qdup-1')", ct))
            .ShouldBe(0, "dedup-collapsed duplicates create no tombstone — only genuinely removed content does");
        (await CountAsync(connection, "sync_tombstones", DroppedQa, "1 = 1", ct)).ShouldBe(1);
        (await CountAsync(connection, "sync_tombstones", DroppedSweep, "1 = 1", ct)).ShouldBe(1);
        (await ScalarAsync(connection, "SELECT value FROM settings WHERE key = 'ingest.scope.jsaa'", ct))
            .ShouldBe("[\"/x\"]", "on a settings collision the winner's value stands");
        (await ScalarAsync(connection, "SELECT count(*) FROM settings WHERE key = 'ingest.scope.job-search-ai-assistant'", ct))
            .ShouldBe(0);
        (await ScalarAsync(connection, "SELECT value FROM settings WHERE key = 'watch.enabled.jsaa'", ct))
            .ShouldBe("true");
        (await ScalarAsync(connection, "SELECT value FROM settings WHERE key = 'watch.scope.jsaa'", ct))
            .ShouldBe("[\"/y\"]");
        (await ScalarAsync(connection, "SELECT value FROM settings WHERE key = 'watch.concurrency.jsaa'", ct))
            .ShouldBe("4");
        (await ScalarAsync(connection,
                "SELECT count(*) FROM settings WHERE key LIKE '%job-search-ai-assistant'", ct))
            .ShouldBe(0, "every id-embedding loser key renames across all five prefixes");
        (await ScalarAsync(connection, "SELECT value FROM settings WHERE key = 'access.mode.project:jsaa'", ct))
            .ShouldBe("full");
        (await CountAsync(connection, "search_quality", DroppedQa, "1 = 1", ct)).ShouldBe(0,
            "dropped-id quality rows delete with their id rather than floating unattributed");
        (await CountAsync(connection, "promotion_queue", DroppedQa, "1 = 1", ct)).ShouldBe(0);
        (await CountAsync(connection, "watches", DroppedSweep, "1 = 1", ct)).ShouldBe(0,
            "dropped-id watches delete with their id");
        (await ScalarAsync(connection, "SELECT name FROM projects WHERE id = 'jsaa'", ct))
            .ShouldBe("jsaa", "the unregistered winner canonical gains its registry row");
        (await ScalarAsync(connection, "SELECT count(*) FROM projects WHERE id IN ('" + GuidLoser + "','" + RetiredGuid + "','" + DroppedSweep + "')", ct))
            .ShouldBe(0);
        (await CountAsync(connection, "watches", Winner, "1 = 1", ct)).ShouldBe(2);
        (await ScalarAsync(connection, "SELECT scan_owner FROM watches WHERE project_id = 'jsaa' AND path = '/repo/a'", ct))
            .ShouldBe("owner-w", "watch renames preserve the scan lease columns");
        // Folded vs untouched partitions (D1 committed predicate + D3 telemetry ownership):
        // NULL-context bulk and custom rows fold with the labeled rows; the loser metrics row
        // re-keys to the winner with the fold (C1); shared rows (cross-project, H9) and typo rows
        // stay byte-identical. The open-workspace id below pins wholesale (D3, review #614).
        (await CountAsync(connection, "entries", Loser, "context_label IS NULL AND workspace_id IS NULL", ct)).ShouldBe(0,
            "D1: the loser bulk row folds to the winner");
        (await CountAsync(connection, "entries", Loser, "scope = 'custom'", ct)).ShouldBe(0,
            "D1: the loser custom row folds to the winner");
        (await CountAsync(connection, "entries", Winner, "scope = 'project' AND context_label IS NULL", ct)).ShouldBe(2,
            "winner bulk plus the folded loser bulk meet under the winner");
        (await CountAsync(connection, "entries", Winner, "scope = 'custom'", ct)).ShouldBe(1,
            "the folded loser custom row lands under the winner with its scope intact");
        (await CountAsync(connection, "entries", Loser, "scope = 'shared'", ct)).ShouldBe(1);
        // D3 workspace block (review #614): the attributed open-workspace id pins wholesale — its
        // scratch stays byte-identical and nothing lands under the winner, even with a valid map.
        (await CountAsync(connection, "entries", PinnedWs, "workspace_id IS NOT NULL", ct)).ShouldBe(1);
        (await CountAsync(connection, "workspaces", PinnedWs, "1 = 1", ct)).ShouldBe(1);
        (await CountAsync(connection, "entries", Winner, "workspace_id IS NOT NULL", ct)).ShouldBe(0);
        (await CountAsync(connection, "entries", Typo, "1 = 1", ct)).ShouldBe(1);
        (await CountAsync(connection, "metrics", Loser, "1 = 1", ct)).ShouldBe(0,
            "C1: the loser metrics row re-keys to the winner with the fold");
        (await CountAsync(connection, "metrics", Winner, "1 = 1", ct)).ShouldBe(1,
            "C1: telemetry follows the fold — regenerable, never verdict-blocking, never stranded");

        var second = await NewJob().RunAsync(connection, ct);

        second.ShouldBeFalse("a converged bank schedules no further work");
        (await NewJob().HasWorkAsync(connection, ct)).ShouldBeFalse();
        (await CountAsync(connection, "entries", Winner, "scope = 'project' AND context_label IS NOT NULL", ct))
            .ShouldBe(7, "the second run moves nothing further");
        (await CountAsync(connection, "promotion_queue", Winner, "1 = 1", ct)).ShouldBe(4);
    }

    /// <summary>
    ///     MUST-2: renamed rows invalidate (pending + NULLs, stale vec legs gone) while the winner's
    ///     own embedded rows stay embedded; FTS still finds the moved content without any resync;
    ///     and a re-embed lands under the winner ctx the KNN filters on.
    ///     Ledger — skip-vec-invalidation : --filter FtsVecResync_AfterRename : embedded loser row l1;
    ///     add-an-FTS-resync-step is the over-budgeted inverse (trigger no-op assertion pins its absence).
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
    ///     Ledger — skip-chunk-step : --filter ChunkRenumber_Contiguous : duplicate positions.
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

    private static string FixtureMapJson() =>
        new ProjectIdAliasMap(
                [new ProjectIdAliasEntry("job-search-ai-assistant", "jsaa"), new ProjectIdAliasEntry("AI-RACCOON", "ai-raccoon"), new ProjectIdAliasEntry("pinned-workspace-id", "jsaa")],
                ["jsaa", "ai-badger", "ai-raccoon", "hermes-default", "deepseek-harness", "arasz-home-page", "vue-kanban", "dotnet-ignore", "interview-tasks"],
                ["qa-noise-project", "manual-sweep"]).ToJson();

    private static async Task RequestRepairAsync(SqliteConnection connection, string? mapJson = null) =>
        await connection.ExecuteAsync(MemorySql.RequestRepair,
            new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds(), mapJson = mapJson ?? FixtureMapJson() });

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
        // Same-hash queue+entries production shape (review MUST-1): the content dedups under the
        // winner while the queue candidate must survive — only because the queue fold runs first.
        await Entry("qdup-1", "project", Winner, "ctx-a", "shared candidate content");
        await Entry("w-bulk", "project", Winner, null, "winner bulk");
        await EmbedEntry("w1");
        await Queue(Winner, "q-w1", 0.9, 10, 12);
        await Queue(Winner, "q-share", 0.6, 10, 10);
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO promotion_discards (project_id, hash, discarded_at) VALUES ('jsaa', 'd-w', @now), ('jsaa', 'd-share', @now)",
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
                "INSERT INTO sync_tombstones (project_id, hash, scope, deleted_at) VALUES ('jsaa', 't-gone', 'project', @now), " +
                "('jsaa', 't-collide', 'project', @now)",
                new { now }, cancellationToken: ct));
        await Code(Winner, "c-w", true);

        // Loser: labeled rows (one embedded), a dup colliding with the winner, bulk + custom rows
        // the D1 predicate folds, a shared row it never touches (H9), queue + split-hash queue, pending code.
        await Entry("l1", "project", Loser, "ctx-a", "loser one gamma");
        await Entry("l2", "project", Loser, "ctx-a", "loser two delta");
        await Entry("dup-hash-1", "project", Loser, "ctx-a", "dup content");
        await Entry("qdup-1", "project", Loser, "ctx-a", "shared candidate content");
        await Entry("l-bulk", "project", Loser, null, "loser bulk");
        await Entry("l-custom", "custom", Loser, "ctx-a", "loser custom");
        await Entry("l-shared", "shared", Loser, "ctx-a", "loser shared");
        await EmbedEntry("l1");
        await Queue(Loser, "q-l1", 0.7, 9, 11);
        await Queue(Loser, "q-share", 0.8, 8, 14);
        await Queue(Loser, "qdup-1", 0.5, 7, 7);
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO promotion_discards (project_id, hash, discarded_at) VALUES (@loser, 'd-l', @now), (@loser, 'd-share', @earlier)",
                new { loser = Loser, now, earlier = now - 50 }, cancellationToken: ct));
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
                "('access.mode.project:job-search-ai-assistant', 'full'), " +
                "('watch.scope.job-search-ai-assistant', '[\"/y\"]'), ('watch.concurrency.job-search-ai-assistant', '4')", cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO sync_tombstones (project_id, hash, scope, deleted_at) VALUES (@loser, 't-loser-gone', 'project', @now), " +
                "(@loser, 't-collide', 'project', @earlier)",
                new { loser = Loser, now, earlier = now - 50 }, cancellationToken: ct));
        await Code(Loser, "c-l", true);
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO metrics (name, kind, value, unit, project_id, recorded_at) VALUES ('m', 'k', 1, 'u', @loser, @now)",
                new { loser = Loser, now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO workspaces (id, project_id, status, created_at) VALUES ('ws-1', @loser, 'open', @now)",
                new { loser = PinnedWs, now }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, workspace_id, created_at, updated_at, embed_state) " +
                "VALUES ('ws-1', 'ws-1', 'ws-1', 'seed.md', 's', NULL, @loser, NULL, 'ws-1', @now, @now, 'pending')",
                new { loser = PinnedWs, now }, cancellationToken: ct));

        // Guid loser registered under its pre-guid name: folds via the name, like live 01a062f4.
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO projects (id, name, created_at) VALUES (@id, @loser, @now)",
                new { id = GuidLoser, loser = Loser, now }, cancellationToken: ct));
        await Entry("g1", "project", GuidLoser, "ctx-a", "guid one epsilon");

        // Drop candidates and a retired zero-row guid.
        await Entry("n1", "project", DroppedQa, "ctx-a", "noise residue");
        await Queue(DroppedQa, "nq", 0.5, 1, 1);
        // Dropped-id quality disposition (review SHOULD-2): the delete below must take this row.
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO search_quality (correlation_id, query, project_id, created_at) VALUES ('q-n', 'q', @dropped, @now)",
                new { dropped = DroppedQa, now }, cancellationToken: ct));
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
