using System.Globalization;
using System.Text.RegularExpressions;
using AiRaccoon.Core.Memory;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Single-file bank schema (see docs/work/2026-08-03-native-memory-plan.md §2.2): entries, workspaces, settings, an FTS5
///     external-content index (value, source_file, section) and content + structure vec0
///     tables. Idempotent on every bank open; legacy banks migrate (see MigrateAsync).
/// </summary>
internal static class MemorySchema
{
    // workspace_id exists now for the P8 structural-isolation wave; the FK and the
    // "workspace XOR committed scope" CHECK land with P8.
    private static readonly string Ddl = $"""
                               CREATE TABLE IF NOT EXISTS workspaces (
                                   id TEXT PRIMARY KEY,
                                   project_id TEXT NOT NULL,
                                   agent_id TEXT NULL,
                                   name TEXT NULL,
                                   status TEXT NOT NULL,
                                   created_at INTEGER NOT NULL,
                                   closed_at INTEGER NULL
                               );

                               CREATE TABLE IF NOT EXISTS entries (
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
                                   structure_embedding BLOB NULL,
                                   chunk_index INTEGER NOT NULL DEFAULT 0,
                                   total_chunks INTEGER NOT NULL DEFAULT 0,
                                   FOREIGN KEY (workspace_id) REFERENCES workspaces(id) ON DELETE RESTRICT,
                                   CHECK ((workspace_id IS NULL AND scope IN ('shared','project','custom')) OR (workspace_id IS NOT NULL AND scope IS NULL))
                               );

                               CREATE TABLE IF NOT EXISTS settings (
                                   key TEXT PRIMARY KEY,
                                   value TEXT NOT NULL
                               );

                               CREATE VIRTUAL TABLE IF NOT EXISTS entries_fts USING fts5(
                                   value,
                                   source_file,
                                   section,
                                   content='entries',
                                   content_rowid='id'
                               );

                               -- vec0 stays empty until the embed pipeline fills it; the embedder owns the embedding
                               -- dimension if the model is not all-MiniLM (384). `ctx` is the partition key
                               -- (docs/plans/2026-08-08-search-knn-perf.md §3.1): the vec0 KNN queries a
                               -- single partition instead of a WHERE filter, and cosine is declared
                               -- explicitly so a bare MATCH cannot silently fall back to L2.
                               CREATE VIRTUAL TABLE IF NOT EXISTS vec_entries USING vec0(ctx TEXT partition key, embedding float[384] distance_metric=cosine);

                               -- Structure modality: heading-path vectors, rowid = entry id.
                               -- Written by the embed transition (EntryEmbedder, ADR-0004);
                               -- the triggers below keep it consistent with entries.
                               CREATE VIRTUAL TABLE IF NOT EXISTS vec_structure USING vec0(ctx TEXT partition key, embedding float[384] distance_metric=cosine);

                               CREATE TRIGGER IF NOT EXISTS vec_structure_ad AFTER DELETE ON entries BEGIN
                                   DELETE FROM vec_structure WHERE rowid = OLD.id;
                               END;

                               -- Mirrors vec_entries_au: fires when the embed transition writes
                               -- structure_embedding (MarkEmbedded/MarkStructure).
                               CREATE TRIGGER IF NOT EXISTS vec_structure_au AFTER UPDATE OF structure_embedding ON entries
                               WHEN NEW.structure_embedding IS NOT NULL
                               BEGIN
                                   DELETE FROM vec_structure WHERE rowid = NEW.id;
                                   INSERT INTO vec_structure(rowid, ctx, embedding) VALUES (NEW.id, {MemorySql.ContextKeyExpression("NEW.")}, NEW.structure_embedding);
                               END;

                               -- Clear arm for vec_structure (#190): merge-reindex invalidates a row by
                               -- setting embed_state back to 'pending' (see SyncService's reindex UPDATE,
                               -- which also nulls structure_embedding/heading_path in the same statement) —
                               -- without this, the vec_structure row survives and structure search returns
                               -- an entry whose content vector was deliberately invalidated. Mirrors
                               -- vec_entries_pending exactly, keyed off embed_state, not structure_embedding.
                               CREATE TRIGGER IF NOT EXISTS vec_structure_pending AFTER UPDATE OF embed_state ON entries
                               WHEN NEW.embed_state = 'pending' AND OLD.embed_state = 'embedded'
                               BEGIN
                                   DELETE FROM vec_structure WHERE rowid = OLD.id;
                               END;

                               CREATE TRIGGER IF NOT EXISTS entries_fts_ai AFTER INSERT ON entries BEGIN
                                   INSERT INTO entries_fts(rowid, value, source_file, section)
                                   VALUES (new.id, new.value, new.source_file, new.section);
                               END;

                               CREATE TRIGGER IF NOT EXISTS entries_fts_ad AFTER DELETE ON entries BEGIN
                                   INSERT INTO entries_fts(entries_fts, rowid, value, source_file, section)
                                   VALUES ('delete', old.id, old.value, old.source_file, old.section);
                               END;

                               CREATE TRIGGER IF NOT EXISTS entries_fts_au AFTER UPDATE OF value, source_file, section ON entries BEGIN
                                   INSERT INTO entries_fts(entries_fts, rowid, value, source_file, section)
                                   VALUES ('delete', old.id, old.value, old.source_file, old.section);
                                   INSERT INTO entries_fts(rowid, value, source_file, section)
                                   VALUES (new.id, new.value, new.source_file, new.section);
                               END;

                               -- vec0 has no triggers: embedding rows follow embed_state.
                               -- Marking embedded upserts the vec row (delete-then-insert so a
                               -- re-embed replaces rather than duplicates); marking pending or
                               -- deleting the entry removes it.
                               CREATE TRIGGER IF NOT EXISTS vec_entries_au AFTER UPDATE OF embed_state ON entries
                               WHEN NEW.embed_state = 'embedded' AND NEW.embedding IS NOT NULL
                               BEGIN
                                   DELETE FROM vec_entries WHERE rowid = NEW.id;
                                   INSERT INTO vec_entries(rowid, ctx, embedding) VALUES (NEW.id, {MemorySql.ContextKeyExpression("NEW.")}, NEW.embedding);
                               END;

                               CREATE TRIGGER IF NOT EXISTS vec_entries_pending AFTER UPDATE OF embed_state ON entries
                               WHEN NEW.embed_state = 'pending' AND OLD.embed_state = 'embedded'
                               BEGIN
                                   DELETE FROM vec_entries WHERE rowid = OLD.id;
                               END;

                               CREATE TRIGGER IF NOT EXISTS vec_entries_ad AFTER DELETE ON entries BEGIN
                                   DELETE FROM vec_entries WHERE rowid = OLD.id;
                               END;

                               CREATE TABLE IF NOT EXISTS sync_meta (
                                   key TEXT PRIMARY KEY,
                                   value TEXT NOT NULL
                               );

                               CREATE TABLE IF NOT EXISTS sync_tombstones (
                                   hash TEXT NOT NULL,
                                   scope TEXT NOT NULL,
                                   deleted_at INTEGER NOT NULL,
                                   PRIMARY KEY (hash, scope)
                               );

                               -- File-watcher feature: persisted watch registrations and per-path
                               -- fingerprints (hash-skip). D3-normalized paths; runtime state is not persisted.
                               CREATE TABLE IF NOT EXISTS watches (
                                   project_id            TEXT NOT NULL,
                                   path                  TEXT NOT NULL,
                                   created_at            INTEGER NOT NULL,
                                   last_change_ts        INTEGER NOT NULL,       -- catch-up watermark (D1)
                                   scan_owner            TEXT NULL,              -- cross-process scan lease (D2)
                                   scan_lease_expires_at INTEGER NOT NULL DEFAULT 0,
                                   PRIMARY KEY (project_id, path)
                               );

                               CREATE TABLE IF NOT EXISTS watch_files (
                                   project_id      TEXT NOT NULL,
                                   path            TEXT NOT NULL,
                                   file_hash       TEXT NOT NULL,          -- SHA-256(path + full content)
                                   updated_at      INTEGER NOT NULL,
                                   PRIMARY KEY (project_id, path)
                               );

                               -- Propose tier: candidates waiting for promotion review. Separate from
                               -- entries by design — queue rows are never searchable and never counted
                               -- by memory_stats; capacity/eviction lives on this table only (shared
                               -- tier stays curated and sweep-exempt).
                               CREATE TABLE IF NOT EXISTS promotion_queue (
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

                               CREATE INDEX IF NOT EXISTS idx_entries_scope_project ON entries(scope, project_id);
                               CREATE INDEX IF NOT EXISTS idx_entries_hash ON entries(hash);
                               CREATE INDEX IF NOT EXISTS idx_entries_workspace ON entries(workspace_id);
                               CREATE INDEX IF NOT EXISTS idx_entries_embed_state ON entries(embed_state, project_id);
                               CREATE INDEX IF NOT EXISTS idx_watches_project ON watches(project_id);
                               CREATE INDEX IF NOT EXISTS idx_promotion_queue_project ON promotion_queue(project_id);
                               CREATE INDEX IF NOT EXISTS idx_promotion_queue_score ON promotion_queue(score);

                               """;


    /// <summary>
    ///     Bucket uniqueness (F3). Not part of <see cref="Ddl"/>: on a legacy bank the duplicates
    ///     must be deleted before the index can be created, which is the ladder's job. A fresh
    ///     bank has no rows to violate it, so it gets the indexes directly.
    /// </summary>
    private const string BucketIndexDdl = """
                                          CREATE UNIQUE INDEX IF NOT EXISTS uq_entries_shared_bucket
                                              ON entries(path, hash)
                                              WHERE scope = 'shared';

                                          CREATE UNIQUE INDEX IF NOT EXISTS uq_entries_committed_bucket
                                              ON entries(path, hash, project_id, scope, COALESCE(context_label, ''))
                                              WHERE scope IN ('project', 'custom');
                                          """;

    /// <summary>
    ///     The schema shape this build creates. Bumped by one per shipped schema change, with a
    ///     matching ladder step in <see cref="MigrateToV1Async"/>/<see cref="MigrateToV2Async"/>/
    ///     <see cref="MigrateToV3Async"/> (ADR-0011).
    /// </summary>
    internal const int CurrentVersion = 3;

    public static async Task EnsureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var storedVersion = await ReadVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        // EnsureAsync runs on the read-write open path (SqliteConnectionFactory.InitializeAsync) and
        // refuses a bank stamped newer than CurrentVersion rather than silently no-oping. Note the sync
        // merge path opens snapshots read-only and does not pass through here.
        if (storedVersion > CurrentVersion)
        {
            throw new UnsupportedSchemaVersionException(
                $"bank schema v{storedVersion} is newer than this binary supports (v{CurrentVersion}); update ai-raccoon");
        }

        // A pre-versioning bank and a brand-new file both read 0 and need opposite treatment.
        // What separates them is whether the file holds any table at all — checked before the
        // DDL creates them, and not keyed to one table: the oldest banks predate `entries`.
        var fresh = storedVersion == 0
                    && await connection.ExecuteScalarAsync<long>(
                        new CommandDefinition(
                            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'",
                            cancellationToken: cancellationToken)).ConfigureAwait(false) == 0;

        await using var command = connection.CreateCommand();
        command.CommandText = Ddl;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        // Runs on every open, version or not: it is a data move guarding a deny-by-default gate,
        // it costs one indexed count, and a bank that was stamped by a newer build and then
        // written by an older one would otherwise keep an unreachable scope forever.
        await MigrateIngestScopeKeysAsync(connection, cancellationToken).ConfigureAwait(false);

        if (fresh)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(BucketIndexDdl, cancellationToken: cancellationToken)).ConfigureAwait(false);
            await StampAsync(connection, CurrentVersion, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (storedVersion >= CurrentVersion)
        {
            return;
        }

        // Stamped only when the ladder completed: the v1 step's bucket-index dedupe is
        // deliberately soft (a bank must never fail to open), and a bank stamped past a step
        // that silently failed would never retry it. The v1 step must run — and its dedupe
        // complete — before the v2 step, or chunk-column numbering bakes in rows the dedupe
        // would have deleted (docs/plans/2026-08-08-search-knn-perf.md).
        var healthy = true;
        if (storedVersion < 1)
        {
            healthy = await MigrateToV1Async(connection, cancellationToken).ConfigureAwait(false);
        }

        if (healthy && storedVersion < 2)
        {
            // Hard step, deliberately not soft: an empty-shell vec_entries answers every vector
            // query with silence, which is worse than the bank failing to open.
            await MigrateToV2Async(connection, cancellationToken).ConfigureAwait(false);
        }

        if (healthy && storedVersion < 3)
        {
            // Hard step (issue #200): heals rows a pre-guard binary wrote into a v2 bank without
            // running the write-path chunk recompute.
            await MigrateToV3Async(connection, cancellationToken).ConfigureAwait(false);
        }

        if (healthy)
        {
            await StampAsync(connection, CurrentVersion, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     The scope allowlist bounds every disk-reading surface, not just watching, so its keys
    ///     moved from watch.scope.* to ingest.scope.* (1.2). Carrying pre-1.2 rows over is not
    ///     cosmetic: the scope is deny-by-default, so a bank that kept the old key would refuse
    ///     every ingest and every watch add after the upgrade.
    /// </summary>
    private static async Task MigrateIngestScopeKeysAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // Probe first: this runs on every bank open, and an unconditional write would take a
        // write lock every time — enough to change how a busy bank behaves under maintenance.
        var legacyScopeRows = await connection.ExecuteScalarAsync<long>(
                new CommandDefinition(
                    "SELECT count(*) FROM settings WHERE key LIKE 'watch.scope.%'",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        if (legacyScopeRows == 0)
        {
            return;
        }

        await connection.ExecuteAsync(
                new CommandDefinition(
                    """
                    UPDATE OR IGNORE settings
                       SET key = 'ingest.scope.' || substr(key, length('watch.scope.') + 1)
                     WHERE key LIKE 'watch.scope.%';
                    DELETE FROM settings WHERE key LIKE 'watch.scope.%';
                    """,
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    private static async Task<long> ReadVersionAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<long>(
            new CommandDefinition("PRAGMA user_version", cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>PRAGMA user_version takes no parameter binding, hence the interpolation of an int constant.</summary>
    private static async Task StampAsync(SqliteConnection connection, int version, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(
            new CommandDefinition($"PRAGMA user_version = {version}", cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    /// <summary>
    ///     Ladder step 1 — everything shipped before the version marker existed: the four entries
    ///     columns, the two watch-lease columns, the ingest-scope key move, the FTS rebuild and the
    ///     bucket-uniqueness indexes. Every check is idempotent. Returns false when the soft
    ///     bucket-index step did not complete, so the caller leaves the bank unstamped and the
    ///     next open retries. Must complete (its dedupe especially) before <see cref="MigrateToV2Async"/>.
    /// </summary>
    private static async Task<bool> MigrateToV1Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = (await connection.QueryAsync<string>(
                new CommandDefinition(
                    "SELECT name FROM pragma_table_info('entries')",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        if (!columns.Contains("source_file"))
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        "ALTER TABLE entries ADD COLUMN source_file TEXT",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        if (!columns.Contains("section"))
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        "ALTER TABLE entries ADD COLUMN section TEXT",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        if (!columns.Contains("heading_path"))
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        "ALTER TABLE entries ADD COLUMN heading_path TEXT",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        if (!columns.Contains("structure_embedding"))
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        "ALTER TABLE entries ADD COLUMN structure_embedding BLOB",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        // WP4 cross-process scan lease (D-2; docs/plans/2026-08-07-watch-scan-runaway-fix.md):
        // the lease lives on the watches row so DELETE FROM watches is lease release.
        var watchColumns = (await connection.QueryAsync<string>(
                new CommandDefinition(
                    "SELECT name FROM pragma_table_info('watches')",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        if (!watchColumns.Contains("scan_owner"))
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        "ALTER TABLE watches ADD COLUMN scan_owner TEXT",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        if (!watchColumns.Contains("scan_lease_expires_at"))
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        "ALTER TABLE watches ADD COLUMN scan_lease_expires_at INTEGER NOT NULL DEFAULT 0",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        var ftsSql = await connection.ExecuteScalarAsync<string?>(
                new CommandDefinition(
                    "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'entries_fts'",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        var ftsNeedsRebuild = ftsSql is null
                              || !ftsSql.Contains("source_file", StringComparison.Ordinal)
                              || !ftsSql.Contains("section", StringComparison.Ordinal);
        if (!ftsNeedsRebuild)
        {
            // New shape in place; verify it is not an empty shell left by a crash between the
            // DROP/CREATE and the repopulate — the triggers keep it in sync only from here on.
            var ftsRows = await connection.ExecuteScalarAsync<long>(
                    new CommandDefinition("SELECT count(*) FROM entries_fts", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            var entryRows = await connection.ExecuteScalarAsync<long>(
                    new CommandDefinition("SELECT count(*) FROM entries", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            if (ftsRows != entryRows)
            {
                ftsNeedsRebuild = true;
            }
        }

        if (ftsNeedsRebuild)
        {
            // The old index cannot host weighted source/section columns: drop it with its
            // triggers, recreate in the new shape, and repopulate from the content table.
            // One transaction, so a crash mid-rebuild cannot leave a bank without an FTS index
            // (or with an empty shell) that never heals on reopen.
            await connection.ExecuteAsync(
                    new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            try
            {
                await connection.ExecuteAsync(
                        new CommandDefinition(
                            """
                            DROP TRIGGER IF EXISTS entries_fts_ai;
                            DROP TRIGGER IF EXISTS entries_fts_ad;
                            DROP TRIGGER IF EXISTS entries_fts_au;
                            DROP TABLE IF EXISTS entries_fts;

                            CREATE VIRTUAL TABLE entries_fts USING fts5(
                                value,
                                source_file,
                                section,
                                content='entries',
                                content_rowid='id'
                            );

                            CREATE TRIGGER entries_fts_ai AFTER INSERT ON entries BEGIN
                                INSERT INTO entries_fts(rowid, value, source_file, section)
                                VALUES (new.id, new.value, new.source_file, new.section);
                            END;

                            CREATE TRIGGER entries_fts_ad AFTER DELETE ON entries BEGIN
                                INSERT INTO entries_fts(entries_fts, rowid, value, source_file, section)
                                VALUES ('delete', old.id, old.value, old.source_file, old.section);
                            END;

                            CREATE TRIGGER entries_fts_au AFTER UPDATE OF value, source_file, section ON entries BEGIN
                                INSERT INTO entries_fts(entries_fts, rowid, value, source_file, section)
                                VALUES ('delete', old.id, old.value, old.source_file, old.section);
                                INSERT INTO entries_fts(rowid, value, source_file, section)
                                VALUES (new.id, new.value, new.source_file, new.section);
                            END;

                            INSERT INTO entries_fts(rowid, value, source_file, section)
                            SELECT id, value, source_file, section FROM entries;
                            """,
                            cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                await connection.ExecuteAsync(
                        new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
            }
            catch
            {
                await connection.ExecuteAsync(
                        new CommandDefinition("ROLLBACK", cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                throw;
            }
        }

        // Bucket-uniqueness indexes (F3; see docs/work/2026-08-06-extraction-followups-plan.md):
        // one row per (path, hash) in the global shared tier, and per (path, hash, project,
        // scope, context-label bucket) in the committed tiers. A violating legacy bank (the
        // audited 14.2% duplicates) is deduped before the indexes are created, so creation
        // can never fail on a real bank; if the dedupe itself fails the bank stays open
        // (degraded — duplicates remain, indexes arrive on the next open). Deliberately
        // softer than the FTS rebuild above, which rethrows: a bank must never fail to open.
        var hasSharedIndex = await connection.ExecuteScalarAsync<long?>(
                new CommandDefinition(
                    "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = 'uq_entries_shared_bucket'",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false) is not null;
        var hasCommittedIndex = await connection.ExecuteScalarAsync<long?>(
                new CommandDefinition(
                    "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = 'uq_entries_committed_bucket'",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false) is not null;
        if (!hasSharedIndex || !hasCommittedIndex)
        {
            try
            {
                await connection.ExecuteAsync(
                        new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                try
                {
                    // Dedupe first (survivor = earliest row; content is identical within a
                    // group by construction — hash = SHA-256(path ‖ value)), then index.
                    // The GROUP BY mirrors the index expressions exactly (COALESCE included);
                    // NULL path/hash are guarded because GROUP BY equates them while the
                    // UNIQUE indexes treat them as distinct.
                    await connection.ExecuteAsync(
                            new CommandDefinition(
                                """
                                DELETE FROM entries
                                WHERE scope = 'shared'
                                  AND path IS NOT NULL AND hash IS NOT NULL
                                  AND id NOT IN (SELECT MIN(id) FROM entries
                                                 WHERE scope = 'shared'
                                                 GROUP BY path, hash);

                                DELETE FROM entries
                                WHERE scope IN ('project', 'custom')
                                  AND path IS NOT NULL AND hash IS NOT NULL
                                  AND id NOT IN (SELECT MIN(id) FROM entries
                                                 WHERE scope IN ('project', 'custom')
                                                 GROUP BY path, hash, project_id, scope,
                                                          COALESCE(context_label, ''));

                                CREATE UNIQUE INDEX IF NOT EXISTS uq_entries_shared_bucket
                                    ON entries(path, hash)
                                    WHERE scope = 'shared';

                                CREATE UNIQUE INDEX IF NOT EXISTS uq_entries_committed_bucket
                                    ON entries(path, hash, project_id, scope, COALESCE(context_label, ''))
                                    WHERE scope IN ('project', 'custom');
                                """,
                                cancellationToken: cancellationToken))
                        .ConfigureAwait(false);
                    await connection.ExecuteAsync(
                            new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                        .ConfigureAwait(false);
                }
                catch
                {
                    await connection.ExecuteAsync(
                            new CommandDefinition("ROLLBACK", cancellationToken: cancellationToken))
                        .ConfigureAwait(false);
                    throw;
                }
            }
            catch
            {
                // Degraded but open; the next open retries the dedupe + index creation.
                return false;
            }
        }

        return true;
    }

    private static readonly Regex VecDimensionPattern = new(@"float\[(\d+)\]", RegexOptions.Compiled);

    private const int DefaultEmbeddingDimension = 384;

    /// <summary>
    ///     Ladder step 2 (docs/plans/2026-08-08-search-knn-perf.md): persists chunk_index/total_chunks
    ///     (until now recomputed per query with a window function) and rebuilds vec_entries/vec_structure
    ///     with the vec0 partition key that lets the vector queries drop their WHERE filter for a native
    ///     KNN. One transaction, rethrown on failure — unlike the v1 step's soft bucket-index dedupe,
    ///     an empty-shell vec_entries answers every vector query with silence, which is worse than the
    ///     bank failing to open. Runs only after MigrateToV1Async's dedupe has completed, or the
    ///     chunk-column numbering would bake in rows the dedupe would have deleted.
    /// </summary>
    private static async Task MigrateToV2Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = (await connection.QueryAsync<string>(
                new CommandDefinition(
                    "SELECT name FROM pragma_table_info('entries')",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        if (!columns.Contains("chunk_index"))
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        "ALTER TABLE entries ADD COLUMN chunk_index INTEGER NOT NULL DEFAULT 0",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        if (!columns.Contains("total_chunks"))
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        "ALTER TABLE entries ADD COLUMN total_chunks INTEGER NOT NULL DEFAULT 0",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }

        // The embedder owns the dimension when the model is not all-MiniLM (384); read each
        // table's own declared dimension rather than re-hardcoding, so a bank on a different
        // model keeps its vectors through the rebuild.
        var entriesDimension = await ReadVecDimensionAsync(connection, "vec_entries", cancellationToken)
            .ConfigureAwait(false);
        var structureDimension = await ReadVecDimensionAsync(connection, "vec_structure", cancellationToken)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(
                new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(MemorySql.RecomputeChunkColumnsBankWide, cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            await RebuildVecTableAsync(connection, "vec_entries", entriesDimension, "embedding",
                    "embed_state = 'embedded' AND embedding IS NOT NULL", cancellationToken)
                .ConfigureAwait(false);
            await RebuildVecTableAsync(connection, "vec_structure", structureDimension, "structure_embedding",
                    "structure_embedding IS NOT NULL", cancellationToken)
                .ConfigureAwait(false);

            // vec_entries_pending/vec_entries_ad need no ctx and are unchanged in shape; dropped
            // and recreated anyway so every trigger on the table comes from one place post-rebuild.
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        $"""
                        DROP TRIGGER IF EXISTS vec_entries_au;
                        DROP TRIGGER IF EXISTS vec_entries_pending;
                        DROP TRIGGER IF EXISTS vec_entries_ad;

                        CREATE TRIGGER vec_entries_au AFTER UPDATE OF embed_state ON entries
                        WHEN NEW.embed_state = 'embedded' AND NEW.embedding IS NOT NULL
                        BEGIN
                            DELETE FROM vec_entries WHERE rowid = NEW.id;
                            INSERT INTO vec_entries(rowid, ctx, embedding) VALUES (NEW.id, {MemorySql.ContextKeyExpression("NEW.")}, NEW.embedding);
                        END;

                        CREATE TRIGGER vec_entries_pending AFTER UPDATE OF embed_state ON entries
                        WHEN NEW.embed_state = 'pending' AND OLD.embed_state = 'embedded'
                        BEGIN
                            DELETE FROM vec_entries WHERE rowid = OLD.id;
                        END;

                        CREATE TRIGGER vec_entries_ad AFTER DELETE ON entries BEGIN
                            DELETE FROM vec_entries WHERE rowid = OLD.id;
                        END;
                        """,
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            await connection.ExecuteAsync(
                    new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch
        {
            await connection.ExecuteAsync(
                    new CommandDefinition("ROLLBACK", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    ///     Ladder step 3 (issue #200): a bank-wide self-heal recompute of chunk_index/total_chunks,
    ///     reusing the exact SQL <see cref="AiRaccoon.Infrastructure.Sync.SyncService"/>'s merge uses
    ///     — no duplicate. Heals rows a pre-#200 binary wrote into a v2 bank without running the
    ///     write-path recompute (docs/plans/2026-08-08-search-knn-perf.md §3.3). One transaction,
    ///     rethrown on failure, matching <see cref="MigrateToV2Async"/>'s hard-step pattern.
    /// </summary>
    private static async Task MigrateToV3Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
                new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(MemorySql.RecomputeChunkColumnsBankWide, cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            await connection.ExecuteAsync(
                    new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch
        {
            await connection.ExecuteAsync(
                    new CommandDefinition("ROLLBACK", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Drops and recreates one vec0 table in the partitioned shape, repopulating it from <paramref name="sourceColumn"/>.</summary>
    private static async Task RebuildVecTableAsync(SqliteConnection connection, string table, int dimension,
        string sourceColumn, string wherePredicate, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
                new CommandDefinition($"DROP TABLE IF EXISTS {table}", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(
                    $"CREATE VIRTUAL TABLE {table} USING vec0(ctx TEXT partition key, embedding float[{dimension}] distance_metric=cosine)",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(
                    $"""
                    INSERT INTO {table}(rowid, ctx, embedding)
                    SELECT id, {MemorySql.ContextKeyExpression("")}, {sourceColumn} FROM entries WHERE {wherePredicate}
                    """,
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>Reads the embedding dimension a vec0 table was declared with, falling back to 384 when the table (or a recognizable declaration) is absent.</summary>
    private static async Task<int> ReadVecDimensionAsync(SqliteConnection connection, string table,
        CancellationToken cancellationToken)
    {
        var sql = await connection.ExecuteScalarAsync<string?>(
                new CommandDefinition(
                    "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @table",
                    new { table }, cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        if (sql is null)
        {
            return DefaultEmbeddingDimension;
        }

        var match = VecDimensionPattern.Match(sql);
        return match.Success
            ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : DefaultEmbeddingDimension;
    }
}
