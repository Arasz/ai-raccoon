---
name: memory-bank-audit
description: Use when auditing a SQLite memory bank read-only.
---

# Memory-bank audit (read-only SQLite forensics)

Class of work: periodic memory-usage audits (v1/v2/v3 pattern in ai-raccoon/docs/work/), MoE lanes analyzing a live `memory.db` without writing to it or building the product. Covers: churn mechanics, version accumulation, dedup semantics, TTL/sweep self-correction, shared-tier freshness, metadata gaps, growth projection.

## Workflow

1. **Establish ground truth first.** Open the bank read-only: `sqlite3 "file:$HOME/.ai-raccoon/memory.db?mode=ro"`. Before trusting any time series / sampler / MCP stats, reconcile it against direct SQL in ONE connection (e.g. `SELECT COUNT(*)` vs the series' `entries_total`). A sampler's fields can be internally inconsistent (windowed counts vs COUNT(*)); direct SQL wins.
2. **Normalize timezones before reconciling anything.** macOS `stat -f "%Sm"` prints LOCAL time; DB `created_at` is unixepoch = UTC. Data packages often use local time. Mismatches manufacture fake "contradictions" (file mtime vs row created_at, event ordering). Convert first (local = UTC+2 here), then compare.
3. **Schema + pragmas:** `.schema entries`, `PRAGMA page_count/page_size/freelist_count`, settings table, watch_files schema. Note `vec0` may not be loadable in the plain sqlite3 CLI — vec tables are unreadable that way; use the schema/triggers as evidence instead.
4. **Version accumulation:** same `source_file` with rows on 2+ distinct days, or MAX-MIN created_at spread > 60s → true re-ingest generations. Also code-read the watcher: replace-by-path (delete old generation then ingest, hash-skip unchanged) vs additive ingest — the bank's accumulation behavior is decided by the BUILD that wrote the rows, not by the report narrative. One generation per file ≠ "churn"; the burst may be first ingests of NEW files.
5. **Dedup semantics:** count hash dup groups (`GROUP BY hash HAVING COUNT(*)>1`). Distinguish the two duplicate mechanisms:
   - multi-process race: chunk text appears ONCE in the source doc (`grep -c`) yet has N copies, with interleaved ids/seconds → N concurrent watchers racing a non-atomic check-then-insert (no UNIQUE constraint).
   - blind-insert of repeated content: identical-value pairs inside ONE contiguous id block → a single ingester without the dedup check (often a stale dev build). Verify id contiguity (single transaction) vs interleaving (concurrent transactions).
   - cross-scope hash collisions: shared rows vs project rows — 0 collisions means "shared copies by design" is NOT realized; shared rows are original content.
6. **Self-correction audit:** `SELECT COUNT(ttl_days)`; settings for sweep thresholds; code-read `DegradationPolicy.ShouldDegrade` — if it requires `ttlDays.HasValue`, rows with NULL ttl can NEVER degrade; sweep is a no-op regardless of rating. Default-rating + never-accessed rows (rating 0.5, access 0) have no removal path at all. Check whether anything SCHEDULES the sweep (hosted service) vs manual tool only.
7. **Access/rating loop:** distribution of access_count (94%+ at 0 is normal here), `MAX(last_accessed_at)` to date the last real search hit. Re-verify "access 0" claims at the END of the audit — live banks move under you (a search during the audit bumped the shared tier).
8. **Growth model:** decompose per-day and per-hour creation counts into discrete events (one-time bulk load, maintenance re-ingest, watch-init scan, organic new docs). NEVER extrapolate a burst (e.g. 43 rows/10min = ONE 43-chunk file ingest, series decays to 0 between events). Storage per row from `page_count*page_size - freelist` / row count. Project quiet vs heavy scenarios; name the wrong extrapolation explicitly.
9. **Grade every finding** `[MEASURED]` (you ran it / it's in the data) or `[INFERRED]` (reasoned), with SQL/file/line evidence. End with a verdict + top N risks at 10x scale.
10. **Space-utilization analysis (sqlite3_analyzer):** the tool for "why is the bank file this big / what is it wasting" questions. It is space-only forensics (freelist, unused bytes, fragmentation per table/index) — blind to the WAL and to
    query performance (doc: 0 hits for wal/performance). On a live WAL-mode bank: `.backup` snapshot first (preserves freelist/layout — cross-check with live `PRAGMA page_count/freelist_count`, they matched exactly), analyze the snapshot,
    then quantify reclaim with `VACUUM INTO` + size compare. NEVER analyze a vacuumed copy when the question is live-bank waste — vacuuming destroys the evidence. 2026-08-06 baseline: bank 47.9% freelist (3446/7200 pages), VACUUM → −48%
    (29.5 → 15.3 MB); ENTRIES 33.7% unused bytes, 41.9% non-sequential pages; a 431 MB WAL beside the 29.5 MB DB (checkpoint issue, invisible to the tool). Full recipe in references/sqlite3-analyzer.md.
11. **WAL + holder forensics** (for "why is the WAL huge" / footprint >> logical): the decisive test runs on a COPY of db+wal+shm — `PRAGMA wal_checkpoint(TRUNCATE)`; tuple `0|0|0` and the file drops to ~0 bytes = the whole WAL was
    already-checkpointed garbage (crash replays nothing). A huge-but-checkpointable WAL + pooled connections (Microsoft.Data.Sqlite default ON) in N long-lived processes is the classic never-truncates shape: passive auto-checkpoints sync
    content, but truncation needs a reader-free window and close-time truncation only fires on the LAST connection closing — which pooling prevents. `grep wal_checkpoint|wal_autocheckpoint|auto_vacuum|VACUUM|ANALYZE` — no hits = no
    maintenance path; the only TRUNCATE may sit in a sync flow that never ran (verify via its metadata table row count). Enumerate holders with `lsof` + `ps -o lstart`; date sidecars with APFS birth time (`stat -f "%N born=%SB"`) to
    correlate WAL birth with process starts. Check `sqlite_stat%` tables (0 = ANALYZE never ran; low impact for point-lookup/FTS5/vec0 workloads). Full recipe + baseline: references/physical-layer-space-wal-audit.md.

## Pitfalls

- `SELECT COUNT(*) total, (subquery) ...` — an aggregate column with NO `FROM` clause returns **1** (SQLite's implicit single row), not the table count. Use `(SELECT COUNT(*) FROM entries)` subquery form or `COUNT(*) FROM entries`. This looks like a bank anomaly and is not — it's a query typo.
- `created_at` is **insert-stamped, not commit-stamped**: a long-held write transaction (rows stamped at insert, embedded serially, committed minutes later) makes windowed counts (`created_at > now-600`) disagree with `COUNT(*)` for minutes. Rows can also be created-then-deleted (probe write + cleanup) — windowed counts see them, totals don't. Reconcile, don't panic.
- Multiple server processes on ONE bank (5-6 ai-raccoon instances all loading watch config) each run a watcher → concurrent ingests of the same file event → duplicate rows (measured 14.2% of the bank). Expected at this deployment shape; flag the fix (UNIQUE index on (project_id, path, hash, scope, context_label, workspace_id) or single ingester).
- Zombie dev servers (running from a deleted worktree, no --data-root) write to the LIVE bank with older, pre-dedup semantics. Check `ps` for ai-raccoon processes and their data roots before attributing rows.
- Data packages / orchestrator notes are snapshots; every claim they make about the bank should be re-queried if you cite it.
- vec0 chunk tables allocate in **1024-vector capacity units**: one chunk row = `1024 × vector_bytes` (float[384] → rows of exactly 1,572,864 B). `count(*)` on chunk tables counts CHUNK ROWS, not vectors — 4 rows is not 4 vectors; 1772
  vectors can live in 4 rows (2 full + 2 partial chunks). Verify with `<table>_chunks` validity bitmaps + `<table>_rowids` counts; chunk-table dbstat pages are capacity allocation and VACUUM cannot shrink them — not bloat. (Nearly published
  as "vector index emptied" 2026-08-06; caught via dbstat + `length(vectors)` before it entered the record.)

## Support files

- `references/sql-query-cookbook.md` — copy-paste SQL for every audit dimension (version dup, hash dup, per-day/hour, windowed counts, metadata gaps, sync/workspaces, settings) + v3 audit baseline snapshot (2026-08-06) for future comparison.
- `references/sqlite3-analyzer.md` — sqlite3_analyzer space-utilization audit: binary acquisition (sqlite.org anti-robot PRODUCT CSV trick), .backup-first workflow, VACUUM-into reclaim quantification, scope limits (blind to WAL and query
  perf), measured 2026-08-06 baseline.
- `references/physical-layer-space-wal-audit.md` — WAL/holder/vec0 forensics: checkpoint-on-copy test and tuple semantics, WAL-never-truncates mechanism, lsof + APFS birth-time dating, vec0 chunk-capacity gotchas, sqlite_stat check,
  2026-08-06/07 baseline.
