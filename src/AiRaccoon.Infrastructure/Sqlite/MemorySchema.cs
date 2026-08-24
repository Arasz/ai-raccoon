using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Watch;
using Dapper;
using Microsoft.Data.Sqlite;

namespace AiRaccoon.Infrastructure.Sqlite;

/// <summary>
///     Single-file bank schema (see docs/work/archive/2026-08-03-native-memory-plan.md §2.2): entries, workspaces, settings, an FTS5
///     external-content index (value, source_file, section) and content + structure vec0
///     tables. Idempotent on every bank open; legacy banks migrate (see MigrateAsync).
/// </summary>
internal static class MemorySchema
{
    /// <summary>
    ///     Bucket uniqueness. Not part of <see cref="Ddl" />: on a legacy bank the duplicates
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

                                          CREATE UNIQUE INDEX IF NOT EXISTS uq_entries_workspace_bucket
                                              ON entries(path, hash, workspace_id)
                                              WHERE workspace_id IS NOT NULL;
                                          """;

    /// <summary>
    ///     The schema shape this build creates. Bumped by one per shipped schema change, with a
    ///     matching ladder step in <see cref="MigrateToV1Async" />/<see cref="MigrateToV2Async" />/
    ///     <see cref="MigrateToV3Async" />/<see cref="MigrateToV4Async" />/<see cref="MigrateToV5Async" />/<see cref="MigrateToV6Async" />/
    ///     <see cref="MigrateToV7Async" />/<see cref="MigrateToV8Async" />/
    ///     <see cref="MigrateToV9Async" />/
    ///     <see cref="MigrateToV10Async" /> (ADR-0011),
    ///     <see cref="MigrateToV11Async" />. Not every schema change needs a ladder
    ///     step: a trigger body replacement that is safely re-runnable on every open belongs in the
    ///     unconditional <see cref="Ddl" /> instead (ADR-0023 amendment) — the ladder is for changes
    ///     that need guarded, one-time work.
    /// </summary>
    internal const int CurrentVersion = 11;

    private const int DefaultEmbeddingDimension = 384;

    /// <summary>
    ///     The promotion_queue_entries_ad body (ADR-0023, amended by H4 and again by ADR-0046):
    ///     "still backed" means a row inside the project, which is <see cref="ProjectRows" /> — the
    ///     one definition ShareAsync resolves through.
    /// </summary>
    private static readonly string PromotionQueueTriggerDdl = $"""
                                                               CREATE TRIGGER IF NOT EXISTS promotion_queue_entries_ad AFTER DELETE ON entries BEGIN
                                                                   DELETE FROM promotion_queue
                                                                   WHERE project_id = OLD.project_id AND hash = OLD.hash
                                                                     AND NOT EXISTS (SELECT 1 FROM entries e
                                                                                     WHERE e.project_id = OLD.project_id AND e.hash = OLD.hash
                                                                                       AND {ProjectRows.Scope("e.")});
                                                               END;
                                                               """;

    // workspace_id carries its own FK to workspaces and a CHECK enforcing "workspace XOR
    // committed scope": an entry is either workspace-scratch or one of shared/project/custom, never both.
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
                                              chunk_index INTEGER NOT NULL DEFAULT -1,
                                              total_chunks INTEGER NOT NULL DEFAULT 0,
                                              source_id INTEGER NULL,
                                              FOREIGN KEY (workspace_id) REFERENCES workspaces(id) ON DELETE RESTRICT,
                                              CHECK ((workspace_id IS NULL AND scope IN ('shared','project','custom')) OR (workspace_id IS NOT NULL AND scope IS NULL))
                                          );

                                          CREATE TABLE IF NOT EXISTS settings (
                                              key TEXT PRIMARY KEY,
                                              value TEXT NOT NULL
                                          );

                                          -- Maintenance job ledger (ADR-0070). The clock lives in the BANK, not in the
                                          -- process: the previous per-process timer was seeded on first run, so a bank
                                          -- only ever opened by short-lived processes never vacuumed at all. A job with
                                          -- no interval runs once ever, which is what `last_run_at` existing records.
                                          CREATE TABLE IF NOT EXISTS maintenance_jobs (
                                              name        TEXT PRIMARY KEY,
                                              last_run_at INTEGER NOT NULL,
                                              run_count   INTEGER NOT NULL DEFAULT 0
                                          );

                                          CREATE VIRTUAL TABLE IF NOT EXISTS entries_fts USING fts5(
                                              value,
                                              source_file,
                                              section,
                                              content='entries',
                                              content_rowid='id'
                                          );

                                          -- vec0 stays empty until the embed pipeline fills it; the embedder owns the embedding
                                          -- dimension if the model is not all-MiniLM (384). `ctx` is a vec0 metadata column,
                                          -- filterable but not a partition key (ADR-0068, ladder step v9); cosine is declared
                                          -- explicitly so a bare MATCH cannot fall back to L2.
                                          CREATE VIRTUAL TABLE IF NOT EXISTS vec_entries USING vec0(ctx TEXT, embedding float[384] distance_metric=cosine);

                                          -- Structure modality: heading-path vectors, rowid = entry id. Written by the
                                          -- embed transition (EntryEmbedder, ADR-0004); the triggers below keep it consistent.
                                          CREATE VIRTUAL TABLE IF NOT EXISTS vec_structure USING vec0(ctx TEXT, embedding float[384] distance_metric=cosine);

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

                                          -- Clear arm for vec_structure: merge-reindex invalidates a row by setting
                                          -- embed_state back to 'pending' (see SyncService's reindex UPDATE, which also
                                          -- nulls structure_embedding/heading_path); without this the vec_structure row
                                          -- survives a deliberately invalidated content vector. Mirrors vec_entries_pending.
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

                                          -- vec0 has no triggers: embedding rows follow embed_state. Marking embedded
                                          -- upserts the vec row (delete-then-insert, so a re-embed replaces rather than
                                          -- duplicates); marking pending or deleting the entry removes it.
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

                                          -- promotion_queue_entries_ad (ADR-0023) is *not* declared here. Its guard
                                          -- needed a body replacement (H4, see EnsurePromotionQueueTriggerScopeGuardAsync
                                          -- below), and CREATE TRIGGER IF NOT EXISTS can only ever create — replacing a
                                          -- body needs a DROP first, which is a real schema write and must not run on
                                          -- every open the way every other statement here (all IF NOT EXISTS, all
                                          -- no-ops once the object exists) safely can.

                                          CREATE TABLE IF NOT EXISTS sync_meta (
                                              key TEXT PRIMARY KEY,
                                              value TEXT NOT NULL
                                          );

                                          -- noise_clusters/vec_noise (ADR-0039's centroid-clustering store) are
                                          -- removed from the fresh-bank DDL (this task, amending ADR-0039): dead by
                                          -- evidence — leader-follower produced 1,583 clusters from 1,940 items (93%
                                          -- singletons) on real traffic, and noise silhouette (0.047-0.142) is no
                                          -- better than signal (0.045-0.075). MigrateToV6Async is left unchanged as a
                                          -- historical no-op ladder step, so a legacy bank stamped &lt;v6 still gets
                                          -- these tables (inert) on upgrade; only a fresh bank now skips them.
                                          --
                                          -- noise_entries (ADR-0029) is restored: the write-time reject log is the
                                          -- noise-learning training-data source (ADR-0039) — shadow mode plus this
                                          -- table is how a deployment produces the labelled set the research named as
                                          -- missing. expires_at is computed at insert time from the
                                          -- noise.retention-days.global setting (default 14, ADR-0029).
                                          CREATE TABLE IF NOT EXISTS noise_entries (
                                              id                 INTEGER PRIMARY KEY,
                                              request_content    TEXT NOT NULL,
                                              project_id         TEXT NOT NULL,
                                              source_file        TEXT NULL,
                                              detected_by_policy TEXT NOT NULL,
                                              expires_at         INTEGER NOT NULL,
                                              created_at         INTEGER NOT NULL
                                          );

                                          CREATE INDEX IF NOT EXISTS idx_noise_entries_expires_at ON noise_entries(expires_at);

                                          CREATE TABLE IF NOT EXISTS sync_tombstones (
                                              project_id TEXT NOT NULL,
                                              hash TEXT NOT NULL,
                                              scope TEXT NOT NULL,
                                              deleted_at INTEGER NOT NULL,
                                              PRIMARY KEY (project_id, hash, scope)
                                          );

                                          -- File-watcher feature: persisted watch registrations and per-path
                                          -- fingerprints (hash-skip). Normalized paths; runtime state is not persisted.
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

                                          -- WP12 Fix A: the chunker runs outside the write lock, so two replaces
                                          -- racing the same stale-to-new transition (SqliteMemoryStore.ReplaceCoreAsync)
                                          -- need a claim to still chunk exactly once — see MemorySql.TryClaimWatchDigest.
                                          -- A crashed claim holder never deletes its row, so claimed_at (not presence
                                          -- alone) lets the next digest reclaim it once it is stale.
                                          CREATE TABLE IF NOT EXISTS watch_digest_claims (
                                              project_id  TEXT NOT NULL,
                                              path        TEXT NOT NULL,
                                              claimed_at  INTEGER NOT NULL,
                                              PRIMARY KEY (project_id, path)
                                          );

                                          -- Propose tier: candidates waiting for promotion review, kept separate from
                                          -- entries — queue rows are never searchable and never counted by memory_stats.
                                          -- Capacity/eviction lives on this table only (the shared tier stays curated and sweep-exempt).
                                          CREATE TABLE IF NOT EXISTS promotion_queue (
                                              id          INTEGER PRIMARY KEY,
                                              project_id  TEXT NOT NULL,
                                              hash        TEXT NOT NULL,
                                              path        TEXT NULL,
                                              value       TEXT NOT NULL,
                                              source_file TEXT NULL,
                                              score       REAL NOT NULL,
                                              reasons     TEXT NOT NULL DEFAULT '[]',
                                              scorer_version INTEGER NOT NULL DEFAULT 0,
                                              claimed_at  INTEGER NULL,
                                              created_at  INTEGER NOT NULL,
                                              updated_at  INTEGER NOT NULL,
                                              UNIQUE (project_id, hash)
                                          );

                                          -- Agent rejections (memory_promotion_discard): a permanent per-project
                                          -- "no" for a content identity — propose never re-queues it (docs/adr/0026).
                                          CREATE TABLE IF NOT EXISTS promotion_discards (
                                              project_id   TEXT NOT NULL,
                                              hash         TEXT NOT NULL,
                                              discarded_at INTEGER NOT NULL,
                                              PRIMARY KEY (project_id, hash)
                                          );

                                          CREATE INDEX IF NOT EXISTS idx_entries_scope_project ON entries(scope, project_id);
                                          CREATE INDEX IF NOT EXISTS idx_entries_hash ON entries(hash);
                                          CREATE INDEX IF NOT EXISTS idx_entries_workspace ON entries(workspace_id);
                                          CREATE INDEX IF NOT EXISTS idx_entries_embed_state ON entries(embed_state, project_id);
                                          CREATE INDEX IF NOT EXISTS idx_watches_project ON watches(project_id);
                                          CREATE INDEX IF NOT EXISTS idx_promotion_queue_project ON promotion_queue(project_id);
                                          CREATE INDEX IF NOT EXISTS idx_promotion_queue_score ON promotion_queue(score);

                                          CREATE TABLE IF NOT EXISTS memory_source (
                                              id            INTEGER PRIMARY KEY,
                                              source_type   TEXT NOT NULL CHECK(source_type IN ('file','transcript','manual')),
                                              source_locator TEXT NOT NULL,
                                              section       TEXT NULL,
                                              heading_path  TEXT NULL
                                          );

                                          CREATE UNIQUE INDEX IF NOT EXISTS uq_memory_source
                                              ON memory_source(source_type, source_locator, COALESCE(section, ''));

                                          -- Search-quality metric: tracks every memory_search call with correlation-id,
                                          -- follow-through (did the agent use the result?), and usefulness grade.
                                          -- See docs/plans/2026-08-11-search-quality-metric-plan.md.
                                          CREATE TABLE IF NOT EXISTS search_quality (
                                              id                INTEGER PRIMARY KEY,
                                              correlation_id    TEXT NOT NULL UNIQUE,
                                              query             TEXT NOT NULL,
                                              scope             TEXT,
                                              project_id        TEXT,
                                              session_id        TEXT,
                                              result_count      INTEGER,
                                              top_source_files  TEXT,       -- JSON array of SourceFile paths
                                              follow_through_count INTEGER  DEFAULT 0,
                                              follow_through_files TEXT,    -- JSON array of files read after search
                                              usefulness_grade  INTEGER     CHECK(usefulness_grade BETWEEN 1 AND 5),
                                              grade_note        TEXT,
                                              created_at        INTEGER NOT NULL
                                          );

                                          CREATE INDEX IF NOT EXISTS idx_sq_project_time ON search_quality(project_id, created_at);

                                          -- Universal self-instrumentation row (docs/plans/2026-08-15-performance-metrics-implementation.md,
                                          -- WP0). project_id/query_hash/correlation_id are real, indexed columns rather than
                                          -- entries inside tags: project_id is the performance report's primary filter, and the
                                          -- other two join back to search_quality. Every other deferred dimension stores as a
                                          -- row under a new `name`, not a new column. In Ddl (not the `if (fresh)` branch) so
                                          -- it and both indexes reach legacy banks on the next open, not only fresh ones —
                                          -- idx_entries_source_id already had to be written twice for missing exactly this.
                                          CREATE TABLE IF NOT EXISTS metrics (
                                              id             INTEGER PRIMARY KEY,
                                              name           TEXT NOT NULL,
                                              kind           TEXT NOT NULL,
                                              value          REAL NOT NULL,
                                              unit           TEXT NOT NULL,
                                              project_id     TEXT NULL,
                                              query_hash     TEXT NULL,
                                              correlation_id TEXT NULL,
                                              tags           TEXT NULL,
                                              recorded_at    INTEGER NOT NULL
                                          );

                                          CREATE INDEX IF NOT EXISTS idx_metrics_name_time    ON metrics(name, recorded_at);
                                          CREATE INDEX IF NOT EXISTS idx_metrics_project_time ON metrics(project_id, recorded_at);

                                          -- The retention reaper (MetricsRetentionJob) deletes on recorded_at alone; neither
                                          -- index above leads with it, so every reap was a full-table SCAN (review-fixes
                                          -- finding 4, confirmed with EXPLAIN QUERY PLAN). In the unconditional Ddl block,
                                          -- not the `if (fresh)` branch, for the same reason as the two indexes above.
                                          CREATE INDEX IF NOT EXISTS idx_metrics_recorded_at ON metrics(recorded_at);

                                          -- Project registry (ADR-0089 decision 5): which project ids exist, as a fact of
                                          -- record rather than derived from entries rows. name is optional and never an
                                          -- identifier — not unique, never accepted where an id is expected. In the
                                          -- unconditional Ddl block, not the `if (fresh)` branch, same reason as metrics
                                          -- above: no CurrentVersion bump, reaches a legacy bank via the digest-rerun path.
                                          CREATE TABLE IF NOT EXISTS projects (
                                              id         TEXT PRIMARY KEY,
                                              name       TEXT,
                                              created_at INTEGER NOT NULL
                                          );

                                          -- Model-migration outbox (ADR-0076): a single-row lock+ledger for an in-flight
                                          -- embedding-engine change. `model embedding set` writes the new engine settings and this
                                          -- row in ONE transaction — the outbox pattern, so no crash can produce the
                                          -- settings change without the record of the re-embed it owes, or vice versa.
                                          -- finished_at is null for exactly as long as a re-embed is owed; a separate
                                          -- relay (ModelMigrationJob) drains it. lease_* let a running relay resist a
                                          -- second one claiming the same row, with stale-lease reclamation mirroring
                                          -- watches.scan_owner (ADR-0037).
                                          CREATE TABLE IF NOT EXISTS model_migration (
                                              id               INTEGER PRIMARY KEY CHECK (id = 1),
                                              provider         TEXT NOT NULL,
                                              model            TEXT NULL,
                                              base_url         TEXT NULL,
                                              engine           TEXT NOT NULL,
                                              started_at       INTEGER NOT NULL,
                                              finished_at      INTEGER NULL,
                                              lease_owner      TEXT NULL,
                                              lease_expires_at INTEGER NULL
                                          );

                                          -- Repair outbox (ADR-0075 amendment): `repair <verb> --apply` commits a request
                                          -- row through the server instead of writing the bank itself. Keyed by kind
                                          -- (RepairKinds), not a single id=1 row like model_migration, since chunk-index
                                          -- and reingest repairs are independent and either can have its own open request.
                                          -- finished_at is null for exactly as long as the corresponding maintenance job
                                          -- (ReingestRepairJob/ChunkIndexRepairJob) owes the apply.
                                          CREATE TABLE IF NOT EXISTS repair_requests (
                                              kind         TEXT PRIMARY KEY,
                                              requested_at INTEGER NOT NULL,
                                              finished_at  INTEGER NULL
                                          );

                                          -- Promotion-queue-prune outbox (ADR-0075 amendment): `extract prune --apply`
                                          -- commits a request row through the server instead of deleting the orphaned
                                          -- rows itself. A single id=1 row, like model_migration — there is only ever
                                          -- one prune operation, not independent kinds the way repair has. finished_at
                                          -- is null for exactly as long as PromotionQueuePruneJob owes the apply.
                                          CREATE TABLE IF NOT EXISTS promotion_queue_prune_requests (
                                              id           INTEGER PRIMARY KEY CHECK (id = 1),
                                              requested_at INTEGER NOT NULL,
                                              finished_at  INTEGER NULL
                                          );

                                          -- Code corpus (docs/work/2026-08-21-code-search-implementation-plan.md §3.1): a second,
                                          -- code-only corpus alongside entries — additive, digest-gated, no ladder step, no
                                          -- CurrentVersion bump (metrics-table precedent above). Project-scoped only: no
                                          -- scope/workspace_id/agent_id, no rating/ttl_days/access_count (no degradation — code is
                                          -- a re-derivable cache, §3.2), no heading_path/section/source_id (no structure modality
                                          -- in v1).
                                          CREATE TABLE IF NOT EXISTS code_entries (
                                              id           INTEGER PRIMARY KEY,
                                              hash         TEXT NOT NULL,
                                              path         TEXT NOT NULL,
                                              value        TEXT NOT NULL,
                                              source_file  TEXT NOT NULL,
                                              line_start   INTEGER NOT NULL,
                                              line_end     INTEGER NOT NULL,
                                              project_id   TEXT NOT NULL,
                                              created_at   INTEGER NOT NULL,
                                              updated_at   INTEGER NOT NULL,
                                              embed_state  TEXT NOT NULL DEFAULT 'pending' CHECK(embed_state IN ('pending','embedded')),
                                              embedding    BLOB NULL,
                                              chunk_index  INTEGER NOT NULL DEFAULT -1,
                                              total_chunks INTEGER NOT NULL DEFAULT 0
                                          );

                                          -- embed_attempts (S2) is NOT declared here: ALTER TABLE ADD COLUMN isn't
                                          -- idempotent under a race (two connections opening the bank at once, e.g.
                                          -- the maintenance hosted service and the first real request, can both see
                                          -- it missing before either commits), unlike every CREATE/DROP ... IF
                                          -- [NOT] EXISTS statement in this block — see EnsureCodeEmbedAttemptsColumnAsync,
                                          -- called right after this block runs (same digest-mismatch-only cadence,
                                          -- never on a steady-state open) and tolerant of losing that race.

                                          CREATE UNIQUE INDEX IF NOT EXISTS uq_code_chunk ON code_entries(project_id, path, hash);
                                          CREATE INDEX IF NOT EXISTS idx_code_entries_project ON code_entries(project_id);
                                          CREATE INDEX IF NOT EXISTS idx_code_entries_hash ON code_entries(hash);
                                          CREATE INDEX IF NOT EXISTS idx_code_entries_embed_state ON code_entries(embed_state, project_id);
                                          -- idx_code_entries_path (project_id, path) was a redundant left-prefix of
                                          -- uq_code_chunk (project_id, path, hash): the planner already uses uq_code_chunk
                                          -- for the digest-event delete legs (proven: dropping this index left the plan
                                          -- unchanged), so the index cost a B-tree write per insert for no query it alone
                                          -- served. Self-cleans dev banks that already created it — the digest change
                                          -- re-runs this block (integration review, code-search-implementation-plan §3.1).
                                          DROP INDEX IF EXISTS idx_code_entries_path;

                                          CREATE VIRTUAL TABLE IF NOT EXISTS code_fts USING fts5(
                                              value,
                                              source_file,
                                              content='code_entries',
                                              content_rowid='id'
                                          );

                                          CREATE TRIGGER IF NOT EXISTS code_fts_ai AFTER INSERT ON code_entries BEGIN
                                              INSERT INTO code_fts(rowid, value, source_file)
                                              VALUES (new.id, new.value, new.source_file);
                                          END;

                                          CREATE TRIGGER IF NOT EXISTS code_fts_ad AFTER DELETE ON code_entries BEGIN
                                              INSERT INTO code_fts(code_fts, rowid, value, source_file)
                                              VALUES ('delete', old.id, old.value, old.source_file);
                                          END;

                                          CREATE TRIGGER IF NOT EXISTS code_fts_au AFTER UPDATE OF value, source_file ON code_entries BEGIN
                                              INSERT INTO code_fts(code_fts, rowid, value, source_file)
                                              VALUES ('delete', old.id, old.value, old.source_file);
                                              INSERT INTO code_fts(rowid, value, source_file)
                                              VALUES (new.id, new.value, new.source_file);
                                          END;

                                          -- vec0 for the code corpus: `ctx` is the project id directly — code is project-scoped
                                          -- only, so it never needs ContextKeyExpression's shared/workspace/custom branching.
                                          CREATE VIRTUAL TABLE IF NOT EXISTS vec_code USING vec0(ctx TEXT, embedding float[768] distance_metric=cosine);

                                          CREATE TRIGGER IF NOT EXISTS vec_code_au AFTER UPDATE OF embed_state ON code_entries
                                          WHEN NEW.embed_state = 'embedded' AND NEW.embedding IS NOT NULL
                                          BEGIN
                                              DELETE FROM vec_code WHERE rowid = NEW.id;
                                              INSERT INTO vec_code(rowid, ctx, embedding) VALUES (NEW.id, NEW.project_id, NEW.embedding);
                                          END;

                                          CREATE TRIGGER IF NOT EXISTS vec_code_pending AFTER UPDATE OF embed_state ON code_entries
                                          WHEN NEW.embed_state = 'pending' AND OLD.embed_state = 'embedded'
                                          BEGIN
                                              DELETE FROM vec_code WHERE rowid = OLD.id;
                                          END;

                                          CREATE TRIGGER IF NOT EXISTS vec_code_ad AFTER DELETE ON code_entries BEGIN
                                              DELETE FROM vec_code WHERE rowid = OLD.id;
                                          END;

                                          """;

    /// <summary>
    ///     First 32 bits of SHA-256(<see cref="Ddl" />), stored in <c>PRAGMA application_id</c>
    ///     (ADR-0075) to gate the <see cref="Ddl" /> block without a <see cref="CurrentVersion" />
    ///     bump. Derived from the same string the block executes, so the two cannot drift.
    ///     Content addressing, not cryptography — truncated to 32 bits to fit the pragma; a
    ///     collision costs a skipped rerun, not a security failure (accepted trade,
    ///     docs/plans/2026-08-16-bank-open-cost-implementation.md §4.2).
    /// </summary>
    internal static readonly int SchemaDigest = ComputeSchemaDigest();

    /// <summary>
    ///     Test seam only: invoked once, immediately after the Ddl block (and S2's column ensure)
    ///     durably completes, before anything else in this open proceeds — lets a test simulate a
    ///     process crash in that exact window. <see cref="AsyncLocal{T}" />, not a plain static
    ///     field: xUnit runs test classes in parallel, and a plain static would leak a thrown hook
    ///     into every other concurrently-running test that also calls <see cref="EnsureAsync" />.
    ///     Null (no-op) in production.
    /// </summary>
    internal static readonly AsyncLocal<Func<CancellationToken, Task>?> TestOnlyAfterDdlHookAsync = new();

    /// <summary>
    ///     Test seam only: when true for the calling async flow, forces this open's ladder to report
    ///     unhealthy without engineering a genuine dedupe failure — lets a test pin "the digest and
    ///     version stamps only land when the ladder actually finished". Same <see cref="AsyncLocal{T}" />
    ///     rationale as <see cref="TestOnlyAfterDdlHookAsync" />. False (no-op) in production.
    /// </summary>
    internal static readonly AsyncLocal<bool> TestOnlyForceUnhealthyLadder = new();

    private static readonly Regex VecDimensionPattern = new(@"float\[(\d+)\]", RegexOptions.Compiled);

    private static int ComputeSchemaDigest()
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(Ddl), hash);
        return BinaryPrimitives.ReadInt32BigEndian(hash);
    }

    /// <summary>
    ///     Returns the unconditional no-overlapping-watches prune step's outcome for THIS call
    ///     (pruned list empty on a fresh bank or when nothing overlapped; warnings non-empty only
    ///     when a project's rows could not be resolved) — <see cref="SqliteConnectionFactory.InitializeAsync" />
    ///     is the one caller that owns a logger and reports both
    ///     (docs/work/2026-08-21-code-search-implementation-plan.md §4). MemorySchema itself stays
    ///     silent and only returns data.
    /// </summary>
    public static async Task<WatchOverlapPruneResult> EnsureAsync(SqliteConnection connection, CancellationToken cancellationToken)
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

        // ADR-0075: Ddl is a no-op read once every object exists (all CREATE ... IF NOT EXISTS),
        // but it is still ~30 statements to reach that no-op, so it is gated on a digest of
        // itself rather than run unconditionally on every open.
        var storedDigest = await ReadApplicationIdAsync(connection, cancellationToken).ConfigureAwait(false);
        // Data F1 / D5: the digest is stamped only once this whole open's schema work is durable
        // (every return point below), never here — a crash between the Ddl block and the version
        // ladder's own stamp must leave the digest stale, or EnsureCheapAsync's digest-only check
        // (the per-tool-call hot path) would trust a half-migrated bank forever.
        var needsDigestStamp = storedDigest != SchemaDigest;
        if (needsDigestStamp)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = Ddl;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            // Not folded into the Ddl string: unlike CREATE/DROP ... IF [NOT] EXISTS there, a
            // bare ALTER TABLE ADD COLUMN is not idempotent under two connections racing the same
            // digest mismatch — its own step, with its own column-existence check (see
            // EnsureCodeEmbedAttemptsColumnAsync).
            await EnsureCodeEmbedAttemptsColumnAsync(connection, cancellationToken).ConfigureAwait(false);

            var testHook = TestOnlyAfterDdlHookAsync.Value;
            if (testHook is not null)
            {
                await testHook(cancellationToken).ConfigureAwait(false);
            }
        }

        // Runs on every open, version or not: it is a data move guarding a deny-by-default gate,
        // it costs one indexed count, and a bank that was stamped by a newer build and then
        // written by an older one would otherwise keep an unreachable scope forever.
        await MigrateIngestScopeKeysAsync(connection, cancellationToken).ConfigureAwait(false);

        // Runs on every open, version or not, same shape as above: one indexed sqlite_master read,
        // write only when the stored trigger body still needs the H4 scope guard.
        await EnsurePromotionQueueTriggerScopeGuardAsync(connection, cancellationToken).ConfigureAwait(false);

        // Runs on every open, version or not (orchestrator ruling, S7): no-overlapping-watches was
        // originally a one-time v11 ladder step; demoted to an unconditional, ungated step in the
        // same "runs regardless" family as the two calls above — it must reach a fresh bank too
        // (harmlessly no-ops: no watches rows yet) and a bank already at CurrentVersion.
        var overlapResult = await PruneOverlappingWatchesAsync(connection, cancellationToken).ConfigureAwait(false);

        if (fresh)
        {
            await connection.ExecuteAsync(
                new CommandDefinition(BucketIndexDdl, cancellationToken: cancellationToken)).ConfigureAwait(false);
            // source_id index: created here for fresh banks (DDL already has the column),
            // and in MigrateToV5Async for v4→v5 migration banks.
            await connection.ExecuteAsync(
                new CommandDefinition(
                    "CREATE INDEX IF NOT EXISTS idx_entries_source_id ON entries(source_id)",
                    cancellationToken: cancellationToken)).ConfigureAwait(false);
            await StampAsync(connection, CurrentVersion, cancellationToken).ConfigureAwait(false);
            if (needsDigestStamp)
            {
                await StampSchemaDigestAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            return overlapResult;
        }

        if (storedVersion >= CurrentVersion)
        {
            // No ladder step is needed, but the Ddl block above may just have run (a digest-gated,
            // no-version-bump addition, e.g. the metrics/code-corpus tables) — this is the only
            // remaining exit for that case, so the digest must be stamped here too.
            if (needsDigestStamp)
            {
                await StampSchemaDigestAsync(connection, cancellationToken).ConfigureAwait(false);
            }

            return overlapResult;
        }

        // Stamped only when the ladder completed: the v1 step's bucket-index dedupe is
        // deliberately soft (a bank must never fail to open), and a bank stamped past a step
        // that silently failed would never retry it. The v1 step must run — and its dedupe
        // complete — before the v2 step, or chunk-column numbering bakes in rows the dedupe
        // would have deleted (docs/plans/2026-08-08-search-knn-perf.md).
        var healthy = !TestOnlyForceUnhealthyLadder.Value;
        if (healthy && storedVersion < 1)
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
            // Hard step: heals rows a pre-guard binary wrote into a v2 bank without running the
            // write-path chunk recompute.
            await MigrateToV3Async(connection, cancellationToken).ConfigureAwait(false);
        }

        if (healthy && storedVersion < 4)
        {
            // Hard step: ALTER TABLE ADD COLUMN is not idempotent, so it needs the version gate
            // (unlike promotion_queue_entries_ad above, which reaches every bank on every open —
            // no version gate at all — because EnsurePromotionQueueTriggerScopeGuardAsync runs
            // unconditionally, outside both the version ladder and the Ddl digest gate).
            await MigrateToV4Async(connection, cancellationToken).ConfigureAwait(false);
        }

        if (healthy && storedVersion < 5)
        {
            await MigrateToV5Async(connection, cancellationToken).ConfigureAwait(false);
        }

        if (healthy && storedVersion < 6)
        {
            await MigrateToV6Async(connection, cancellationToken).ConfigureAwait(false);
        }

        if (healthy && storedVersion < 7)
        {
            // Soft, like the v1 step: a dedupe failure leaves the bank open, degraded, retried
            // on the next open, rather than refusing to open at all.
            healthy = await MigrateToV7Async(connection, cancellationToken).ConfigureAwait(false);
        }

        if (healthy && storedVersion < 8)
        {
            await MigrateToV8Async(connection, cancellationToken).ConfigureAwait(false);
        }

        if (healthy && storedVersion < 9)
        {
            await MigrateToV9Async(connection, cancellationToken).ConfigureAwait(false);
        }

        if (healthy && storedVersion < 10)
        {
            await MigrateToV10Async(connection, cancellationToken).ConfigureAwait(false);
        }

        if (healthy && storedVersion < 11)
        {
            await MigrateToV11Async(connection, cancellationToken).ConfigureAwait(false);
        }

        // Both stamps land together, only once the ladder actually finished (healthy): stamping the
        // digest on a degraded (unhealthy) run would let EnsureCheapAsync trust a bank whose ladder
        // never completed, the same bug this reordering exists to close (Data F1 / D5).
        if (healthy)
        {
            await StampAsync(connection, CurrentVersion, cancellationToken).ConfigureAwait(false);
            if (needsDigestStamp)
            {
                await StampSchemaDigestAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }

        return overlapResult;
    }

    private static async Task MigrateToV6Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string sql = """
                           CREATE TABLE IF NOT EXISTS noise_clusters (
                               id                 INTEGER PRIMARY KEY,
                               project_id         TEXT NOT NULL,
                               user_id            TEXT NULL,
                               cluster_label      TEXT NOT NULL,
                               sample_content     TEXT NOT NULL,
                               frequency          INTEGER NOT NULL DEFAULT 1,
                               status             TEXT NOT NULL CHECK(status IN ('candidate','active','suppressed')),
                               centroid_embedding BLOB NOT NULL,
                               created_at         INTEGER NOT NULL,
                               last_seen_at       INTEGER NOT NULL,
                               UNIQUE(project_id, cluster_label)
                           );

                           CREATE VIRTUAL TABLE IF NOT EXISTS vec_noise USING vec0(
                               ctx TEXT partition key,
                               embedding float[384] distance_metric=cosine
                           );
                           """;
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    ///     Ladder step 7 (WP5b/DA-F1): <see cref="BucketIndexDdl" />'s two partial indexes are
    ///     declared <c>WHERE scope = 'shared'</c> / <c>WHERE scope IN ('project','custom')</c>, but
    ///     a workspace row's CHECK constraint forces <c>scope IS NULL</c> — neither index can ever
    ///     match a workspace row, so a retried workspace write silently duplicated. Dedupes existing
    ///     workspace-scope duplicates first (survivor = earliest row; same shape as
    ///     <see cref="MigrateToV1Async" />'s bucket dedupe, GROUP BY mirroring the index expression
    ///     exactly) so index creation can never fail on a real bank, then creates the index. Soft
    ///     like the v1 step: a dedupe failure leaves the bank open, degraded, retried next open.
    /// </summary>
    private static async Task<bool> MigrateToV7Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var hasWorkspaceIndex = await connection.ExecuteScalarAsync<long?>(
                new CommandDefinition(
                    "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = 'uq_entries_workspace_bucket'",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false) is not null;
        if (hasWorkspaceIndex)
        {
            return true;
        }

        try
        {
            await connection.ExecuteAsync(
                    new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            try
            {
                await connection.ExecuteAsync(
                        new CommandDefinition(
                            """
                            DELETE FROM entries
                            WHERE workspace_id IS NOT NULL
                              AND path IS NOT NULL AND hash IS NOT NULL
                              AND id NOT IN (SELECT MIN(id) FROM entries
                                             WHERE workspace_id IS NOT NULL
                                             GROUP BY path, hash, workspace_id);

                            CREATE UNIQUE INDEX IF NOT EXISTS uq_entries_workspace_bucket
                                ON entries(path, hash, workspace_id)
                                WHERE workspace_id IS NOT NULL;
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

        return true;
    }

    /// <summary>
    ///     Ladder step 8 (WP5b/A-F11): promotion_queue.claimed_at backs the claim-by-update pattern
    ///     PromotionQueueService.PromoteAsync uses instead of claim-by-delete, so a ShareAsync
    ///     failure mid-promotion leaves the candidate reclaimable instead of destroying it. DEFAULT
    ///     covers a fresh bank; existing rows backfill to NULL (unclaimed) by SQLite's own ADD
    ///     COLUMN default — none of them were ever claimed by a versioned claim mechanism.
    /// </summary>
    private static async Task MigrateToV8Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = (await connection.QueryAsync<string>(
                new CommandDefinition(
                    "SELECT name FROM pragma_table_info('promotion_queue')",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        if (!columns.Contains("claimed_at"))
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        "ALTER TABLE promotion_queue ADD COLUMN claimed_at INTEGER NULL",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Ladder step 9 (ADR-0068): rebuilds vec_entries/vec_structure with `ctx` demoted from
    ///     partition key to an ordinary vec0 metadata column. Rethrows for the same reason the v2
    ///     step does — an empty-shell vec_entries answers every query with silence.
    /// </summary>
    private static async Task MigrateToV9Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var entriesDimension = await ReadVecDimensionAsync(connection, "vec_entries", cancellationToken)
            .ConfigureAwait(false);
        var structureDimension = await ReadVecDimensionAsync(connection, "vec_structure", cancellationToken)
            .ConfigureAwait(false);

        await connection.ExecuteAsync(
                new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
        {
            await RebuildVecTableAsync(connection, "vec_entries", entriesDimension, "embedding",
                    "embed_state = 'embedded' AND embedding IS NOT NULL", cancellationToken)
                .ConfigureAwait(false);
            await RebuildVecTableAsync(connection, "vec_structure", structureDimension, "structure_embedding",
                    "structure_embedding IS NOT NULL", cancellationToken)
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

        // The rebuild frees pages into the free list; the FILE does not shrink until VACUUM, and the
        // maintenance service's vacuum clock is per-process and seeded on first run, so a bank only
        // ever opened by short-lived processes never reaches it. Measured on a 183 MB bank: the
        // migration freed 42.0 MB and the file did not move; VACUUM returned 53.2 MB in 0.6 s.
        // Outside the transaction because VACUUM cannot run inside one, and best-effort because a
        // deferred reclaim is not a reason to fail a migration that already succeeded.
        try
        {
            await connection.ExecuteAsync(new CommandDefinition("VACUUM;", cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) // SQLITE_BUSY / SQLITE_LOCKED
        {
            // Another connection holds the bank. The pages stay on the free list and the next
            // maintenance vacuum collects them; nothing is lost but the disk saving is deferred.
        }
    }

    /// <summary>Ladder step 10 (ADR-0070): the maintenance job ledger, so the cadence clock lives in the bank.</summary>
    private static async Task MigrateToV10Async(SqliteConnection connection, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(new CommandDefinition(
                """
                CREATE TABLE IF NOT EXISTS maintenance_jobs (
                    name        TEXT PRIMARY KEY,
                    last_run_at INTEGER NOT NULL,
                    run_count   INTEGER NOT NULL DEFAULT 0
                )
                """, cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    /// <summary>
    ///     Ladder step 11: the <c>sync_tombstones</c> table must carry the Ddl's exact composite
    ///     shape — <c>project_id TEXT NOT NULL PRIMARY KEY</c>, and <c>hash</c>/<c>scope</c> also
    ///     members of the composite <c>PRIMARY KEY (project_id, hash, scope)</c>. A legacy bank that
    ///     had <c>project_id</c> added via ALTER TABLE carries it as plain <c>TEXT</c> — the DDL's
    ///     <c>CREATE TABLE IF NOT EXISTS</c> silently no-ops against that shape. The whole repair —
    ///     inspect, copy, drop, create, reinsert — runs inside one <see cref="MigrateToV9Async" />-style
    ///     <c>BEGIN IMMEDIATE</c> transaction: a concurrent opener cannot read the legacy rows and
    ///     then clobber the repaired table with them, and a crash mid-rebuild rolls back to the
    ///     intact legacy table. Idempotent: a table already at the correct shape is skipped, and
    ///     the shape is re-checked under the write lock in case another opener migrated first.
    /// </summary>
    private static async Task MigrateToV11Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var hasTable = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'sync_tombstones'",
                cancellationToken: cancellationToken))
            .ConfigureAwait(false) > 0;
        if (!hasTable)
        {
            return;
        }

        await connection.ExecuteAsync(
                new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
        {
            var columnRows = (await connection.QueryAsync<PragmaColumnRow>(
                    new CommandDefinition(
                        "SELECT name, type, \"notnull\", pk FROM pragma_table_info('sync_tombstones')",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false)).ToList();

            // Full-shape check, not just project_id: every column's declared type must match the
            // Ddl and all three key members must be part of the composite PRIMARY KEY (pk 1..3).
            // Re-read under the lock — another opener may have migrated between our cheap probe
            // above and this BEGIN.
            if (ShapeMatchesDdl(columnRows))
            {
                await connection.ExecuteAsync(
                        new CommandDefinition("COMMIT", cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                return;
            }

            // Copy policy: tombstone identity is (project_id, hash, scope) text + an integer
            // timestamp; SQLite's TEXT affinity has already coerced any pre-affinity values to
            // text on disk, so reading every column through its declared type via CAST keeps the
            // copy total — no dynamic cast can throw and strand a bank that cannot reach v11.
            // A pre-ALTER legacy table has NO project_id column at all: there is nothing
            // meaningful to keep (every row would be unscoped), so skip the copy entirely.
            // Rows are read as untyped dynamics and converted in C#: sqlite3mc's reader reports
            // field types that break Dapper's typed materializer on empty/edge result shapes.
            List<TombstoneRow> existingRows;
            if (columnRows.Any(c => c.Name == "project_id"))
            {
                var raw = (await connection.QueryAsync(
                        new CommandDefinition(
                            """
                            SELECT CAST(project_id AS TEXT) AS ProjectId,
                                   CAST(hash AS TEXT) AS Hash,
                                   CAST(scope AS TEXT) AS Scope,
                                   CAST(deleted_at AS INTEGER) AS DeletedAt
                            FROM sync_tombstones
                            WHERE project_id IS NOT NULL AND project_id != ''
                            """,
                            cancellationToken: cancellationToken))
                    .ConfigureAwait(false)).ToList();
                existingRows = raw.Select(r => new TombstoneRow(
                    (string)r.ProjectId, (string)r.Hash, (string)r.Scope, (long)r.DeletedAt)).ToList();
            }
            else
            {
                existingRows = [];
            }

            await connection.ExecuteAsync(
                    new CommandDefinition("DROP TABLE sync_tombstones", cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        CREATE TABLE sync_tombstones (
                            project_id TEXT NOT NULL,
                            hash TEXT NOT NULL,
                            scope TEXT NOT NULL,
                            deleted_at INTEGER NOT NULL,
                            PRIMARY KEY (project_id, hash, scope)
                        )
                        """,
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            foreach (var row in existingRows)
            {
                await connection.ExecuteAsync(
                        new CommandDefinition(
                            "INSERT OR IGNORE INTO sync_tombstones (project_id, hash, scope, deleted_at) VALUES (@ProjectId, @Hash, @Scope, @DeletedAt)",
                            row,
                            cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
            }

            // Drop the legacy unique index if it survived the table drop (it did not — SQLite
            // drops indexes on the table — but be explicit for clarity).
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        "DROP INDEX IF EXISTS uq_sync_tombstones_project_identity",
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

    /// <summary>The exact Ddl shape of <c>sync_tombstones</c>: declared types, NOT NULL, and the pk position of every composite-key member.</summary>
    private static bool ShapeMatchesDdl(IReadOnlyList<PragmaColumnRow> columnRows)
    {
        foreach (var expected in new[]
                 {
                     (Name: "project_id", Type: "TEXT", Pk: 1L),
                     (Name: "hash", Type: "TEXT", Pk: 2L),
                     (Name: "scope", Type: "TEXT", Pk: 3L),
                     (Name: "deleted_at", Type: "INTEGER", Pk: 0L),
                 })
        {
            var actual = columnRows.FirstOrDefault(c => c.Name == expected.Name);
            if (actual is null
                || !string.Equals(actual.Type, expected.Type, StringComparison.OrdinalIgnoreCase)
                || actual.NotNull == 0
                || actual.Pk != expected.Pk)
            {
                return false;
            }
        }

        return columnRows.Count == 4;
    }

    /// <summary>
    ///     Unconditional, ungated, every-open no-overlapping-watches prune (orchestrator ruling, S7:
    ///     demoted from a one-time v11 ladder step — see <see cref="CurrentVersion" />'s doc comment).
    ///     Per-project outermost-watch prune, resolved through <see cref="WatchOverlapResolver" />,
    ///     the SAME implementation the runtime <c>WatchService.AddAsync</c>/<c>WatchStore.ResolveAndAddAsync</c>
    ///     path uses (one implementation, many call sites) — tie-break for mutual (real-path-
    ///     equivalent) registrations: longest literal path, then earliest registration. Prune +
    ///     cascade in one transaction, mirroring <c>WatchStore.RemoveWatchAsync</c>'s shape, and only
    ///     opened when there is something to prune — idempotent and cheap otherwise (one SELECT,
    ///     grouped and resolved in memory, no write). S8 hardening: one project's malformed row
    ///     (e.g. a blank path <see cref="IngestPath.ResolveReal" /> cannot resolve) must never fail
    ///     the whole bank open — that project's resolution is skipped and reported as a warning
    ///     instead of pruned, and every other project still resolves normally.
    /// </summary>
    private static async Task<WatchOverlapPruneResult> PruneOverlappingWatchesAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<WatchRow>(
                new CommandDefinition(
                    "SELECT project_id AS ProjectId, path AS Path, created_at AS CreatedAt FROM watches",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);

        var resolver = new WatchOverlapResolver();
        var toPrune = new List<(string ProjectId, PrunedWatch Pruned)>();
        var warnings = new List<WatchOverlapPruneWarning>();
        foreach (var group in rows.GroupBy(r => r.ProjectId, StringComparer.Ordinal))
        {
            var candidates = group.Select(r => new WatchOverlapCandidate(r.Path, r.CreatedAt)).ToArray();
            try
            {
                foreach (var pruned in resolver.SelectPruned(candidates))
                {
                    toPrune.Add((group.Key, pruned));
                }
            }
            catch (Exception ex)
            {
                // Broad by design: any failure resolving one project's watches (a blank path, an
                // unresolvable symlink chain, ...) must skip that project, never crash the bank
                // open every other project still depends on.
                warnings.Add(new WatchOverlapPruneWarning(group.Key, ex.Message));
            }
        }

        if (toPrune.Count == 0)
        {
            return new WatchOverlapPruneResult([], warnings);
        }

        await connection.ExecuteAsync(
                new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
        {
            foreach (var (projectId, pruned) in toPrune)
            {
                var pathPrefix = LikePattern.Escape(pruned.Path) + "/%";
                await connection.ExecuteAsync(
                        new CommandDefinition(MemorySql.DeleteWatchFilesByProjectPathCascade,
                            new { projectId, path = pruned.Path, pathPrefix }, cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
                await connection.ExecuteAsync(
                        new CommandDefinition(MemorySql.DeleteWatch, new { projectId, path = pruned.Path },
                            cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
            }

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

        return new WatchOverlapPruneResult([.. toPrune.Select(t => t.Pruned)], warnings);
    }

    /// <summary>
    ///     S2: adds code_entries.embed_attempts if it is missing — additive, no ladder step, same
    ///     as code_entries itself. Called only from inside the Ddl block's own digest-mismatch
    ///     branch (never on a steady-state open where the digest already matches — WP1/ADR-0075's
    ///     four-statements-not-forty budget applies here too). Tolerates losing a race against
    ///     another connection doing the same thing concurrently (two connections can both detect
    ///     the same digest mismatch, e.g. the maintenance hosted service and the first real request
    ///     opening a stale bank at once): the loser's ALTER fails with "duplicate column name",
    ///     which means the column is already there — exactly the outcome being verified.
    /// </summary>
    private static async Task EnsureCodeEmbedAttemptsColumnAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var hasTable = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'code_entries'",
                cancellationToken: cancellationToken))
            .ConfigureAwait(false) > 0;
        if (!hasTable)
        {
            return; // the Ddl block just above creates code_entries; nothing to alter yet
        }

        var hasColumn = (await connection.QueryAsync<string>(new CommandDefinition(
                "SELECT name FROM pragma_table_info('code_entries')", cancellationToken: cancellationToken))
            .ConfigureAwait(false)).Contains("embed_attempts", StringComparer.Ordinal);
        if (hasColumn)
        {
            return;
        }

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                    "ALTER TABLE code_entries ADD COLUMN embed_attempts INTEGER NOT NULL DEFAULT 0",
                    cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    /// <summary>
    ///     The scope allowlist bounds every disk-reading surface, not just watching, so its keys
    ///     moved from watch.scope.* to ingest.scope.*. Carrying old rows over is not cosmetic: the
    ///     scope is deny-by-default, so a bank that kept the old key would refuse every ingest and watch add.
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

    /// <summary>
    ///     H4 (ADR-0023 amendment): the trigger's guard needed a body *replacement*
    ///     (<c>AND e.scope = 'project'</c>), and <c>CREATE TRIGGER IF NOT EXISTS</c> — the mechanism
    ///     every other object in <see cref="Ddl" /> relies on — can only ever create, never replace. An
    ///     unconditional <c>DROP TRIGGER</c> + <c>CREATE TRIGGER</c> on every open was tried and
    ///     rejected: unlike the no-op <c>IF NOT EXISTS</c> statements around it, that pair is a real
    ///     schema write every time, and <see cref="SqliteConnectionFactory" /> runs
    ///     <see cref="EnsureAsync" /> on every checkout regardless of pooling — SweepService checks
    ///     one out per entry, so a large bank would turn one maintenance pass into thousands of
    ///     schema writes, each bumping
    ///     <c>PRAGMA schema_version</c> (forcing every other connection's prepared-statement cache to
    ///     re-prepare) and each opening a window between the DROP and the CREATE where the trigger
    ///     does not exist — a concurrent delete landing in that window would produce exactly the
    ///     orphan ADR-0023 exists to prevent. Probing first (one indexed <c>sqlite_master</c> read,
    ///     same shape as <see cref="MigrateIngestScopeKeysAsync" /> above) avoids all three: no write,
    ///     no cookie bump, no window, on every open after the first corrected one.
    /// </summary>
    private static async Task EnsurePromotionQueueTriggerScopeGuardAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var storedSql = await connection.ExecuteScalarAsync<string?>(
                new CommandDefinition(
                    "SELECT sql FROM sqlite_master WHERE type = 'trigger' AND name = 'promotion_queue_entries_ad'",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        // Compared against the body we intend, not sniffed for a substring: the previous probe
        // asked whether the stored SQL mentioned "scope" at all, so the ADR-0046 widening would
        // have left every existing bank on the old body forever while reading as up to date.
        if (storedSql is not null && Normalize(storedSql) == Normalize(PromotionQueueTriggerDdl))
        {
            return;
        }

        await connection.ExecuteAsync(
                new CommandDefinition(
                    $"""
                     DROP TRIGGER IF EXISTS promotion_queue_entries_ad;

                     {PromotionQueueTriggerDdl}
                     """,
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false);
    }

    /// <summary>
    ///     Whitespace-insensitive comparison against what sqlite_master actually stores: the body as
    ///     written, minus the <c>IF NOT EXISTS</c> and minus the statement's trailing semicolon.
    /// </summary>
    private static string Normalize(string sql) =>
        string.Join(' ', sql.Replace("IF NOT EXISTS ", "", StringComparison.Ordinal)
                .Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .TrimEnd(';', ' ');

    /// <summary>
    ///     Cheaper than <see cref="EnsureAsync" /> for a hot, pre-every-call read that only needs the
    ///     schema to already be current: one <c>application_id</c> read, falling back to the full
    ///     <see cref="EnsureAsync" /> only when the digest does not match (ADR-0076's ToolGate
    ///     migration check is the first caller — see the ADR's "ToolGate's migration check cost").
    /// </summary>
    public static async Task EnsureCheapAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var digest = await ReadApplicationIdAsync(connection, cancellationToken).ConfigureAwait(false);
        if (digest != SchemaDigest)
        {
            await EnsureAsync(connection, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<long> ReadVersionAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<long>(
            new CommandDefinition("PRAGMA user_version", cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>PRAGMA user_version takes no parameter binding, hence the interpolation of an int constant.</summary>
    private static async Task StampAsync(SqliteConnection connection, int version, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(
                new CommandDefinition($"PRAGMA user_version = {version}", cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    private static async Task<int> ReadApplicationIdAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<int>(
            new CommandDefinition("PRAGMA application_id", cancellationToken: cancellationToken)).ConfigureAwait(false);

    /// <summary>PRAGMA application_id takes no parameter binding, hence the interpolation of an int constant.</summary>
    private static async Task StampSchemaDigestAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(
                new CommandDefinition($"PRAGMA application_id = {SchemaDigest}", cancellationToken: cancellationToken))
            .ConfigureAwait(false);

    /// <summary>
    ///     Ladder step 1 — everything shipped before the version marker existed: entries/watch-lease
    ///     columns, the ingest-scope key move, the FTS rebuild and the bucket-uniqueness indexes.
    ///     Idempotent; returns false when the soft bucket-index step didn't complete (bank stays
    ///     unstamped, retried next open) — must finish before <see cref="MigrateToV2Async" />.
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

        // Cross-process scan lease (docs/plans/2026-08-07-watch-scan-runaway-fix.md): the lease
        // lives on the watches row, so DELETE FROM watches is lease release.
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

        // Bucket-uniqueness indexes (docs/work/archive/2026-08-06-extraction-followups-plan.md): one
        // row per (path, hash) in the shared tier, and per (path, hash, project, scope,
        // context-label) in the committed tiers. A violating legacy bank is deduped before the
        // indexes are created (survivor = earliest row), so creation can never fail on a real bank;
        // a dedupe failure leaves the bank open, degraded, retried on the next open — deliberately
        // softer than the FTS rebuild above, which rethrows.
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
                    // Dedupe first (survivor = earliest row; content is identical within a group by
                    // construction). The GROUP BY mirrors the index expressions exactly (COALESCE
                    // included); NULL path/hash are guarded since GROUP BY equates them but UNIQUE treats them as distinct.
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

    /// <summary>
    ///     Ladder step 2 (docs/plans/2026-08-08-search-knn-perf.md): persists chunk_index/total_chunks
    ///     and rebuilds vec_entries/vec_structure with a `ctx` column. It shared its DDL with the v9
    ///     step from the start, so it now creates the demoted shape directly — the end state after a
    ///     full ladder run is identical either way, and one vec0 DDL beats two that can drift.
    ///     Rethrows on failure —
    ///     an empty-shell vec_entries answers every query with silence, worse than failing to open.
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
                        // GH #371: -1, not 0 — 0 is a real, meaningful first-chunk position; a row
                        // this migration cannot yet order needs its own "unknown" default so the
                        // bank-wide recompute below (fill-unknown-only) actually fills it in.
                        "ALTER TABLE entries ADD COLUMN chunk_index INTEGER NOT NULL DEFAULT -1",
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
    ///     Ladder step 3: a bank-wide self-heal recompute of chunk_index/total_chunks, reusing the
    ///     exact SQL <see cref="AiRaccoon.Infrastructure.Sync.SyncService" />'s merge uses. Heals rows
    ///     an older binary wrote without running the write-path recompute (docs/plans/2026-08-08-search-knn-perf.md §3.3).
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

    /// <summary>
    ///     Ladder step 4 (ADR-0018): adds promotion_queue.scorer_version. DEFAULT 0 on the column
    ///     covers a fresh bank; existing rows backfill to 0 by SQLite's own ADD COLUMN default —
    ///     deliberately, since none of them were ever scored by a versioned scorer.
    /// </summary>
    private static async Task MigrateToV4Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var columns = (await connection.QueryAsync<string>(
                new CommandDefinition(
                    "SELECT name FROM pragma_table_info('promotion_queue')",
                    cancellationToken: cancellationToken))
            .ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        if (!columns.Contains("scorer_version"))
        {
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        "ALTER TABLE promotion_queue ADD COLUMN scorer_version INTEGER NOT NULL DEFAULT 0",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     Ladder step 5 (memory source normalization): creates memory_source table, adds source_id
    ///     FK column to entries, backfills from existing source_file/section, and rebuilds FTS.
    /// </summary>
    private static async Task MigrateToV5Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
                new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        try
        {
            // 1. Create memory_source table (idempotent via IF NOT EXISTS in Ddl, but the
            //    migration needs it before the backfill so we ensure it here too).
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        CREATE TABLE IF NOT EXISTS memory_source (
                            id            INTEGER PRIMARY KEY,
                            source_type   TEXT NOT NULL CHECK(source_type IN ('file','transcript','manual')),
                            source_locator TEXT NOT NULL,
                            section       TEXT NULL,
                            heading_path  TEXT NULL
                        );

                        CREATE UNIQUE INDEX IF NOT EXISTS uq_memory_source
                            ON memory_source(source_type, source_locator, COALESCE(section, ''));
                        """,
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            // 2. Populate from existing entries (deduplicated by the unique constraint).
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        INSERT OR IGNORE INTO memory_source (source_type, source_locator, section)
                        SELECT
                            CASE
                                WHEN source_file LIKE 'hermes/%' OR source_file LIKE '%/hermes/%' THEN 'transcript'
                                WHEN source_file IS NULL OR source_file = '' THEN 'manual'
                                ELSE 'file'
                            END,
                            COALESCE(source_file, ''),
                            section
                        FROM entries
                        WHERE source_file IS NOT NULL OR section IS NOT NULL;

                        INSERT OR IGNORE INTO memory_source (source_type, source_locator, section)
                        VALUES ('manual', '', NULL);
                        """,
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            // 3. Add source_id column to entries.
            var columns = (await connection.QueryAsync<string>(
                    new CommandDefinition(
                        "SELECT name FROM pragma_table_info('entries')",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
            if (!columns.Contains("source_id"))
            {
                await connection.ExecuteAsync(
                        new CommandDefinition(
                            "ALTER TABLE entries ADD COLUMN source_id INTEGER NULL",
                            cancellationToken: cancellationToken))
                    .ConfigureAwait(false);
            }

            // 4. Backfill source_id.
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        UPDATE entries
                        SET source_id = (
                            SELECT ms.id FROM memory_source ms
                            WHERE ms.source_locator = COALESCE(entries.source_file, '')
                              AND ms.section IS entries.section
                              AND ms.source_type = CASE
                                  WHEN entries.source_file LIKE 'hermes/%' OR entries.source_file LIKE '%/hermes/%' THEN 'transcript'
                                  WHEN entries.source_file IS NULL OR entries.source_file = '' THEN 'manual'
                                  ELSE 'file'
                              END
                        );
                        """,
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            // 5. Verify no NULL source_id remains.
            var nullCount = await connection.ExecuteScalarAsync<long>(
                    new CommandDefinition(
                        "SELECT count(*) FROM entries WHERE source_id IS NULL",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);
            if (nullCount > 0)
            {
                throw new InvalidOperationException(
                    $"MigrateToV5Async: {nullCount} entries still have NULL source_id after backfill");
            }

            // 6. Create source_id index.
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        "CREATE INDEX IF NOT EXISTS idx_entries_source_id ON entries(source_id)",
                        cancellationToken: cancellationToken))
                .ConfigureAwait(false);

            // 7. Rebuild FTS (same pattern as MigrateToV1Async).
            await connection.ExecuteAsync(
                    new CommandDefinition(
                        """
                        DROP TRIGGER IF EXISTS entries_fts_ai;
                        DROP TRIGGER IF EXISTS entries_fts_ad;
                        DROP TRIGGER IF EXISTS entries_fts_au;
                        DROP TABLE IF EXISTS entries_fts;

                        CREATE VIRTUAL TABLE entries_fts USING fts5(
                            value, source_file, section, content='entries', content_rowid='id'
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

    /// <summary>Drops and recreates one vec0 table in the partitioned shape, repopulating it from <paramref name="sourceColumn" />.</summary>
    private static async Task RebuildVecTableAsync(SqliteConnection connection, string table, int dimension,
        string sourceColumn, string wherePredicate, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(
                new CommandDefinition($"DROP TABLE IF EXISTS {table}", cancellationToken: cancellationToken))
            .ConfigureAwait(false);
        await connection.ExecuteAsync(
                new CommandDefinition(
                    $"CREATE VIRTUAL TABLE {table} USING vec0(ctx TEXT, embedding float[{dimension}] distance_metric=cosine)",
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

    private sealed record PragmaColumnRow(string Name, string Type, long NotNull, long Pk);

    private sealed record TombstoneRow(string ProjectId, string Hash, string Scope, long DeletedAt);

    private sealed record WatchRow(string ProjectId, string Path, long CreatedAt);

    /// <summary>One project's watch-overlap resolution failed on this bank open and was skipped —
    /// its watches are left untouched rather than risk failing the whole bank open (S8).</summary>
    internal sealed record WatchOverlapPruneWarning(string ProjectId, string Reason);

    /// <summary>The unconditional no-overlapping-watches prune step's outcome for one bank open.</summary>
    internal sealed record WatchOverlapPruneResult(
        IReadOnlyList<PrunedWatch> Pruned,
        IReadOnlyList<WatchOverlapPruneWarning> Warnings);
}
