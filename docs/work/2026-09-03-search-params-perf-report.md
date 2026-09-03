# Search-params + live-bank probe — 2026-09-03

Reduced-scope manual checklist run (`docs/work/checklist/2026-09-03-search-params-perf-probe.json`).
Live bank `~/.ai-raccoon` touched **read-only** throughout, except the three explicitly
requested settings resets. No server was started or cycled; the owner's server was left alone.

## Parameter acknowledgement (read this first)

Every measurement below was gathered while the bank carried **custom** retrieval settings:

| setting | was | default | now |
|---|---|---|---|
| `retrieval.rrfK` | 30 | 60 | 60 ✅ reset |
| `retrieval.ftsWeight` | 3 | 1 | 1 ✅ reset |
| `retrieval.vectorWeight` | 2 | 1 | 1 ✅ reset |
| `retrieval.structureAlpha` | 0.5 (row, equals default) | 0.5 | left as-is |
| fusion no-regression guard | off (row, equals default) | off | left as-is |

Reset commands (verbatim): `retrieval rrfK set to 60`, `retrieval fts-weight set to 1`,
`retrieval vector-weight set to 1` — verified by `settings retrieval show-all`.
Caveat: the metrics window (Aug 16 → Sep 3) spans the pre- and post-custom periods, so the
phase timings are **not** purely "performance under the custom params" — they are all-time
aggregates. The settings rows themselves changed only today.

## Live server identity

- `ai-raccoon serve --restart`, PID 74534, port 7721 (owner's — never touched).
- Global tool `1.37.0+55f71beb` == derived tree facts (`version=1.37.0`, 29 tools, 2 prompts).
- Bank `memory.db` 1.29 GB. Pending-embedding rows: **0**.

## Performance (metrics table, 83,510 rows, Aug 16 → Sep 3; last 24h: 38 searches, avg 3.6s)

| phase | n | p50 | p95 | max |
|---|---|---|---|---|
| search.total | 1748 | 1.5 s | 20.0 s | 144 s |
| search.fts | 1782 | 527 ms | 15.7 s | 133 s |
| search.vector | 1782 | 471 ms | 3.6 s | 15.6 s |
| search.embed | 1748 | 32 ms | 978 ms | 9.7 s |
| search.fusion | 1782 | 0.8 ms | 9.5 ms | 408 ms |
| search.snippets | 1782 | — | — | 5.4 s (avg 86 ms) |

FTS dominates end-to-end latency and carries the long tail. Fusion/embed are noise by comparison.

## Logs

`quiet.log` (61 MB, current): recent lines are Debug proxy traffic ending in
`backend live at http://127.0.0.1:7721/mcp`. The 2262 historical error/fail lines are
restart-cycle artifacts (Aug 11 startup `TaskCanceledException`, Aug 13 `Connection refused`
during a restart, Aug 28 shutdown-checkpoint warning). **No active error storm.**

## Promoted memories

- Shared (sweep-exempt) entries: **237**.
- Propose queue: **1000 rows — exactly at `extract.queue-capacity.global` (1000).** ⚠️ Flag:
  the queue is full; top contributors are capped at ~156–157 each (deepseek-harness 157,
  jsaa 157, ai-badger 156, ai-raccoon 156, arasz-home-page 156). Whether proposers shed load
  past the cap was not verified — worth a follow-up before trusting propose-tier completeness.
- Promotion discards: **967**.

## Projects in use

Registered (`projects` table, 12): ai-badger ×3 guids, 2 unnamed guids, ai-sheepdog,
memory-roundtrip-test, job-search-ai-assistant ×2, aib, pi-badger-integration,
pbi-badger-integration. Newest: `pbi-badger-integration` + guid `01a062f4…` (jsaa), both Sep 2.

Entry volume by `project_id` (scope=project, 48,636): jsaa 13,070 · deepseek-harness 12,890 ·
ai-raccoon 9,569 · ai-badger 7,465 · hermes-default 2,666 · arasz-home-page 1,719 ·
vue-kanban 1,184 · long tail (dotnet-ignore 25, job-search-ai-assistant 22, manual-sweep 18,
interview-tasks 1, three guid/test ids ≤2).

New-projectId (1.37.0 cwd-default) resolver surface: 9 `ingest.scope.*` rows
(ai-badger, ai-raccoon, arasz-home-page, deepseek-harness, dotnet-ignore, global,
interview-tasks, jsaa, vue-kanban) + 10 live `watches`. 48,901 entries carry no context label
(bulk-ingested file content); the rest sit under small named contexts (largest: `project` 23,
`job-search-ai-assistant` 20). 28 rows are workspace-scoped; 19 workspaces still Active
(ai-raccoon lanes, jsaa feedback lanes, ai-badger p-lanes), remainder Closed/Discarded.

## Retrieval-quality signal: stale — cause found, then FIXED by #596 (addenda)

[2026-09-03 am] The above blamed agent behavior. The actual mechanism, confirmed in code and
 data: PR #580 (merged 2026-08-24, the exact day the rows stop) flipped `memory_search`'s
 default `kind` to `both`, and `SearchDispatcher` recorded `search_quality` rows only for
 `kind=memory` (deliberate — code-adjacent query hashes sync off-machine).

[2026-09-03 pm] SUPERSEDED: origin/main 356afe95 (#596, ADR-0094) makes search_quality record
 EVERY kind with always-emitted correlationIds + a kind column. The signal flows again; the
 am diagnosis is now the history of a closed defect, not a live one. Session ids remain
 never-passed (1534/1534 NULL at measurement time) — attribution still blind.

The above blamed agent behavior. The actual mechanism, confirmed in code and data: PR #580
(merged 2026-08-24, the exact day the rows stop) flipped `memory_search`'s default `kind` to
`both`, and `SearchDispatcher` records `search_quality` rows **only for `kind=memory`**
(deliberate — code-adjacent query hashes sync off-machine; correlationIds are withheld for
non-memory kinds so grade/follow-through cannot silently no-op). hermes-default alone ran
307 searches since Aug 25 with 0 rows. The signal is not broken; it was designed away from
the default path. Remedy candidates, none taken: record content-free quality rows for
kind=both (counts/scopes, no query hash), revert the default, or accept the blindness.
Session ids were never passed by any caller (1534/1534 NULL) — even the surviving rows
cannot be attributed to a session.

Original note (superseded cause, still-true facts): `search_quality`: 1534 rows, avg 6.9 results/search — but the newest row is **Aug 24** (0 in the
last 7 days) and only 1 follow-through ever recorded. Grades skew negative (140× grade 2,
18× grade 1 vs 18× grade 5). Any search-tuning decision taken today rests on a dead signal;
re-arm grading/follow-through before trusting it.

## Counts

9 pass · 0 fail · 0 skipped · 0 substituted. `accepted` left null throughout for the owner.
