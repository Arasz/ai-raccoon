using AiRaccoon.Core.Projects;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Projects;

/// <summary>
///     Package B gate: the fold applier works the committed predicate (project+custom, any label),
///     not the d-426 labeled-only keep. Each test seeds its own scratch bank and drives the public
///     <see cref="ProjectIdsRepair.ApplyAsync" /> with a hand-built plan — no job, no CLI.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ProjectIdsRepairFoldCommittedTests : IDisposable
{
    private const string Winner = "jsaa";
    private const string Loser = "job-search-ai-assistant";

    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot = TestData.CreateTempRoot("project-ids-fold-committed");
    private readonly SqliteConnectionFactory _factory;

    public ProjectIdsRepairFoldCommittedTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>
    ///     B AC1: project-NULL + custom-labeled (+ custom-NULL) loser rows fold to zero loser rows
    ///     in one apply; the winner's own NULL bulk row stays put (the fold re-keys loser ids only).
    ///     Ledger — narrow-keep-predicate : --filter FoldCommittedScopes_MovesNullContextAndCustomRowsToWinner :
    ///     labeled + NULL + custom + custom-NULL loser rows (a labeled-only applier leaves 3 behind).
    /// </summary>
    [RetryFact]
    public async Task FoldCommittedScopes_MovesNullContextAndCustomRowsToWinner()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await EntryAsync(connection, "w-labeled", "project", Winner, "ctx-a", "winner labeled", ct);
        await EntryAsync(connection, "w-bulk", "project", Winner, null, "winner bulk", ct);
        await EntryAsync(connection, "move-labeled", "project", Loser, "ctx-a", "loser labeled", ct);
        await EntryAsync(connection, "move-bulk", "project", Loser, null, "loser bulk", ct);
        await EntryAsync(connection, "move-custom", "custom", Loser, "ctx-a", "loser custom", ct);
        await EntryAsync(connection, "move-custom-bulk", "custom", Loser, null, "loser custom bulk", ct);
        (await CommittedCountAsync(connection, Loser, ct)).ShouldBe(4, "arrange: all four loser shapes are seated");

        var result = await ApplyAsync(connection, ct);

        (await CommittedCountAsync(connection, Loser, ct))
            .ShouldBe(0, "no committed loser row survives — NULL and custom fold with labeled");
        (await CountAsync(connection,
                "SELECT count(*) FROM entries WHERE project_id = @winner AND scope = 'project' AND context_label IS NOT NULL",
                ct, new { winner = Winner }))
            .ShouldBe(2, "winner labeled: its own plus the folded loser row");
        (await CountAsync(connection,
                "SELECT count(*) FROM entries WHERE project_id = @winner AND scope = 'project' AND context_label IS NULL",
                ct, new { winner = Winner }))
            .ShouldBe(2, "winner bulk: its own row stays, the folded loser bulk joins it");
        (await CountAsync(connection,
                "SELECT count(*) FROM entries WHERE project_id = @winner AND scope = 'custom'",
                ct, new { winner = Winner }))
            .ShouldBe(2, "both custom shapes land under the winner");
        result.EntriesMoved.ShouldBe(4, "all four loser rows move — none dedup, none stranded");
        result.EntriesDeduped.ShouldBe(0, "no collision exists, so the delete leg removes nothing");
    }

    /// <summary>
    ///     H9 (genuine shape, pin-with-reason): scope='shared' rows carry their writer's project_id
    ///     by construction (<c>EntryBucket.For</c> shared leg) and the fold never touches them;
    ///     workspace scratch stays too. Ledger — widen-to-shared :
    ///     --filter FoldCommittedScopes_LeavesSharedAndWorkspaceRowsUntouched : shared + workspace
    ///     loser rows beside one folding labeled row (a shared-folding applier moves the shared row).
    /// </summary>
    [RetryFact]
    public async Task FoldCommittedScopes_LeavesSharedAndWorkspaceRowsUntouched()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await EntryAsync(connection, "move-labeled", "project", Loser, "ctx-a", "loser labeled", ct);
        await EntryAsync(connection, "stay-shared", "shared", Loser, null, "shared content", ct);
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO workspaces (id, project_id, status, created_at) VALUES ('ws-1', @loser, 'open', 1)",
                new { loser = Loser }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, workspace_id, created_at, updated_at, embed_state) " +
                "VALUES ('stay-ws', 'stay-ws', 'scratch', 'seed.md', 's', NULL, @loser, NULL, 'ws-1', 1, 1, 'pending')",
                new { loser = Loser }, cancellationToken: ct));

        var result = await ApplyAsync(connection, ct);

        result.EntriesMoved.ShouldBe(1, "arrange proof: the fold ran — only the labeled row moves");
        (await CountAsync(connection,
                "SELECT count(*) FROM entries WHERE hash = 'stay-shared' AND scope = 'shared' AND project_id = @loser AND value = 'shared content'",
                ct, new { loser = Loser }))
            .ShouldBe(1, "the shared row is byte-identical under its writer's key");
        (await CountAsync(connection,
                "SELECT count(*) FROM entries WHERE hash = 'stay-ws' AND scope IS NULL AND workspace_id = 'ws-1'",
                ct))
            .ShouldBe(1, "workspace scratch never moves across projects");
    }

    /// <summary>
    ///     B AC2: a loser row whose (path, hash, scope, label) already lives under the winner dedups
    ///     with no tombstone — only genuinely removed content mints one. Ledger — reintroduce-dedup-tombstone :
    ///     --filter FoldCommittedScopes_DedupCollisionDeletesWithoutTombstone : winner/loser same-bucket pair.
    /// </summary>
    [RetryFact]
    public async Task FoldCommittedScopes_DedupCollisionDeletesWithoutTombstone()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await EntryAsync(connection, "dup", "project", Winner, "ctx-a", "dup content", ct);
        await EntryAsync(connection, "dup", "project", Loser, "ctx-a", "dup content", ct);
        await EntryAsync(connection, "move-labeled", "project", Loser, "ctx-a", "loser labeled", ct);

        var result = await ApplyAsync(connection, ct);

        (await CommittedCountAsync(connection, Loser, ct)).ShouldBe(0);
        (await CountAsync(connection,
                "SELECT count(*) FROM entries WHERE project_id = @winner AND path = 'dup' AND hash = 'dup'",
                ct, new { winner = Winner }))
            .ShouldBe(1, "the winner's row survives exactly once — no duplicate created");
        result.EntriesMoved.ShouldBe(1, "only the distinct row moves");
        result.EntriesDeduped.ShouldBe(1, "the colliding row deletes");
        result.TombstonesCreated.ShouldBe(0, "dedup-collapse mints no tombstone");
        (await CountAsync(connection, "SELECT count(*) FROM sync_tombstones", ct))
            .ShouldBe(0, "no tombstone row exists for dedup-collapsed content");
    }

    /// <summary>
    ///     B AC2 scope leg: the dedup tuple mirrors <c>uq_entries_committed_bucket</c> exactly —
    ///     same (path, hash, label) under a different scope is NOT a duplicate, so both rows meet
    ///     under the winner. Ledger — drop-scope-from-dedup-tuple :
    ///     --filter FoldCommittedScopes_CrossScopeSameContentIsNotADuplicate : project/custom same-path pair
    ///     (a scope-blind dedup deletes the loser row and reports it deduped).
    /// </summary>
    [RetryFact]
    public async Task FoldCommittedScopes_CrossScopeSameContentIsNotADuplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await EntryAsync(connection, "same", "project", Winner, "ctx-a", "same content", ct);
        await EntryAsync(connection, "same", "custom", Loser, "ctx-a", "same content", ct);

        var result = await ApplyAsync(connection, ct);

        (await CommittedCountAsync(connection, Loser, ct)).ShouldBe(0);
        (await CountAsync(connection,
                "SELECT count(*) FROM entries WHERE project_id = @winner AND path = 'same' AND hash = 'same'",
                ct, new { winner = Winner }))
            .ShouldBe(2, "project + custom buckets coexist under the winner — different tuples, no conflict");
        result.EntriesMoved.ShouldBe(1, "the cross-scope row moves, it must not delete as a duplicate");
        result.EntriesDeduped.ShouldBe(0);
    }

    /// <summary>
    ///     B AC3: embedded NULL + custom loser rows invalidate (pending, vec legs drop) while the
    ///     winner's own embedded row never flickers; FTS still finds the moved content with no resync.
    ///     Ledger — narrow-vec-predicate : --filter FoldCommittedScopes_InvalidatesVecForNullAndCustomRows :
    ///     embedded bulk + custom loser rows (a labeled-only invalidation leaves them embedded).
    /// </summary>
    [RetryFact]
    public async Task FoldCommittedScopes_InvalidatesVecForNullAndCustomRows()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await EntryAsync(connection, "w1", "project", Winner, "ctx-a", "winner one alpha", ct);
        await EmbedAsync(connection, "w1", ct);
        await EntryAsync(connection, "l-bulk", "project", Loser, null, "loser bulk zerobravo", ct);
        await EmbedAsync(connection, "l-bulk", ct);
        await EntryAsync(connection, "l-custom", "custom", Loser, "ctx-a", "loser custom yankeebeta", ct);
        await EmbedAsync(connection, "l-custom", ct);

        var result = await ApplyAsync(connection, ct);

        result.VecInvalidated.ShouldBe(2, "both broadened rows invalidate");
        foreach (var hash in new[] { "l-bulk", "l-custom" })
        {
            (await CountAsync(connection,
                    "SELECT count(*) FROM entries WHERE hash = @hash AND embed_state = 'pending' AND embedding IS NULL",
                    ct, new { hash }))
                .ShouldBe(1, $"{hash} invalidates for the embed drain");
            (await CountAsync(connection,
                    "SELECT count(*) FROM vec_entries v JOIN entries e ON e.id = v.rowid WHERE e.hash = @hash",
                    ct, new { hash }))
                .ShouldBe(0, $"the stale ctx-tagged vec leg for {hash} drops on commit");
            (await CountAsync(connection,
                    "SELECT count(*) FROM vec_structure v JOIN entries e ON e.id = v.rowid WHERE e.hash = @hash",
                    ct, new { hash }))
                .ShouldBe(0, $"the stale structure leg for {hash} drops too");
        }

        (await CountAsync(connection,
                "SELECT count(*) FROM entries WHERE hash = 'w1' AND embed_state = 'embedded'", ct))
            .ShouldBe(1, "invalidation is renamed-rows-only: the winner row never flickers");
        (await CountAsync(connection,
                "SELECT count(*) FROM vec_entries v JOIN entries e ON e.id = v.rowid WHERE e.hash = 'w1'", ct))
            .ShouldBe(1, "the winner's vec leg survives untouched");
        var ftsHits = await connection.QueryAsync<string>(
            "SELECT e.hash FROM entries_fts f JOIN entries e ON e.id = f.rowid " +
            "WHERE e.project_id = @winner AND entries_fts MATCH 'zerobravo'", new { winner = Winner });
        ftsHits.ShouldContain("l-bulk", "a project_id rewrite is an FTS trigger no-op — no resync step exists");
    }

    /// <summary>
    ///     H5 under the broadened predicate: the queue fold still runs before the entries-dedup
    ///     DELETE, so a same-hash queue candidate survives even when its backing entries row dedups —
    ///     for labeled and for NULL/custom rows alike. Ledger — entries-before-queue :
    ///     --filter FoldCommittedScopes_QueueCandidateSurvivesEntriesDedup : queue+entries same-hash pairs
    ///     (entries-first lets promotion_queue_entries_ad eat the loser candidate: merged score stays 0.6).
    /// </summary>
    [RetryFact]
    public async Task FoldCommittedScopes_QueueCandidateSurvivesEntriesDedup()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await EntryAsync(connection, "qdup", "project", Winner, "ctx-a", "shared candidate content", ct);
        await EntryAsync(connection, "qdup", "project", Loser, "ctx-a", "shared candidate content", ct);
        await QueueAsync(connection, Winner, "qdup", 0.6, ct);
        await QueueAsync(connection, Loser, "qdup", 0.8, ct);
        await EntryAsync(connection, "hmove", "custom", Loser, null, "custom bulk candidate", ct);
        await QueueAsync(connection, Loser, "hmove", 0.5, ct);

        var result = await ApplyAsync(connection, ct);

        (await CountAsync(connection,
                "SELECT count(*) FROM promotion_queue WHERE project_id = @winner", ct, new { winner = Winner }))
            .ShouldBe(2, "both candidates meet under the winner");
        (await CountAsync(connection,
                "SELECT count(*) FROM promotion_queue WHERE project_id = @loser", ct, new { loser = Loser }))
            .ShouldBe(0, "the loser queue key is absent after the fold");
        (await connection.ExecuteScalarAsync<double>(
                "SELECT score FROM promotion_queue WHERE project_id = @winner AND hash = 'qdup'",
                new { winner = Winner }))
            .ShouldBe(0.8, "the same-hash conflict keeps the max score — the queue fold merged before the entries delete");
        (await CountAsync(connection,
                "SELECT count(*) FROM entries WHERE project_id = @winner AND hash = 'hmove'", ct, new { winner = Winner }))
            .ShouldBe(1, "the custom-NULL backing row folds with its queue candidate");
        result.QueueMerged.ShouldBe(1, "the qdup pair merges");
        result.QueueMoved.ShouldBe(1, "the hmove candidate re-keys");
        result.EntriesDeduped.ShouldBe(1, "the qdup backing row dedups under the winner");
        result.EntriesMoved.ShouldBe(1, "the hmove backing row moves");
    }

    /// <summary>
    ///     Convergence: re-applying the same plan moves nothing — <c>TotalChanges == 0</c>.
    ///     Ledger — non-convergent-fold : --filter SecondApply_WithNothingLeftToFold_ReportsNoChanges :
    ///     labeled + bulk loser rows (any stranded row re-folds and reddens the zero).
    /// </summary>
    [RetryFact]
    public async Task SecondApply_WithNothingLeftToFold_ReportsNoChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _factory.OpenBankAsync(ct);
        await EntryAsync(connection, "move-labeled", "project", Loser, "ctx-a", "loser labeled", ct);
        await EntryAsync(connection, "move-bulk", "project", Loser, null, "loser bulk", ct);
        var plan = new ProjectIdsFoldPlan([new ProjectIdFold(Loser, Winner)], [], [], []);
        var repair = new ProjectIdsRepair(new FakeTimeProvider(FixedNow));

        var first = await repair.ApplyAsync(connection, plan, ct);
        var second = await repair.ApplyAsync(connection, plan, ct);

        first.TotalChanges.ShouldBeGreaterThan(0, "arrange: the first apply does real work");
        second.TotalChanges.ShouldBe(0, "a converged bank schedules no further work");
    }

    private static async Task<ProjectIdsRepairResult> ApplyAsync(SqliteConnection connection, CancellationToken ct)
    {
        var plan = new ProjectIdsFoldPlan([new ProjectIdFold(Loser, Winner)], [], [], []);
        return await new ProjectIdsRepair(new FakeTimeProvider(FixedNow)).ApplyAsync(connection, plan, ct);
    }

    private static async Task EntryAsync(SqliteConnection connection, string hash, string scope, string projectId,
        string? label, string value, CancellationToken ct) =>
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO entries (hash, path, value, source_file, section, scope, project_id, context_label, created_at, updated_at, embed_state) " +
                "VALUES (@hash, @hash, @value, 'seed.md', 's', @scope, @projectId, @label, 1, 1, 'pending')",
                new { hash, scope, projectId, label, value }, cancellationToken: ct));

    private static async Task EmbedAsync(SqliteConnection connection, string hash, CancellationToken ct)
    {
        var contentVector = EmbeddingBlob.ToBytes(new float[384]);
        var structureVector = EmbeddingBlob.ToBytes(new float[384]);
        await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE entries SET heading_path = 'h', structure_embedding = @structureVector WHERE hash = @hash",
                new { structureVector, hash }, cancellationToken: ct));
        await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE entries SET embedding = @contentVector, embed_state = 'embedded' WHERE hash = @hash",
                new { contentVector, hash }, cancellationToken: ct));
    }

    private static async Task QueueAsync(SqliteConnection connection, string projectId, string hash, double score,
        CancellationToken ct) =>
        await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO promotion_queue (project_id, hash, value, score, created_at, updated_at) " +
                "VALUES (@projectId, @hash, @hash, @score, 1, 1)",
                new { projectId, hash, score }, cancellationToken: ct));

    private static async Task<long> CommittedCountAsync(SqliteConnection connection, string projectId,
        CancellationToken ct) =>
        await CountAsync(connection,
            "SELECT count(*) FROM entries WHERE project_id = @projectId AND scope IN ('project', 'custom')",
            ct, new { projectId });

    private static async Task<long> CountAsync(SqliteConnection connection, string sql, CancellationToken ct,
        object? param = null) =>
        await connection.ExecuteScalarAsync<long>(new CommandDefinition(sql, param, cancellationToken: ct));
}
