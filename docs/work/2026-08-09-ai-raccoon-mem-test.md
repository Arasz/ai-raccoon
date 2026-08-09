# AiRaccoon memory server — live test (2026-08-09)

Task: `mem-test` — exercise the live memory server end-to-end: all tools, metrics access,
DB contents, watch health, promotion queue. Method per the mcp-tool-surface-testing skill:
expectations written before calls, live contract outranks docs, isolated test project
(`memtest-x`, zero residue verified after).

## Environment (measured)

- `ai-raccoon serve --restart` (PID 2047) on 127.0.0.1:7721 — real bank `~/.ai-raccoon/memory.db`
  (142.4 MB, WAL-mode, WAL 152 KB — checkpointed; freelist 434 pages).
- Hermes MCP bridge: stdio `ai-raccoon --quiet` (installed global tool). Installed version
  **1.6.0**; repo main is 1.6.1 (bump committed) — install lags repo by one patch.
- HTTP `/mcp` now requires a token (`X-AiRaccoon-Token` / Authorization, token file
  `~/.ai-raccoon/mcp-token`, 0600): unauthenticated POST → 401 `-32001`.
- `/metrics` and `/health` → 404. No OTEL env on the serve process, no collector on
  4317/4318 — OTLP export is NOT live right now; metrics are tool-visible only.
- `memory-operations.jsonl` (494 ops) records **only the hermes provider channel**
  (hermes-default 448, hermes-itest 46); bridge MCP calls are not logged there.

## Tool surface: 23 tools + 2 prompts — all exercised

HTTP `tools/list` (with token): 23 tools. Bridge `list_prompts`: 2 prompts
(`workspace-consolidation-guide`, `memory-usage-guide`, project-templated content ✓).

| # | Tool | Result |
|---|------|--------|
| 1 | memory_search | PASS — round-trips at rank 1; scope=project/shared; meta carries queue stats |
| 2 | memory_stats | PASS — project-scoped entries/pending/contexts |
| 3 | memory_list | PASS — indexed-file JSON tree |
| 4 | memory_write | PASS / **see Finding 1** — hash math exact (path=SHA256(content).md, hash=SHA256(path+content), verified byte-for-byte) |
| 5 | memory_share | PASS — shared:true; shared row re-hashed (shared/ path prefix) |
| 6 | memory_share_extract | PASS — propose queues + persists; promote drains (promotedHashes); projectIds is plural |
| 7 | memory_promotion_list | PASS — per-project or bank-wide (omit projectId); previews + reasons |
| 8 | memory_promotion_discard | PASS — {discarded:1} |
| 9 | memory_set_ttl | PASS — set/clear; `canEverExpire:false` explains the rating gate (0.5 > 0.3) |
| 10 | memory_sweep | PASS — dry + real run, both negative controls {candidates:[],deleted:[]} |
| 11 | memory_embed_pending | PASS — {processed:0, pending:0} (write-time embedding active) |
| 12 | memory_ingest_file | PASS — {indexed:0} (hash-skip dedup) |
| 13 | memory_ingest_directory | PASS — {scanned:0} (hash-skip dedup) |
| 14 | memory_delete | PASS — {deleted:0} bogus / {deleted:1} real; deleting an entry invalidates its queue row (ADR-0023, observed live) |
| 15 | memory_delete_context | PASS — {deleted:3} for `project:<id>` |
| 16 | memory_sync | PASS — typed `sync-not-configured` + remedy (sync_meta empty; no cloud sync configured) |
| 17 | memory_watch_add | PASS — registration echoed |
| 18 | memory_watch_status | PASS — Healthy with lastSync; Scanning fallback semantics confirmed |
| 19 | memory_watch_remove | PASS |
| 20 | memory_workspace_begin | PASS |
| 21 | memory_workspace_status | PASS — outbox listing |
| 22 | memory_workspace_consolidate | PASS — {promoted:1, discarded:0}, row moved to project scope |
| 23 | memory_workspace_discard | PASS — {discarded:1} |

Watch cycle proven end-to-end on the test project: CLI config (enable + scope) → watch_add →
initial scan → Healthy + lastSync → files searchable (rank 1-2, embedded) → watch_remove →
config removed → zero residue (entries/promotion_queue/watches/watch_files/settings all 0).
Workspace state machine recorded Closed / Discarded transitions.

## What the DB looks like (read-only, 13,523 entries)

- Per project: jsaa 7,787 · ai-raccoon 3,051 · ai-badger 1,764 · arasz-home-page 814 ·
  hermes-default 95; custom-scope 12 (test labels); workspace rows 3; **shared 1**.
- **All rows embedded, pending = 0** (write-time embedding + watch-digest embed both work).
- TTL: **0 rows** carry ttl_days → nothing is sweepable by design (rating gate also blocks:
  avg rating 0.539 vs threshold 0.3; 934/13,523 rows ever accessed, 6.9%).
- Growth: 08-05: 374 · 08-06: 180 · 08-07: 2,435 · 08-08: 9,479 (watch scans + re-ingests) ·
  08-09: 1,055. No churn signature: per-path chunk counts == distinct hashes (biggest 191/191)
  — the watch-loop pattern is absent.
- Config: access.mode=full, embedding local:bundled, extract enabled+propose, sweep enabled,
  maintenance vacuum interval 1d. Watch scope is unified under `ingest.scope.*` (per-project
  JSON arrays; global = /Users/arasz/RiderProjects, global watch disabled).
- Debris (harmless, test-era): 8 stale **Active** workspaces (acme ×2 from 08-04,
  manual-13x-probe ×5, manual-d1d2d3-verify ×1) and 12 custom-scope rows.

## Watches: working

7 registrations across 4 projects. Live state: ai-raccoon/docs **Healthy** (lastSync
19:54:00), jsaa/docs + ai-badger/docs + arasz-home-page/docs have fresh lastChange
(08-08/08-09). The `.ai-badger/mcp-tools.json` **file** watch reports `Scanning` — the known
restart fallback, not a defect: its mirror row updated 17:20:08 and 16 chunks exist, so the
file-mode watch (parent-dir + Filter, PR #47) is functional. Zero "Watch event source error"
in the logs.

## Promotion queue: 998 waiting, over capacity, nothing promoting

- Bank-wide queue: **998** (jsaa 249, arasz-home-page 249, ai-badger 248, ai-raccoon 246,
  hermes-default 6); oldest queued 08-08 18:08 (~28 h). Scores 2.68–3.44; reasons
  adr/rule-language/measured-values/verified-contract.
- Shared tier: **1 row**. Extraction hosted service runs propose mode every interval
  (last pass 17:30: "5 projects, 0 promoted") and keeps adding candidates; nothing ever
  promotes, so the queue grows past capacity: meta on every call reports
  `capacity {reserved:200, used:246, borrowing:true}` for ai-raccoon (all projects borrow;
  hermes-default is the only one under). The reserved value is **dynamic** (200 → 166 across
  calls; semantics not documented).
- Queue meta is project-scoped when projectId is passed (246 for ai-raccoon) vs bank-wide
  (998) when omitted — consistent with SQL.
- The queue is the visible symptom of the in-flight `fix-promotion-algorithm` task; the
  memory-usage-guide documents eviction ("weakest candidate of the biggest occupier") but
  eviction has not fired at 5× reserved capacity.

## Finding 1 (BUG, reproduced): watch replace-by-path deletes manual writes citing the watched file

`MemorySql.DeleteBySourcePath` (MemorySql.cs:264) deletes by **source_file**, not `path`:

```sql
DELETE FROM entries
WHERE project_id = @projectId AND workspace_id IS NULL
  AND (source_file = @path OR source_file LIKE @pathPrefix ESCAPE '\')
```

The watch digest calls this for every changed file (replace-by-path). Mirror rows carry
`path = source_file = <real path>`; **manual `memory_write` rows carry `path = <hash>.md` but
whatever `sourceFile` the caller cited**. So a manual write citing a watched file is deleted
as collateral the next time that file changes.

Experiment (test project, all rows verified in SQL):
1. `memory_write(sourceFile=/tmp/memtest-2WO0/alpha.md)` → row present (hash valid).
2. Appended one line to alpha.md → watch digest ran.
3. Row **gone**; alpha.md re-ingested with new content. A second write with the same
   sourceFile was deleted too — 2/2 collateral victims. Writes issued during the initial
   scan also blocked ~95 s behind the scan's write transaction (one of them was the first
   victim).

Impact: any manual/scripted write (or provider turn) that cites a watched file path as
`sourceFile` is silently lost on the file's next change. The jsaa corpus chunks cite watched
doc paths — a doc edit deletes its chunks and re-ingests only the file's own chunks, so
corpus rows survive by re-creation, but anything else citing that path does not.

Suggested fix (not applied — belongs to a task/PR): match on `path` instead of `source_file`
(mirror/ingest rows store the real path; manual rows store the hash filename, so
`path = @path` cannot hit manual rows), or tag mirror-owned rows explicitly.

## Minor findings

- Shared path for file-sourced rows is `shared//tmp/...` (double slash, cosmetic).
- `capacity.reserved` varies across calls (200 → 166) — undocumented semantics.
- Installed tool 1.6.0 vs repo 1.6.1 (one-patch lag).
- `/metrics` + `/health` 404; OTLP export off — "metrics" today = memory_stats + queue meta +
  maintenance-stats.json + ops log.

## Verdict

Server is healthy: 23/23 tools behave per contract, embeddings complete, watches live,
sweep correctly inert (no TTLs), workspace/queue state machines correct, zero test residue.
One real defect (Finding 1) — silent loss of manual entries citing watched files — worth an
issue + fix PR. Promotion is operationally stalled (998 queued vs 1 shared row, propose-only
extraction) — the in-flight fix-promotion-algorithm task is the right place to land the
remedy.
