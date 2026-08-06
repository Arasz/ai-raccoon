# Research: AiRaccoon memory usage audit — bank contents, feature adoption, and data quality

**Date:** 2026-08-06
**Question:** What is actually in the shared memory bank, which AiRaccoon features are used in production, were any memories promoted, and what does the usage data say about growth?

## Findings

### F1 — The whole installation is ONE bank: 2,649 entries, 3 projects, zero shared rows [MEASURED]

`~/.ai-raccoon/memory.db` (20.9 MB + 14.7 MB WAL) holds every project's memory — no other `memory.db` exists on the machine and the jsaa `.mcp.json` also points at the installed tool, so all agents share this one bank. 2,649 entries: ai-raccoon 1,276, job-search-ai-assistant 762, ai-badger 611. Every row is `scope='project'`; there are ZERO `scope='shared'` rows and ZERO custom-scope rows. Embedding: 2,647 `embedded`, 2 `pending`. `memory_stats` via the live bridge returns the same numbers (1276/0, 762/0, 611/2) and its `contexts` list contains only the three `project:` contexts — no `"shared"`, confirming the tier is empty from the server's own view.

**Evidence:** `sqlite3 "file:$HOME/.ai-raccoon/memory.db?mode=ro"` — `.tables`, `SELECT scope, COUNT(*) FROM entries GROUP BY scope`, `SELECT project_id, COUNT(*) ... GROUP BY project_id`, `SELECT embed_state, COUNT(*) ... GROUP BY embed_state`; `find ~ -maxdepth 4 -name memory.db`; live MCP `memory_stats` for all three projectIds (2026-08-06 12:2x, AiRaccoon 1.0.7.0 via the Hermes bridge).

### F2 — Promotion (`memory_share`) was exercised exactly once — in the tool-surface test — and the shared tier is now empty [MEASURED]

The memory-grade log shows the shared tier was populated once, during the full-surface tool test on project `tool-test-20260806` (2026-08-06 09:17:59 UTC): a `scope=shared` search returned `shared/48056986…md` (the "quick brown fox" fixture, ranking 1), and a subsequent `scope=all` search at 09:18:36 still saw it. The test's cleanup then deleted it (the test record documents "zero residue confirmed after"; `memory_delete_context('shared')` wipes the whole tier). No organic `memory_share` call appears anywhere in session history (searched `memory_share` across recent sessions — hits are only docs/skill text and the tool test).

**Evidence:** `~/.ai-badger/memory-grade/memory-quality.jsonl` lines 2026-08-06T09:17:17/09:17:59/09:18:36 (host=hermes, projectId=tool-test-20260806); session_search("memory_share") — 5 sessions, no organic call; live `memory_stats` shows no `shared` context; `SELECT COUNT(*) FROM entries WHERE scope='shared'` = 0.

### F3 — Organic search usage is sparse: 11 logged searches since the grade hook started, most of them test probes [MEASURED]

`AI_BADGER_MEMORY_GRADE=1` is set and the hook is live (host=hermes), so every `memory_search` since ~21:11 Aug 5 lands in `~/.ai-badger/memory-grade/memory-quality.jsonl`. The file has exactly 11 lines: 10 hermes + 1 manual (`host: null`). 7 of the 11 are tool-test probes; organic searches are 3 for ai-raccoon ("hermes plugin live gate", "encryption sidecar bitwarden", "static class classification invariants") and 1 for ai-badger. 2 searches were graded (4 and 5). Scopes used: `all` ×7, `project` ×2, `shared` ×2 (both test). No claude/copilot host lines exist even though jsaa's `.mcp.json` declares ai-raccoon — either Claude Code never searches or the hook doesn't fire there.

**Evidence:** python parse of `/Users/arasz/.ai-badger/memory-grade/memory-quality.jsonl` (11 rows; Counter over host/scope/projectId/usefulness; ts range 2026-08-05T19:11:59Z → 2026-08-06T09:18:36Z); `env | grep AI_BADGER` shows `AI_BADGER_MEMORY_GRADE=1`.

### F4 — Organic writes: exactly 5 in the bank's lifetime; everything else is ingest or watch mirroring [MEASURED]

Five rows were written by agents via `memory_write` (all without `sourceFile` except one): id 986 (ai-badger, 2026-08-05 17:46, "[facts] ai-badger integration … 1.0.4 global tool ships the fixed multi-RID shell"), ids 1159–1161 (ai-raccoon, 2026-08-05 20:23: "docs sync audit", "integration review PRs #24/#25/#26", "code-review-graph state"), and id 2577 (ai-raccoon, `sourceFile='src/AiRaccoon/Setup/McpServerSetup.cs'`, the "REGRESSION: watch tools not on MCP surface" note). The other 2,644 rows trace to the jsaa ingest script (762), `memory_ingest_directory` (1,423), and the file watcher (464 churn rows on Aug 6).

**Evidence:** `SELECT id, project_id, datetime(created_at,'unixepoch'), substr(value,1,140) FROM entries WHERE (source_file IS NULL OR source_file='') AND project_id != 'job-search-ai-assistant'`; `SELECT … WHERE source_file='src/AiRaccoon/Setup/McpServerSetup.cs'`; per-project per-day creation counts.

### F5 — The file watcher is the only actively-running background feature: 3 registrations, live Healthy, 113 tracked files [MEASURED]

Registered watches: ai-raccoon/docs, ai-raccoon/.ai-badger/mcp-tools.json, ai-badger/docs. Live `memory_watch_status(ai-raccoon)` reports both ai-raccoon watches **Healthy**, lastSync 10:30:53 / 10:31:17 UTC today. `watch_files` holds 113 digests (ai-raccoon 86, ai-badger 24, tool-test 3 residue). Watch config is enabled per project (`watch.enabled.ai-raccoon=true`, `.ai-badger=true`), scopes are `{ai-badger: docs}`, `{ai-raccoon: .ai-badger + docs}`, global scope `/Users/arasz/RiderProjects`. The watcher is actively re-ingesting: 464 new ai-raccoon entries on Aug 6 came from docs churn (additive ingest on change).

**Evidence:** `SELECT * FROM watches`; live `memory_watch_status`; `SELECT project_id, COUNT(*) FROM watch_files GROUP BY 1`; settings table `watch.*` keys; per-day creation counts.

### F6 — Watch-mirrored entries are never embedded: the only 2 `pending` rows in the bank are watcher ingests from this morning [MEASURED]

Entries 3068 and 3091 (ai-badger project, created 2026-08-06 10:13:16/10:13:17 UTC) are `embed_state='pending'`: `/Users/arasz/RiderProjects/ai-badger/docs/changelog/0.81.0-project-local-invariants.md` and `/Users/arasz/RiderProjects/ai-badger/docs/authoring-a-feature.md`. They were ingested by the ai-badger docs watch (watch_files for ai-badger last touched 10:15:05) and never embedded — no `memory_embed_pending` run since. They are FTS-searchable only; vector retrieval and hybrid fusion miss them until embedded. `memory_stats(ai-badger)` confirms `pending: 2` live.

**Evidence:** `SELECT id, project_id, source_file, datetime(created_at,'unixepoch') FROM entries WHERE embed_state='pending'`; watch_files updated_at; live `memory_stats(ai-badger)` → `{"entries":611,"pending":2,...}`.

### F7 — BUG: watching a FILE path breaks the watcher — recurring "directory name does not exist" errors for the mcp-tools.json watch [MEASURED]

The mcp-tools.json file-watch (registered via `memory_watch_add`) fails at runtime: `~/.hermes/logs/mcp-stderr.log` contains repeated `Watch event source error for ai-raccoon on /Users/arasz/RiderProjects/ai-raccoon/.ai-badger/mcp-tools.json: The directory name '...mcp-tools.json' does not exist. (Parameter 'path')` with the full `System.ArgumentException` stack. Root cause in code: `src/AiRaccoon.Infrastructure/Watch/WatchEventSource.cs:45` constructs `new FileSystemWatcher(normalized)` on the registered path, and `FileSystemWatcher` requires a directory — a file path throws at watcher creation. So a file-path registration is accepted by the tool, stored, reported Healthy, but its event source never works.

**Evidence:** grep of `~/.hermes/logs/mcp-stderr.log` (recurring error, e.g. lines 1347991/1348162); `src/AiRaccoon.Infrastructure/Watch/WatchEventSource.cs:42-45` (`new FileSystemWatcher(normalized)`); registration row `ai-raccoon | .../mcp-tools.json | created 2026-08-06 09:14:09`.

### F8 — Provenance drift: the jsaa corpus carries its structured path in `agent_id`, not `source_file`; section/structure metadata is otherwise near-absent [MEASURED]

All 762 jsaa rows have `source_file` NULL and `agent_id` = the structured path (e.g. `ai-badger:agent-instructions/README.md`, `docs:adr:0062-…`, `remember:archive.md#2026-07-14`) — `scripts/ingest-jsaa-docs.py:875` passes `agent_id=chunk.structured_path`, a pre-Wave-2 provenance carrier. The ai-raccoon/ai-badger corpora (1,882 rows) have absolute-path `source_file` but only **1 row has a `section` value, 0 rows have `heading_path`, and 0 of 2,649 rows have a `structure_embedding`** — the dual-vector structure signal shipped in Wave 6 is not populated in the live bank. Search results still carry chunkIndex/totalChunks for source_file rows, but the section-level metadata and structure vectors that section-targeted retrieval depends on are absent.

**Evidence:** `SELECT project_id, (source_file IS NULL OR source_file=''), COUNT(*) FROM entries GROUP BY 1,2`; `SELECT COUNT(section), COUNT(heading_path), COUNT(structure_embedding) FROM entries`; `grep -n "agent_id" scripts/ingest-jsaa-docs.py` → line 875 `agent_id=chunk.structured_path`; agent_id value sample.

### F9 — Degradation/access machinery is idle: 89% of rows never accessed, no TTLs, ratings only moved by access [MEASURED]

2,357/2,649 rows (89%) have `access_count=0` and rating exactly 0.5 (default). 268 rows were accessed once, 24 rows 2–5 times; every non-0.5 rating (0.532–0.700) belongs to an accessed row. Zero rows set `ttl_days`. No workspace rows exist in `entries`; the `workspaces` table holds 5 test-only workspaces (3 `acme` from feature tests, 2 `tool-test-20260806`, all Closed). `sync_meta` and `sync_tombstones` are empty — cloud sync has never been configured or run. Settings contain no `retrieval.alpha`, no `sweep.threshold`, no `encryption.*` rows, and no `memory.db.source` sidecar exists — everything except embedding provider and watch config is at defaults. Access marks are written by `MemorySql.cs:225` (`SET access_count = access_count + 1`); whether search hits bump it is not settled (see Still open).

**Evidence:** `SELECT CASE WHEN access_count=0 THEN '0' … GROUP BY 1`; `SELECT rating, COUNT(*) …`; `SELECT COUNT(ttl_days)`; `SELECT * FROM workspaces`; `SELECT * FROM sync_meta`; `SELECT key, value FROM settings`; `ls ~/.ai-raccoon/memory.db.source` → absent; `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:220-225`.

### F10 — Ghost and residual config rows survive: `watch.enabled.CLAUDE.md=true` with no registration, tool-test residue [MEASURED]

Settings table holds `watch.enabled.CLAUDE.md=true` — the documented shell-glob ghost (an unquoted `*` in `watch enable` wrote a per-filename key) — with NO `CLAUDE.md` watch registration and NO scope for that project, so it is inert config clutter. Also present: `watch.enabled.tool-test-20260806=false`, 3 tool-test rows in `watch_files`, and the 2 closed tool-test workspaces. `watch remove` exists for exactly this cleanup but the ghosts were never removed.

**Evidence:** `SELECT key, value FROM settings WHERE key LIKE '%CLAUDE%' OR key LIKE '%tool-test%'`; `SELECT * FROM watches` (no CLAUDE.md row); `SELECT project_id, COUNT(*) FROM watch_files GROUP BY 1`.

### F11 — The live server works: stats/status probes match the SQL ground truth [MEASURED]

The Hermes-bridge server (AiRaccoon 1.0.7.0) answers `memory_stats` and `memory_watch_status` correctly and consistently with direct SQL: per-project counts match to the row, `pending: 2` matches, Healthy states match the most recent watch activity. The mcp-stderr log shows 17,138 ai-raccoon lines, mostly per-request info logs; the only repeated error class is the file-watch one from F7.

**Evidence:** live `memory_stats(ai-raccoon|job-search-ai-assistant|ai-badger)` and `memory_watch_status(ai-raccoon)` (2026-08-06 ~12:2x UTC); `grep -c "ai-raccoon" ~/.hermes/logs/mcp-stderr.log` = 17138; error-class grep.

## Still open

- **access_count semantics:** `MemorySql.cs:225` increments it, but a row that a logged live probe ranked #1 on Aug 5 19:11 (`ai-badger/docs/changelog/0.78.0-ai-raccoon-memory-store.md`) still shows `access_count=0` — either that probe ran against a different data root or search hits do not bump access marks. Settling this needs a code read of the search path (SqliteMemoryStore.SearchAsync → memory access marking).
- **Why watch ingests stay pending:** the two pending rows came through the watcher, while `memory_write` embeds synchronously with `embedding.provider=local`. Whether the watch digest executor defers embedding by design or by bug is not read in code.
- **Pre-hook usage:** the grade log only starts Aug 5 21:11; organic search volume before that is unmeasured (the 292 accessed rows suggest more searching than the log shows, but the mapping is not proven).
- **Claude Code usage:** no `host=claude` lines — cannot distinguish "never searches" from "hook doesn't fire there".
- **Structure vectors absent:** whether the current ingest pipeline populates `structure_embedding` at all, or whether the live corpora simply predate Wave 6, is not checked in code.
- **Growth question:** with 5 organic writes and ~4 organic searches in the measured window, the data is too thin to project feature demand; the shared tier being empty means the promotion feature has no production evidence either way.
