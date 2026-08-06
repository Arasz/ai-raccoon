# Integration Review: latest code changes → 1.0.9

**Reviewer:** Hermes agent (task `integration-review-1-0-8`)
**Date:** 2026-08-06
**Base → HEAD:** `v1.0.7` → `368607a` (main, 35 commits)
**Scope:** integration review of everything merged since 1.0.7; docs refresh; version 1.0.9 (package id migrated to `ai-raccoon`); observability audit; pre-merge tool verification
**Gates:** `dotnet build` (0 warnings) · full `dotnet test` suite · manual fresh-install protocol vs published 1.0.8/1.0.9

---

## Method

Review based on the git range `v1.0.7..main` (35 commits), a line-by-line read of
the production diff (observability, tools, CLI extraction), the new tests, and
live verification: full suite in the task worktree, the packaged tool installed
from nuget.org, and grep-level surface checks. The architect's plan
(deleg_3789feac) decomposed the work; the dotnet-engineer (deleg_b79e3436)
implemented the two code packages TDD.

## What was reviewed (commits since v1.0.7, grouped)

| Group | Commits | Verdict |
|---|---|---|
| Observability refactor | `00b0824` (ToolExecutionActivity extraction), `95a18a8` (tests) | Reviewed + closed the ADR gap (below) |
| Tools: `memory_list` JSON object, `workspace_discard {discarded}` | `212b29e`, `f944825` | Response shapes now match the documented contract; docs already synced |
| CLI extraction wave (PRs C–F) | `6ac9144` (Settings), `ce67b54` (Sync), `00f3e5c` (Watch), `fbb4d45` (test helper) | Consistent with the static-class invariant: injectable components with interfaces; `ConfigCommands` stays the sanctioned static dispatcher |
| Const/naming normalization | `970aebc` (TN_→Tn), `29c832e`/`4574538` (inventory tests) | Mechanical, tests updated |
| Watch + ingest audit fixes | `50aa12f` (#47) | Found the corpus regression (below) |
| BDD native-memory scenarios | `6bd8d1f` (#49) | Implemented previously-@ignore scenarios + tombstone/vec0 fixes |
| Version | `0c2956c` (#48) → `d7bd782` (#53) | 1.0.8 released + published; #53 migrated NuGet id to `ai-raccoon` and bumped 1.0.9 |
| Docs | `2b1eb78` (#37), `e360676` (#38), `9532466`, `97affa6`, `0089e71`, `8696d4b`, `8bb386f`, `368607a`, … | Compacted README, tools-test report, adoption plan |

## Findings by risk tier

### HIGH — corpus regression on main since PR #47 (pre-existing, not from this task)

Two `RetrievalBaselineTests.CorpusIntegrity_*` facts fail on `main` itself:
`HashMapMatchesDatabaseCounts` (map = 761 distinct hashes, committed
`jsaa-memory.db` = 752 rows) and `SourceFileAndSectionPopulated` (the stale DB
lacks the source-identity columns the regenerated map expects). PR #47
regenerated `scripts/chunk-hash-map.json` at the re-pinned jsaa commit
(9397bbef, 772 keys → 761 distinct hashes) but never committed the regenerated
corpus DB — the "real re-ingest" (issue #44, P5) produced 761 rows on a live
data root, only the map was committed. Verified green before #47 (752/752) and
red after. CI does not catch it: `build.yml` runs only `Speed=Fast`, and these
tests are Integration/Slow.

**Resolution:** recorded here; belongs to the retrieval workstream (#44 P5
deliverable — regenerate and commit the corpus DB). Not fixed in this PR to
keep the change scoped (one PR per task). The full-suite gate below reports it
as the single known failure, identical on the base.

### HIGH — `memory_share_extract` crashed on every call since PR #50 (found + fixed in this PR)

The parity test's first wire-level call of `memory_share_extract` (propose
mode) threw an unhandled `InvalidOperationException`:
`ExtractionRow` (a private record — Dapper uses strict constructor matching for
records) declared `int AccessCount` + `int? TtlDays`, but SQLite returns
`INTEGER` columns as `Int64`; materialization failed before any candidate was
returned. The tool was dead on arrival since #50 merged — CI never caught it
because `build.yml` runs only `Speed=Fast` and no existing test called the tool
against a real store. Fixed in this PR: the row record now uses `long`/`long?`
with checked casts in `ToCandidate`; the parity round trip pins it green.

### MEDIUM — 4 tools could exit *unrecorded* on McpException paths (code-review finding 1, fixed in this PR)

`WatchTools` ×3 and `MemoryTools.Sync` wrapped their bodies in
`catch (Exception ex) when (ex is not McpException)` so the outer filter let
`McpException` from `RequireProjectId` (invalid-params) and
`MemoryAccessGuard.EnsureAsync` (access-denied) escape with **no
`RecordError`, no `RecordInvocation`** — the span ended `Unset` with no
`result`/`error_type` tag and no metric, violating the ADR-0002 "every call
emits" contract. The other 15 tools used plain `catch` and recorded everything.
Fixed: `RecordInvocation`/`RecordError` are now idempotent (first record wins,
so the inner typed catches' rethrow can't double-count), and the filters are
dropped to plain `catch (Exception ex) { activity.RecordError(ex); throw; }`.
Pinned by 5 new facts in `McpExceptionPathInstrumentationTests` (RED first).

### MEDIUM — ADR-0002 stale vs code (fixed in this PR)

`docs/adr/0002-opentelemetry-observability.md` drifted from the implementation
in four places: (1) "17-tool server" → now 20; (2) instrumentation described as
"inlined 3–5 lines per method" → now the `ToolExecutionActivity` helper (the
ADR's own Future-evolution #4, done); (3) the ActivitySource tag table omitted
`error_type`; (4) it promised `SetStatus(Ok)` + a `result` activity tag on
success that **no version of the code ever set** — only `SetStatus(Error)` on
failure. Verified against the v1.0.7 source: pre-existing drift, not a refactor
regression. Decision (architect D1): the ADR is the accepted spec, so align
code to it (TDD) and fix the ADR facts in place.

### LOW — manual-test script pin drift (fixed in this PR)

`scripts/manual-fresh-install-test.py` defaulted to 1.0.6 while the release was
at 1.0.7/1.0.8 — a stale default guarantees the post-publish run tests the
wrong version unless the human remembers the env var. Fixed: default + docstring
→ 1.0.9 (id `ai-raccoon`), plus an `AI_RACCOON_SOURCE=local` mode for pre-publish dress
rehearsals against `.nupkg-local`.

## Changes in this PR

1. **fix(observability): success spans carry Ok status + result tag**
   (`ToolExecutionActivity` + `ToolExecutionActivityTests`): `RecordInvocation`
   now sets `ActivityStatusCode.Ok` + `result=success`; `RecordError` adds
   `result=error`. TDD — the two new facts were RED against the old behavior
   before the change (engineer captured the failure). All tools flow through
   this one helper, so the whole surface emits the ADR contract.
2. **test(e2e): 20/20 tool-surface parity** (`McpServerToolSurfaceE2ETests`):
   `tools/list` surfaces exactly the 20 documented tools (incl.
   `memory_share_extract` added by #50), and every tool not already
   round-tripped by `McpServerE2ETests` (memory_list, memory_delete,
   memory_delete_context, memory_ingest_file, memory_ingest_directory,
   memory_sweep, memory_share_extract, memory_watch_add/status/remove) answers
   a minimal call over the wire with shape assertions. Replaces the one-off
   2026-08-06 tools-test report as a permanent regression gate — and caught the
   `memory_share_extract` materialization bug on its first run.
3. **fix(observability): every tool call recorded, including McpException paths**
   — idempotent `RecordInvocation`/`RecordError` + dropped
   `when (ex is not McpException)` filters in WatchTools ×3 and
   MemoryTools.Sync; 5 new facts pin the previously-unrecorded escape paths.
4. **fix(store): `ExtractionRow` SQLite INTEGER → long** — `memory_share_extract`
   now works over the wire (was dead since #50).
5. **docs(adr): refresh 0002 facts** — 20 tools, helper pattern, `error_type`
   in the tag table, Future-evolution #4 marked done, stray fragment removed.
   Decision substance (BCL-only Meter/ActivitySource, instrument names, DI
   singleton) untouched.
6. **chore(scripts): fresh-install test pin 1.0.9 (id `ai-raccoon`) + local-source mode.**

## Observability verification (user ask: "check if all metrics and traces are emitted")

- **Coverage:** 16/16 MemoryTools + 3/3 WatchTools route through
  `ToolExecutionActivity` (grep: 16 + 3 sites). No tool on the old inline path.
- **Metrics:** `ToolCallMetrics` singleton (`Dependencies.cs:68`); counter
  `ai_raccoon_tool_invocations` + histogram `ai_raccoon_tool_duration_ms` on
  meter `AiRaccoon.MemoryTools`, tagged `tool`/`result`/`error_type` — emitted
  by `RecordInvocation` (success) and `RecordError` (error).
- **Traces:** `ActivitySource("AiRaccoon.MemoryTools")` starts one activity per
  tool call with `tool` + `project_id`; success now marked `Ok` + `result`
  tag, error marked `Error` + `error_type` + `result` (ADR contract restored).
- **Tests:** `ToolExecutionActivityTests` (6 facts incl. the 2 new),
  `MemoryToolsInstrumentationTests` (5), `ToolCallMetricsTests` — all green.
- **Live:** `dotnet-counters`/`dotnet-trace` commands in README + ADR verified
  correct against the meter/source names.

## Tool verification (user ask: "manual tests before merge")

- **Published-package gate (post-publish):** ran
  `scripts/manual-fresh-install-test.py` against nuget.org →
  **RESULT: ALL GREEN** — fresh install first try, layout + model/vocab sha256
  pinned, `--version` prints the pinned version, MCP stdio round trip (initialize 664ms,
  memory_write → memory_search hash match at rank 1, memory_stats entries=1
  pending=0 proving the vec0 path), dual-instance, graceful shutdown,
  zero-config probe. Evidence: full protocol log captured during the run.
- **Pre-merge gate:** E2E parity test (20/20 over the wire) + full suite below.
  `verify-tool-package.sh` remains the pre-publish pack gate.

## Gates

- `dotnet build`: 0 warnings / 0 errors
- Full `dotnet test`: **1206 passed / 5 failed / 4 skipped** — all 5 failures
  are the pre-existing #47 corpus regression (verified identical on the base
  commit: `CorpusIntegrity_HashMapMatchesDatabaseCounts`,
  `CorpusIntegrity_SourceFileAndSectionPopulated`, `SourceIdentityTests`,
  `SectionTargetedRetrievalTests`, `QueryConstructionTests`,
  `SourceAffinitySweepTests`, `RrfParameterSweepTests` — the last five all read
  the stale corpus DB). My branch adds 0 failures.
- `scripts/manual-fresh-install-test.py` (published 1.0.8 then 1.0.9, nuget.org): ALL GREEN
- Grep gates: `SetStatus(ActivityStatusCode.Ok)` ×1; `result` tag asserted both
  paths; 20 tool names in the parity test; 17+3 helper sites

## Post-merge checklist (owner)

- [ ] Re-run the fresh-install protocol after the next publish if the version
      moves again (`AI_RACCOON_VERSION=<new>`)
- [ ] Issue #44 P5 follow-up: regenerate + commit `jsaa-memory.db` to clear the
      corpus gate (pre-existing red on main)
