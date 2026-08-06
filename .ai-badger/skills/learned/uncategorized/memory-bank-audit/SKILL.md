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

## Pitfalls

- `SELECT COUNT(*) total, (subquery) ...` — an aggregate column with NO `FROM` clause returns **1** (SQLite's implicit single row), not the table count. Use `(SELECT COUNT(*) FROM entries)` subquery form or `COUNT(*) FROM entries`. This looks like a bank anomaly and is not — it's a query typo.
- `created_at` is **insert-stamped, not commit-stamped**: a long-held write transaction (rows stamped at insert, embedded serially, committed minutes later) makes windowed counts (`created_at > now-600`) disagree with `COUNT(*)` for minutes. Rows can also be created-then-deleted (probe write + cleanup) — windowed counts see them, totals don't. Reconcile, don't panic.
- Multiple server processes on ONE bank (5-6 ai-raccoon instances all loading watch config) each run a watcher → concurrent ingests of the same file event → duplicate rows (measured 14.2% of the bank). Expected at this deployment shape; flag the fix (UNIQUE index on (project_id, path, hash, scope, context_label, workspace_id) or single ingester).
- Zombie dev servers (running from a deleted worktree, no --data-root) write to the LIVE bank with older, pre-dedup semantics. Check `ps` for ai-raccoon processes and their data roots before attributing rows.
- Data packages / orchestrator notes are snapshots; every claim they make about the bank should be re-queried if you cite it.

## Support files

- `references/sql-query-cookbook.md` — copy-paste SQL for every audit dimension (version dup, hash dup, per-day/hour, windowed counts, metadata gaps, sync/workspaces, settings) + v3 audit baseline snapshot (2026-08-06) for future comparison.
