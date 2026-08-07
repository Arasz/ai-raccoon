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

---

## Fix cycle (2026-08-06, issue #44) — measured before/after

Owner directive after v1: report the findings as an issue (filed as #44), then plan → review → implement → QA → manual check → v2. The plan (`docs/plans/memory-audit-fixes.md`) was reviewed by the architect persona (APPROVE-WITH-NITS; all findings folded in). Code fixes shipped as PR #47 (3 commits, TDD); data operations ran against the live bank.

### F12 — File-path watches now work; the mcp-tools.json watch no longer throws at Start [MEASURED]

`WatchEventSource` previously ran `new FileSystemWatcher(registeredPath)` — a file path throws `ArgumentException` (56 occurrences in the bridge log before the fix). File registrations now watch the parent directory filtered to the file name; sibling events are dropped; a rename-away surfaces as Deleted. 7 new unit tests cover start-on-file, missing-file-with-parent, create/change/delete/recreate, rename-away, rename-in, sibling isolation. **The running bridge server still executes the pre-fix build** (installed tool 1.0.7) — the log-error count stops growing only after a build with PR #47 is installed; the fix is proven by the unit tests and the suite, not yet by the live log.

**Evidence:** PR #47 commit 55bdc28; `WatchEventSourceTests` 17/17 pass; `grep -c "Watch event source error" ~/.hermes/logs/mcp-stderr.log` = 56 (pre-fix baseline, live build unchanged); `watch list` shows the file registration intact.

### F13 — Zero pending rows bank-wide; watch digests retry-embed [MEASURED]

Before: 2 pending rows (watcher ingests from 10:13, never embedded). Now: `embed_state` counts show **2,696 embedded / 0 pending** (embed backfill via `memory_embed_pending(ai-badger)` → `{processed: 2, pending: 0}`), and `WatchDigestExecutor` triggers a best-effort `EmbedPendingAsync` after every successful digest so a failed inline embed is retried instead of left pending (hash-skip and delete paths don't embed — covered by tests, including a tolerant-embed-failure test).

**Evidence:** `SELECT embed_state, COUNT(*) FROM entries GROUP BY 1` = 2696/0; PR #47 commit 9e5f5f6; `WatchDigestExecutorTests` 11/11 pass (incl. `Digest_EmbedFailure_IsTolerated_DigestStillCompletes`).

### F14 — jsaa corpus re-ingested; the provenance contract now holds [MEASURED]

Before: 762 rows, `source_file` NULL ×762, `section` NULL ×762, `agent_id` = structured path ×762 (pre-Wave-2 provenance). After the re-ingest (fixed script, reset = `project:job-search-ai-assistant` delete, pinned jsaa HEAD 9397bbef): **761 rows, 761 with `source_file`, 672 with `section`, 0 with `agent_id`**, all embedded. Chunk count 772 (global dedup → 761 rows, the documented contract). Hash map regenerated (`scripts/chunk-hash-map.json`, committed); live spot-checks all green (incl. the Cosmos partition-key query that missed in the rehearsal — ranked 3rd on the live bank). Rehearsal on a scratch root first: same shape (761 rows, 0 agent_id, all embedded) — the script is reproducible.

**Evidence:** live-bank SQL (`SELECT project_id, COUNT(*), COUNT(source_file), COUNT(section), COUNT(agent_id) FROM entries GROUP BY 1`); `/tmp/real-ingest.log` (exit 0, 104.6 s, all spot-checks ✓); `/tmp/rehearsal-ingest.log`; PR #47 commit 15b05d4 + 16253d7.

### F15 — Ghost config and test residue removed [MEASURED]

Before: `watch.enabled.CLAUDE.md=true` (shell-glob ghost, no registration), `watch.enabled.tool-test-20260806=false`, 3 tool-test `watch_files` rows, 2 closed tool-test workspaces. After: settings table has no `CLAUDE.md`/`tool-test` rows (`ai-raccoon watch remove` ×2 → "removed watch config for …"), `watch_files` = 112 (tool-test 0), `workspaces` = 3 (the pre-audit `acme` feature-test rows, left by design). `watch list` no longer shows the ghost targets.

**Evidence:** settings/watches/watch_files/workspaces SQL before and after; `ai-raccoon watch remove` CLI output; the CLI contract (ConfigCommands.cs:214-228 — removes enabled/scope/concurrency rows only, which is why the SQL cleanup was separate).

### F16 — QA gate: full suite 1,142 passed / 0 failed / 43 skipped [MEASURED]

The complete suite (incl. the 11 new/extended watch tests) ran in the PR worktree: `Passed! - Failed: 0, Passed: 1142, Skipped: 43`. Script gates: `grep -rn memory_configure scripts/` = 0 hits; `py_compile` clean; `--dry-run` enumerates 198 files at the re-pinned commit.

**Evidence:** `/tmp/full-suite.log` tail; static gate output; dry-run output.

## Adoption levers (f: 2026-08-06) — how to grow shared-memory usage and organic search

The measured window shows 5 organic writes and ~4 organic searches; the 2 graded organic searches scored 4/5 and 5/5 — when agents search, they get value. The gap is therefore trigger/habit, not quality. Levers, in priority order [INFERRED — reasoned from the measured usage data and the skill/hook mechanics]:

1. **One-call memory brief** — a session-start surface (or skill ritual) returning top recent + project-relevant entries in a single call, removing the 2–3 call cost of "search first" [INFERRED].
2. **Seed the shared tier** — promote the genuinely cross-project organic facts that exist (the 1.0.4 multi-RID tool-fix fact, the watch-tools MCP regression note) so a `scope=shared` search returns value from any project; promotion only pays once consumption exists (cold-start/network effect) [INFERRED].
3. **Memory into the default loop** — a "check project memory" step in the task skill's Phase 0 and the mcp-index per-turn hook, instead of optional `.hermes.md` prose [INFERRED — the scaffolding exists (jsaa `.mcp.json` + global Hermes config), usage doesn't].
4. **Keep the retrieval ceiling high** — the F6/F8/F14 fixes (pending rows, provenance, script) remove friction that would otherwise cap hit rate as usage grows [MEASURED — F13/F14].
5. **KPI** — search count and share count per window are the growth metrics; the grade hook stays the quality loop (2/11 graded in v1; the ask-after-search loop is the feedback mechanism) [MEASURED — quality-log parse].

## Still open (updated)

- The live watch-error count (F7/F12) can only be re-measured after a build containing PR #47 is installed as the bridge tool — the 56-error baseline is the pre-fix number.
- `structure_embedding`/`heading_path` remain 0 bank-wide: the tool surface (`MemoryWriteRequest`) carries no heading field — recorded as an explicit out-of-scope decision (review F8); a follow-up task would extend the write contract.
- `access_count` semantics still not fully settled (MemorySql.cs:225 increments it, but the wp7 probe row showed 0 — see v1 Still open).
- The `acme` workspaces predate the audit (feature-test artifacts) — left untouched; owner can remove.
- Adoption levers 1–3 are proposals, not shipped changes.
