using AiRaccoon.Core.Projects;
using CommunityToolkit.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>Per-surface rows a project-ids repair apply moved, merged, deleted or invalidated.</summary>
public sealed record ProjectIdsRepairResult(
    int EntriesMoved,
    int EntriesDeduped,
    int CodeMoved,
    int CodeDeduped,
    int QueueMerged,
    int QueueMoved,
    int QueueRemoved,
    int DiscardsMoved,
    int QualityMoved,
    int WatchesMoved,
    int SettingsRenamed,
    int ProjectsEnsured,
    int ProjectsRemoved,
    int TombstonesRewritten,
    int TombstonesCreated,
    int VecInvalidated,
    int CodeVecInvalidated)
{
    /// <summary>Zero after a converged run — the second-run-no-op assertion reads this.</summary>
    public int TotalChanges =>
        EntriesMoved + EntriesDeduped + CodeMoved + CodeDeduped + QueueMerged + QueueMoved + QueueRemoved +
        DiscardsMoved + QualityMoved + WatchesMoved + SettingsRenamed + ProjectsEnsured + ProjectsRemoved +
        TombstonesRewritten + TombstonesCreated + VecInvalidated + CodeVecInvalidated;
}

/// <summary>
///     The apply half of the project-ids repair (air-merge P2): folds every
///     <see cref="ProjectIdsFoldPlan" /> loser into its winner across every id-keyed surface the P1
///     census enumerates, deletes drop-listed ids with a tombstone per removed hash, and retires
///     zero-entry projects rows. Each step runs in its own BEGIN IMMEDIATE transaction
///     (per-batch serialization with ToolGate writers via busy_timeout + WAL — there is no ToolGate
///     lock to hold, review MUST-1), so a failed step rolls back to the pre-step bank and the open
///     repair_requests row retries it on the next maintenance pass.
///     <para>
///         Single-pass (d-426 SHOULD-5): the plan derives from the diagnose-time census, so a
///         write under a folded id landing mid-apply re-creates the loser key — operators quiesce
///         writers or loop derive→apply until a run reports no folds (the --apply receipt states this).
///     </para>
///     <para>
///         Entries scope rule (review S2): only <c>scope = 'project'</c> rows with a non-NULL
///         context_label move — NULL-context bulk rows and custom/shared partitions stay
///         byte-identical. Dropped ids delete all committed-scope rows instead (there is no winner
///         to preserve them under). Metrics, noise, workspaces and workspace scratch are never
///         touched; <c>source_id</c> rides along untouched (memory_source is content-keyed and
///         local-only). Chunk renumber is NOT here — <see cref="Maintenance.ProjectIdsRepairJob" />
///         runs <see cref="Ingestion.ChunkIndexRepair" /> as an ordered job step after this, and the
///         existing PendingEmbedJob/CodeReindexJob drain the rows this invalidates.
///     </para>
/// </summary>
public sealed class ProjectIdsRepair(TimeProvider timeProvider)
{
    /// <summary>Applies the whole plan, step by step; returns the per-surface counts.</summary>
    public async Task<ProjectIdsRepairResult> ApplyAsync(SqliteConnection connection, ProjectIdsFoldPlan plan,
        CancellationToken cancellationToken = default)
    {
        Guard.IsNotNull(connection);
        Guard.IsNotNull(plan);
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        var vecInvalidated = await InvalidateVecAsync(connection, plan, cancellationToken).ConfigureAwait(false);
        var codeVecInvalidated = await InvalidateCodeVecAsync(connection, plan, cancellationToken).ConfigureAwait(false);
        // Queue BEFORE entries (review MUST-1): the entries-dedup DELETE below fires
        // promotion_queue_entries_ad, which eats queue rows still keyed by the loser id — the
        // normal production shape is a queue hash that IS an entries hash, so folding entries
        // first silently drops candidates the queue fold would have merged.
        var (queueMerged, queueMoved, queueRemoved) =
            await FoldQueueAsync(connection, plan, cancellationToken).ConfigureAwait(false);
        var (entriesMoved, entriesDeduped, entriesTombstoned) =
            await FoldEntriesAsync(connection, plan, now, cancellationToken).ConfigureAwait(false);
        var (codeMoved, codeDeduped) = await FoldCodeAsync(connection, plan, cancellationToken).ConfigureAwait(false);
        var discardsMoved = await FoldDiscardsAsync(connection, plan, cancellationToken).ConfigureAwait(false);
        var qualityMoved = await FoldQualityAsync(connection, plan, cancellationToken).ConfigureAwait(false);
        var watchesMoved = await FoldWatchesAsync(connection, plan, cancellationToken).ConfigureAwait(false);
        var settingsRenamed = await FoldSettingsAsync(connection, plan, cancellationToken).ConfigureAwait(false);
        var (projectsEnsured, projectsRemoved) =
            await FoldProjectsAsync(connection, plan, now, cancellationToken).ConfigureAwait(false);
        var tombstonesRewritten =
            await FoldTombstonesAsync(connection, plan, cancellationToken).ConfigureAwait(false);
        return new ProjectIdsRepairResult(entriesMoved, entriesDeduped, codeMoved, codeDeduped,
            queueMerged, queueMoved, queueRemoved, discardsMoved, qualityMoved, watchesMoved, settingsRenamed,
            projectsEnsured, projectsRemoved, tombstonesRewritten, entriesTombstoned,
            vecInvalidated, codeVecInvalidated);
    }

    /// <summary>
    ///     Targeted vec invalidation (review MUST-2, unconditional): vec KNN filters on a ctx that
    ///     embeds the project id, and a plain project_id UPDATE fires neither vec trigger — without
    ///     this the renamed rows go invisible under the winner. Renamed-ids-only,
    ///     embedded-state-only: the embed_state flip fires vec_entries_pending/vec_structure_pending
    ///     immediately (stale ctx rows drop on commit) and the existing PendingEmbedJob drains the
    ///     pending rows. FTS needs no resync (entries_fts_au fires only on value/source_file/section).
    ///     Runs BEFORE the rewrite so the loser predicate still matches.
    /// </summary>
    private static async Task<int> InvalidateVecAsync(SqliteConnection connection, ProjectIdsFoldPlan plan,
        CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var fold in plan.Folds)
        {
            total += await InWriteTransactionAsync(connection, () => connection.ExecuteAsync(
                    new CommandDefinition(
                        $"""
                        UPDATE entries
                        SET embed_state = 'pending', embedding = NULL,
                            structure_embedding = NULL, heading_path = NULL
                        WHERE embed_state = 'embedded'
                          AND {ProjectRows.LabeledProjectScope("", "loser")}
                        """,
                        new { loser = fold.Loser }, cancellationToken: cancellationToken)),
                cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    private static async Task<int> InvalidateCodeVecAsync(SqliteConnection connection, ProjectIdsFoldPlan plan,
        CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var fold in plan.Folds)
        {
            total += await InWriteTransactionAsync(connection, () => connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE code_entries
                        SET embed_state = 'pending', embedding = NULL
                        WHERE embed_state = 'embedded' AND project_id = @loser
                        """,
                        new { loser = fold.Loser }, cancellationToken: cancellationToken)),
                cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    private static async Task<(int Moved, int Deduped, int Tombstoned)> FoldEntriesAsync(SqliteConnection connection,
        ProjectIdsFoldPlan plan, long now, CancellationToken cancellationToken)
    {
        var moved = 0;
        var deduped = 0;
        var tombstoned = 0;
        foreach (var fold in plan.Folds)
        {
            var step = await InWriteTransactionAsync(connection, async () =>
            {
                // Content-addressed fold: a row whose (path, hash, label) already lives under the
                // winner is the same content — the winner's row survives and the loser's deletes
                // with NO tombstone. Tombstones are created only for genuinely removed content
                // (the dropped path below): a dedup tombstone is redundant for suppression (the
                // unique committed bucket already dedups the content) and purely destructive on
                // pull, where the apply-tombstones site deletes live winner rows other replicas
                // still serve (review MUST-2). The queue fold already ran ahead of this step, so
                // the queue legs are winner-keyed and promotion_queue_entries_ad (which matches
                // OLD.project_id = loser) never touches the merged candidates.
                var stepMoved = await connection.ExecuteAsync(new CommandDefinition(
                        $"""
                        UPDATE entries SET project_id = @winner
                        WHERE {ProjectRows.LabeledProjectScope("", "loser")}
                          AND NOT EXISTS (
                              SELECT 1 FROM entries w
                              WHERE {ProjectRows.ProjectScope("w.", "winner")}
                                AND w.path = entries.path AND w.hash = entries.hash
                                AND COALESCE(w.context_label, '') = COALESCE(entries.context_label, ''))
                        """,
                        new { winner = fold.Winner, loser = fold.Loser }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                var stepDeleted = await connection.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM entries WHERE " + ProjectRows.LabeledProjectScope("", "loser"),
                        new { loser = fold.Loser }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                return (Moved: stepMoved, Deleted: stepDeleted);
            }, cancellationToken).ConfigureAwait(false);
            moved += step.Moved;
            deduped += step.Deleted;
        }

        foreach (var dropped in plan.Dropped)
        {
            var step = await InWriteTransactionAsync(connection, async () =>
            {
                var stepTombstoned = await connection.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT OR IGNORE INTO sync_tombstones (project_id, hash, scope, deleted_at)
                        SELECT project_id, hash, scope, @now FROM entries
                        WHERE project_id = @dropped AND scope IN ('project', 'custom', 'shared')
                        """,
                        new { dropped, now }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                var stepDeleted = await connection.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM entries WHERE project_id = @dropped AND scope IN ('project', 'custom', 'shared')",
                        new { dropped }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                return (Tombstoned: stepTombstoned, Deleted: stepDeleted);
            }, cancellationToken).ConfigureAwait(false);
            tombstoned += step.Tombstoned;
            deduped += step.Deleted;
        }

        return (moved, deduped, tombstoned);
    }

    private static async Task<(int Moved, int Deduped)> FoldCodeAsync(SqliteConnection connection,
        ProjectIdsFoldPlan plan, CancellationToken cancellationToken)
    {
        var moved = 0;
        var deduped = 0;
        foreach (var fold in plan.Folds)
        {
            var step = await InWriteTransactionAsync(connection, async () =>
            {
                // Code never syncs, so a leftover is an exact (path, hash) duplicate of a winner
                // row — dropped outright, no tombstone. code_fts/code-vec shadows are rowid-keyed:
                // the UPDATE is a trigger no-op for them, the DELETE cleans them via code_fts_ad.
                var stepMoved = await connection.ExecuteAsync(new CommandDefinition(
                        """
                        UPDATE code_entries SET project_id = @winner
                        WHERE project_id = @loser
                          AND NOT EXISTS (
                              SELECT 1 FROM code_entries w
                              WHERE w.project_id = @winner
                                AND w.path = code_entries.path AND w.hash = code_entries.hash)
                        """,
                        new { winner = fold.Winner, loser = fold.Loser }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                var stepDeleted = await connection.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM code_entries WHERE project_id = @loser",
                        new { loser = fold.Loser }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                return (Moved: stepMoved, Deduped: stepDeleted);
            }, cancellationToken).ConfigureAwait(false);
            moved += step.Moved;
            deduped += step.Deduped;
        }

        foreach (var dropped in plan.Dropped)
        {
            deduped += await InWriteTransactionAsync(connection, () => connection.ExecuteAsync(
                    new CommandDefinition("DELETE FROM code_entries WHERE project_id = @dropped",
                        new { dropped }, cancellationToken: cancellationToken)),
                cancellationToken).ConfigureAwait(false);
        }

        return (moved, deduped);
    }

    /// <summary>
    ///     Queue conflict rule (review MUST-3): the same hash under both ids keeps the max score
    ///     and the min created_at; every other column stays the winner's. Off-scorer territory —
    ///     this merges stored rows, never recomputes a score.
    /// </summary>
    private static async Task<(int Merged, int Moved, int Removed)> FoldQueueAsync(SqliteConnection connection,
        ProjectIdsFoldPlan plan, CancellationToken cancellationToken)
    {
        var merged = 0;
        var moved = 0;
        var removed = 0;
        foreach (var fold in plan.Folds)
        {
            var step = await InWriteTransactionAsync(connection, async () =>
            {
                var stepMerged = await connection.ExecuteAsync(new CommandDefinition(
                        """
                        UPDATE promotion_queue AS w
                        SET score = MAX(w.score, (SELECT l.score FROM promotion_queue l
                                                 WHERE l.project_id = @loser AND l.hash = w.hash)),
                            created_at = MIN(w.created_at, (SELECT l.created_at FROM promotion_queue l
                                                            WHERE l.project_id = @loser AND l.hash = w.hash)),
                            updated_at = MAX(w.updated_at, (SELECT l.updated_at FROM promotion_queue l
                                                            WHERE l.project_id = @loser AND l.hash = w.hash))
                        WHERE w.project_id = @winner
                          AND EXISTS (SELECT 1 FROM promotion_queue l
                                      WHERE l.project_id = @loser AND l.hash = w.hash)
                        """,
                        new { winner = fold.Winner, loser = fold.Loser }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                var stepMoved = await connection.ExecuteAsync(new CommandDefinition(
                        """
                        UPDATE promotion_queue SET project_id = @winner
                        WHERE project_id = @loser
                          AND NOT EXISTS (SELECT 1 FROM promotion_queue w
                                          WHERE w.project_id = @winner AND w.hash = promotion_queue.hash)
                        """,
                        new { winner = fold.Winner, loser = fold.Loser }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                var stepRemoved = await connection.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM promotion_queue WHERE project_id = @loser",
                        new { loser = fold.Loser }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                return (Merged: stepMerged, Moved: stepMoved, Removed: stepRemoved);
            }, cancellationToken).ConfigureAwait(false);
            merged += step.Merged;
            moved += step.Moved;
            removed += step.Removed;
        }

        foreach (var dropped in plan.Dropped)
        {
            removed += await InWriteTransactionAsync(connection, () => connection.ExecuteAsync(
                    new CommandDefinition("DELETE FROM promotion_queue WHERE project_id = @dropped",
                        new { dropped }, cancellationToken: cancellationToken)),
                cancellationToken).ConfigureAwait(false);
        }

        return (merged, moved, removed);
    }

    /// <summary>
    ///     Discards are keyed (project_id, hash) and must follow the fold (review MUST-3) or a
    ///     prior agent-'no' re-proposes under the winner. INSERT OR IGNORE keeps the winner's row
    ///     on the rare same-hash collision.
    /// </summary>
    private static async Task<int> FoldDiscardsAsync(SqliteConnection connection, ProjectIdsFoldPlan plan,
        CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var fold in plan.Folds)
        {
            total += await InWriteTransactionAsync(connection, async () =>
            {
                await connection.ExecuteAsync(new CommandDefinition(
                        """
                        INSERT OR IGNORE INTO promotion_discards (project_id, hash, discarded_at)
                        SELECT @winner, hash, discarded_at FROM promotion_discards WHERE project_id = @loser
                        """,
                        new { winner = fold.Winner, loser = fold.Loser }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                return await connection.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM promotion_discards WHERE project_id = @loser",
                        new { loser = fold.Loser }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }

        foreach (var dropped in plan.Dropped)
        {
            total += await InWriteTransactionAsync(connection, () => connection.ExecuteAsync(
                    new CommandDefinition("DELETE FROM promotion_discards WHERE project_id = @dropped",
                        new { dropped }, cancellationToken: cancellationToken)),
                cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    private static async Task<int> FoldQualityAsync(SqliteConnection connection, ProjectIdsFoldPlan plan,
        CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var fold in plan.Folds)
        {
            total += await InWriteTransactionAsync(connection, () => connection.ExecuteAsync(
                    new CommandDefinition("UPDATE search_quality SET project_id = @winner WHERE project_id = @loser",
                        new { winner = fold.Winner, loser = fold.Loser }, cancellationToken: cancellationToken)),
                cancellationToken).ConfigureAwait(false);
        }

        // Dropped ids are test residue: their quality rows delete with them rather than floating
        // unattributed (NULL project_id) in the metric.
        foreach (var dropped in plan.Dropped)
        {
            total += await InWriteTransactionAsync(connection, () => connection.ExecuteAsync(
                    new CommandDefinition("DELETE FROM search_quality WHERE project_id = @dropped",
                        new { dropped }, cancellationToken: cancellationToken)),
                cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    /// <summary>
    ///     Watch renames run as UPDATEs preserving scan_owner/lease (review SHOULD-2, P4's
    ///     in-flight-scan case relies on it); on a same-path collision the winner's registration
    ///     survives. A cross-store transient is impossible here — every table folds in place, no
    ///     DELETE+INSERT round trip. Internal (not private) so the in-flight-scan test can drive the
    ///     real step in isolation (d-427 SHOULD-3) — a hand-SQL replay of this statement would drift
    ///     from it silently.
    /// </summary>
    internal static async Task<int> FoldWatchesAsync(SqliteConnection connection, ProjectIdsFoldPlan plan,
        CancellationToken cancellationToken)
    {
        var total = 0;
        // Table names are this method's own constants, never bank content.
        foreach (var table in new[] { "watches", "watch_files", "watch_digest_claims" })
        {
            foreach (var fold in plan.Folds)
            {
                total += await InWriteTransactionAsync(connection, async () =>
                {
                    // UPDATE-first preserves the row (and its scan lease) in the common case;
                    // a same-path leftover means the winner already holds that path, so it deletes.
                    var stepMoved = await connection.ExecuteAsync(new CommandDefinition(
                            $"UPDATE {table} SET project_id = @winner WHERE project_id = @loser " +
                            $"AND NOT EXISTS (SELECT 1 FROM {table} w WHERE w.project_id = @winner AND w.path = {table}.path)",
                            new { winner = fold.Winner, loser = fold.Loser }, cancellationToken: cancellationToken))
                        .ConfigureAwait(false);
                    var stepRemoved = await connection.ExecuteAsync(new CommandDefinition(
                            $"DELETE FROM {table} WHERE project_id = @loser",
                            new { loser = fold.Loser }, cancellationToken: cancellationToken))
                        .ConfigureAwait(false);
                    return stepMoved + stepRemoved;
                }, cancellationToken).ConfigureAwait(false);
            }

            foreach (var dropped in plan.Dropped)
            {
                total += await InWriteTransactionAsync(connection, () => connection.ExecuteAsync(
                        new CommandDefinition($"DELETE FROM {table} WHERE project_id = @dropped",
                            new { dropped }, cancellationToken: cancellationToken)),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return total;
    }

    /// <summary>
    ///     Settings keys embed the raw id (AI-RACCOON vs ai-raccoon own disjoint keys while sharing
    ///     entries — lane A finding c). Only the five id-keyed prefixes rename; on a collision the
    ///     winner's value stands and the loser key deletes.
    /// </summary>
    private static async Task<int> FoldSettingsAsync(SqliteConnection connection, ProjectIdsFoldPlan plan,
        CancellationToken cancellationToken)
    {
        var total = 0;
        foreach (var fold in plan.Folds)
        {
            var pairs = SettingsKeyPairs(fold.Loser, fold.Winner).ToList();
            total += await InWriteTransactionAsync(connection, async () =>
            {
                var handled = 0;
                foreach (var (loserKey, winnerKey) in pairs)
                {
                    var loserExists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                            "SELECT count(*) FROM settings WHERE key = @key",
                            new { key = loserKey }, cancellationToken: cancellationToken))
                        .ConfigureAwait(false) > 0;
                    if (!loserExists)
                    {
                        continue;
                    }

                    var winnerExists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                            "SELECT count(*) FROM settings WHERE key = @key",
                            new { key = winnerKey }, cancellationToken: cancellationToken))
                        .ConfigureAwait(false) > 0;
                    if (winnerExists)
                    {
                        handled += await connection.ExecuteAsync(new CommandDefinition(
                                "DELETE FROM settings WHERE key = @key",
                                new { key = loserKey }, cancellationToken: cancellationToken))
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        handled += await connection.ExecuteAsync(new CommandDefinition(
                                "UPDATE settings SET key = @winnerKey WHERE key = @loserKey",
                                new { winnerKey, loserKey }, cancellationToken: cancellationToken))
                            .ConfigureAwait(false);
                    }
                }

                return handled;
            }, cancellationToken).ConfigureAwait(false);
        }

        foreach (var dropped in plan.Dropped)
        {
            var keys = SettingsKeysFor(dropped);
            total += await InWriteTransactionAsync(connection, () => connection.ExecuteAsync(
                    new CommandDefinition("DELETE FROM settings WHERE key IN @keys",
                        new { keys }, cancellationToken: cancellationToken)),
                cancellationToken).ConfigureAwait(false);
        }

        return total;
    }

    private static IEnumerable<(string Loser, string Winner)> SettingsKeyPairs(string loser, string winner) =>
        SettingsKeysFor(loser).Zip(SettingsKeysFor(winner));

    private static List<string> SettingsKeysFor(string projectId) =>
    [
        $"ingest.scope.{projectId}",
        $"watch.scope.{projectId}",
        $"watch.enabled.{projectId}",
        $"watch.concurrency.{projectId}",
        $"access.mode.project:{projectId}"
    ];

    /// <summary>
    ///     Projects rows never sync back — the pushed snapshot carries the projects table
    ///     off-machine untouched but the merge never reads remote.projects
    ///     (docs/work/2026-09-03-air-merge-p1-trace-answers.md §(a): survives (untouched) / NEVER syncs [back]),
    ///     so every replica folds its own registry: the
    ///     winner row is ensured (first-write-wins name = the id itself, matching auto-register),
    ///     then loser, dropped and retired rows delete.
    /// </summary>
    private static async Task<(int Ensured, int Removed)> FoldProjectsAsync(SqliteConnection connection,
        ProjectIdsFoldPlan plan, long now, CancellationToken cancellationToken)
    {
        var ensured = 0;
        var removed = 0;
        var winners = plan.Folds.Select(fold => fold.Winner).Distinct(StringComparer.Ordinal).ToList();
        if (winners.Count > 0)
        {
            ensured = await InWriteTransactionAsync(connection, async () =>
            {
                var count = 0;
                foreach (var winner in winners)
                {
                    count += await connection.ExecuteAsync(new CommandDefinition(
                            "INSERT OR IGNORE INTO projects (id, name, created_at) VALUES (@winner, @winner, @now)",
                            new { winner, now }, cancellationToken: cancellationToken))
                        .ConfigureAwait(false);
                }

                return count;
            }, cancellationToken).ConfigureAwait(false);
        }

        var removals = plan.Folds.Select(fold => fold.Loser)
            .Concat(plan.Dropped)
            .Concat(plan.RetiredProjects)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (removals.Count > 0)
        {
            removed = await InWriteTransactionAsync(connection, () => connection.ExecuteAsync(
                    new CommandDefinition("DELETE FROM projects WHERE id IN @ids",
                        new { ids = removals }, cancellationToken: cancellationToken)),
                cancellationToken).ConfigureAwait(false);
        }

        return (ensured, removed);
    }

    /// <summary>
    ///     Tombstone PK rewrite (review M4a): surviving tombstones follow the fold so a pull from
    ///     an unrepaired replica keeps suppressing the loser push (the merge compares the folded
    ///     id — SyncService folds loser ids on pull). Creation lives next to the rows it describes
    ///     in <see cref="FoldEntriesAsync" />'s dropped path only — never for dedup-collapsed
    ///     duplicates (review MUST-2). On a same-(hash, scope) collision the later delete wins.
    /// </summary>
    private static async Task<int> FoldTombstonesAsync(SqliteConnection connection,
        ProjectIdsFoldPlan plan, CancellationToken cancellationToken)
    {
        var rewritten = 0;
        foreach (var fold in plan.Folds)
        {
            rewritten += await InWriteTransactionAsync(connection, async () =>
            {
                await connection.ExecuteAsync(new CommandDefinition(
                        """
                        UPDATE sync_tombstones AS w
                        SET deleted_at = MAX(w.deleted_at, (SELECT l.deleted_at FROM sync_tombstones l
                                                            WHERE l.project_id = @loser
                                                              AND l.hash = w.hash AND l.scope = w.scope))
                        WHERE w.project_id = @winner
                          AND EXISTS (SELECT 1 FROM sync_tombstones l
                                      WHERE l.project_id = @loser AND l.hash = w.hash AND l.scope = w.scope)
                        """,
                        new { winner = fold.Winner, loser = fold.Loser }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                var stepMoved = await connection.ExecuteAsync(new CommandDefinition(
                        """
                        UPDATE sync_tombstones SET project_id = @winner
                        WHERE project_id = @loser
                          AND NOT EXISTS (
                              SELECT 1 FROM sync_tombstones w
                              WHERE w.project_id = @winner
                                AND w.hash = sync_tombstones.hash AND w.scope = sync_tombstones.scope)
                        """,
                        new { winner = fold.Winner, loser = fold.Loser }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                var stepMerged = await connection.ExecuteAsync(new CommandDefinition(
                        "DELETE FROM sync_tombstones WHERE project_id = @loser",
                        new { loser = fold.Loser }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                return stepMoved + stepMerged;
            }, cancellationToken).ConfigureAwait(false);
        }

        // Created counts ride on the entries step (dropped deletes tombstone inline, next to the
        // rows they describe) — this step rewrites only.
        return rewritten;
    }

    /// <summary>One step, one BEGIN IMMEDIATE transaction: writers serialize on the write lock instead of failing with SQLITE_BUSY.</summary>
    private static async Task<T> InWriteTransactionAsync<T>(SqliteConnection connection, Func<Task<T>> step,
        CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
        {
            var result = await step().ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            return result;
        }
        catch
        {
            await connection.ExecuteAsync(new CommandDefinition("ROLLBACK", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            throw;
        }
    }
}
