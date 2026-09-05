# Research: project-ids repair fix state

**Date:** 2026-09-05
**Question:** What is the fix state of the `ai-raccoon repair project-ids` sequence in the pasted transcript, and what remains to be done?

## Findings

### F1 — The bank re-derives to one actionable fold, zero drops, zero retires [MEASURED]

A fresh dry run on this machine reproduces the transcript's end state exactly: 43 ids censused — 1 fold (`b0e32c16…` → `jsaa`), 0 drop, 0 retire, 1 unresolved (`50a8bb05…`), 3 pinned, 38 needing nothing. Nothing in the transcript's final dry run has drifted since; this is the live state, not a stale paste.

**Evidence:** `ai-raccoon repair project-ids --map /Users/arasz/.ai-raccoon/project-id-map.template.json` run on this machine (macOS, local bank `~/.ai-raccoon/memory.db`, server census path), single run, exit 0, output byte-identical in counts to the transcript's last two dry runs.

### F2 — The `--apply` pass moved 10 rows across 7 folds with totals flat, then settled fold-free [READ]

The pasted transcript's `--apply` block shows pass 1/10 deriving 7 folds and reaping 10 moved rows with census totals unchanged (53301 → 53301, a re-key, not a delete), closing as "attention needed: 0 fold, 0 drop, 0 retire, 1 unresolved, 3 pinned". The six entry-folds from the first dry run are gone and stayed gone.

**Evidence:** operator-pasted CLI transcript in the 2026-09-05 request, `--apply` block lines ("pass 1/10 — derived 7 fold…", "reaped: moved 10 row(s); census totals 53301 → 53301 entries", "summary — attention needed: 0 fold…").

### F3 — The remaining fold is post-apply content, not a failed fold [INFERRED]

Reasoning from F2 (the apply settled at 0 folds) and F4 (the loser's only foldable row is timestamped 21:34:57 UTC, after the apply sequence): session `01a0737e` wrote a fresh search-quality row under the already-folded loser id, re-creating an actionable fold. The repair worked; a writer re-soiled the id.

### F4 — The `b0e32c16` fold is scheduled by a single search-quality row [MEASURED]

Direct bank reads show the loser owns zero entries, zero code entries, zero queued, zero watches, zero discards and zero tombstones — but one `search_quality` row ("offer source ingest health…", kind `both`, 2026-09-05 21:34:57 UTC, session `01a0737e`) plus 14 telemetry metrics rows. The CLI's one-line "owns 0 entries (0 NULL-context…), 0 queued" hides exactly this row. The owning session's bus traffic is all jsaa offer-source lane work, matching the planned winner.

**Evidence:** `sqlite3 ~/.ai-raccoon/memory.db` on this machine — per-table `count(*)` over `entries`, `code_entries`, `promotion_queue`, `watches`, `promotion_discards`, `sync_tombstones`, `search_quality`, `metrics` for the loser id, plus the `search_quality` row read (query text, kind, timestamp, session prefix) and bus row check for sender `01a0737e`.

### F5 — One quality row suffices to schedule a fold, by design [READ]

`OwnsMoveableContent` counts `QualityRows` alongside entries, code, queue, discards, watches and tombstones, and its doc comment states it is exactly the applier's executable predicate "so a planned fold can never execute as zero moves". Telemetry rides along but never schedules; shared rows and workspace scratch never move. A zero-entry id with one quality row is therefore a legitimate fold, not planner noise.

**Evidence:** `src/AiRaccoon.Core/Projects/ProjectIdsFoldPlan.cs:223-243` (`OwnsMoveableContent`, `QualityRows` at line 230, predicate comment at 216-222); telemetry/shared/workspace exclusions at lines 244-276 (`PinUnmoveable`).

### F6 — `50a8bb05` is unattributable content from one router-fallback session [MEASURED]

The unresolved id owns zero entries and zero of every other moveable surface, but 29 metrics rows and two `search_quality` rows, both from session `01a07260` ("router fallback free model provider extension", 18:14 UTC; "router-fallback provider chain order…", 18:50 UTC). It is absent from the `projects` table (unregistered) and the alias map contains zero references to it, which is precisely the shape the planner leaves for a human.

**Evidence:** `sqlite3 ~/.ai-raccoon/memory.db` counts and row reads as in F4 for id `50a8bb05…` (metrics names, quality query texts, timestamps, session prefix); `grep -c "50a8bb05" /Users/arasz/.ai-raccoon/project-id-map.template.json` → 0; repair output line "1 id(s) match no known id — left alone for a human to attribute".

### F7 — `50a8bb05` most likely belongs to pi-badger-integration [INFERRED]

Reasoning from F6 (both searches are router-fallback queries from session `01a07260`) plus that session's own bus announcement, `[pbi-router-failure-free-model-fallback] merged: router-fallback extension to main`. The suggested fix is one alias-map line, `50a8bb05…` → `pi-badger-integration`, confirmed with the operator before applying.

### F8 — The three pins are convergence-neutral waits, not pending repairs [READ]

Telemetry-only (`024ef989…`, regenerable derived data the repair never moves), shared-only (`aib`, one shared-scope row that is cross-project content the repair never folds) and open-workspaces (`job-search-ai-assistant`, 14 open workspaces whose live scratch never moves across projects) each name their blocker in the reason line. No `--apply` pass will or should touch them; they clear when their underlying state clears.

**Evidence:** `src/AiRaccoon.Core/Projects/ProjectIdsFoldPlan.cs:34-44` (pin bucket constants) and `244-276` (`PinUnmoveable`/`PinTelemetryOnly` reasons); live reason lines in the F1 dry-run output.

## Still open

- ~~Is the F7 attribution (`50a8bb05` → `pi-badger-integration`) confirmed?~~ Settled 2026-09-05: operator confirmed; alias added to `/Users/arasz/.ai-raccoon/project-id-map.template.json`, dry re-run plans the fold (see F9).
- Will one `--apply` clear the `b0e32c16` fold for good, or will active jsaa sessions keep writing quality rows under the loser id? What settles it: run `--apply`, re-census, and check which sessions still resolve the loser id as their project.
- Does the server's P3 write-refusal cover future quality/metrics writes under folded losers, or entries only? Settled in principle by F10 (legacy row-owners are accepted, not refused) — the residual is whether the applier also re-keys quality rows on fold, which the `--apply` re-census will show.

## Follow-up findings (2026-09-05, post-record)

### F9 — Attributing `50a8bb05` converts it into a planned fold; unresolved hits zero [MEASURED]

After adding `{"Alias": "50a8bb05-4ba6-4002-94be-f8988ecc3b58", "Canonical": "pi-badger-integration"}` to the map, the dry run re-derives to 44 censused — 2 fold (`50a8bb05` → `pi-badger-integration`, `b0e32c16` → `jsaa`), 0 drop, 0 retire, 0 unresolved, 4 pinned. No `--apply` run yet; the folds are planned, not executed.

**Evidence:** same `repair project-ids --map …` dry-run command as F1, run after the one-line map edit; closing line "repair needed: 2 fold, 0 drop, 0 retire, 0 unresolved, 4 pinned".

### F10 — Writes under unknown ids with existing rows are accepted by design, once warned [READ]

`ProjectRegistrationGuard` refuses only unregistered ids that own *no* rows (post-migration), plus retired and drop-listed ids. An unregistered id the bank already holds rows for — entries, quality, even metrics — is accepted as a legacy row-owner with a one-time warning ("it works because the bank already holds rows for it"). That is why the `b0e32c16` quality row at 21:34:57 UTC landed after its entries were folded away, and why folds can recur while writers still carry old spellings: acceptance, not a guard bug.

**Evidence:** `src/AiRaccoon/Projects/ProjectRegistrationGuard.cs:8-15` (ADR-0089 decision 3, P3 gating) and `39-70` (no-rows → refuse; has-rows → warn-once accept); `src/AiRaccoon.Core/Projects/UnregisteredProjectException.cs:3-5`.

### F11 — This investigation left one telemetry footprint that pinned itself [MEASURED]

The re-run census grew 43 → 44 ids because the research itself wrote one `memory_search` histogram row (2026-09-05 21:42:30 UTC) under the literal guid spelling `cfe47dab…` passed as the search project id. The map attributes it to `ai-raccoon`; owning only telemetry, it pins `pinned-telemetry-only`. Convergence-neutral by design — no action, just disclosure that the observer moved the needle.

**Evidence:** `sqlite3 ~/.ai-raccoon/memory.db` read of the single `metrics` row for `cfe47dab…` (name `memory_search`, 21:42:30 UTC — this session's first search); F9 census line showing the new pin.

*Correction (same day, see F12): the effect stands but the mechanism stated above is imprecise — the row was not written "under the spelling passed" by the search path. The 13 search-phase rows went to the canonical `ai-raccoon`; only the tool-level row kept the raw spelling, via the pre-gate telemetry filter.*

## Follow-up findings, round 2 (2026-09-05)

### F12 — The stray row came from the pre-gate telemetry filter, which records the raw argument [READ]

Every tool call passes through `ToolTelemetry.Filter` *before* the tool body — and therefore before `ToolGate.RequireAsync` canonicalizes and folds the id. The filter projects the span/counter id from the raw `projectId` argument ("the span keeps what the caller sent") and, on success, writes one bank `metrics` row named after the tool with that raw id. The tool body itself recorded its 13 search-phase rows under the canonical `ai-raccoon` (the write-time choke worked). So one call produced 13 rows under the winner and 1 row under the raw loser spelling, and that single raw row is the whole `cfe47dab` pin. Whether the bank measurement should fold through `Default` at record time is a design question, not a bug: the raw span id is intentional for tracing.

**Evidence:** `src/AiRaccoon/Observability/ToolTelemetry.cs:59-80` (filter runs before `next`, `ProjectFor` keeps raw `projectId`); `src/AiRaccoon/Observability/ToolExecutionActivity.cs:47-52,70-81,104-106` (raw `_metricProjectId` recorded to bank); bank read — 13 phase rows under `ai-raccoon`, 1 `memory_search` row under the guid, same 21:42–21:44 window.

### F13 — A row added under a duplicate is caught three times over [READ]

First, the applier is predicate-based, not list-based: every fold step is `UPDATE <table> SET project_id=@winner WHERE project_id=@loser` executed at apply time — entries, code, queue, discards, quality, metrics, noise, watches, settings keys, projects row, tombstones — so rows written between derive and apply match the predicate and move. Second, once the alias is durable, the write-time choke folds loser→winner inside `ToolGate.RequireAsync` on every subsequent tool call (plus the watch service and digest executor), so new writes transparently land under the winner without any repair run. Third, the CLI loop re-derives after every pass and any future run derives from scratch, so anything both layers miss reappears as a fresh fold — exactly the observed `b0e32c16` lifecycle.

**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/ProjectIdsRepair.cs:123,145,177,237,284-299,373,408-412,446,510,608-620` (per-surface `UPDATE … WHERE project_id=@loser`); `src/AiRaccoon/Tools/ToolGate.cs:64-77` (post-migration fold through `Default` + retired refusal); `src/AiRaccoon/Setup/Cli/Commands/ProjectIdsRepairCommands.cs:268-330` (re-derive per pass).

### F14 — End state is pinned-only: both folds executed, zero unresolved [MEASURED]

At 21:52:10 UTC the edited map (now 11 aliases) was applied by a later `--apply` — the durable table holds `50a8bb05→pi-badger-integration` with that stamp — and the fold job moved the rows: 0 quality rows remain under either loser (`pi-badger-integration` now holds 9, `jsaa` 163). Live dry run: 42 censused, 0 fold, 0 drop, 0 retire, 0 unresolved, 4 pinned, verdict `pinned-only, P3 armed (11 alias, 26 dropped)`. The repair is finished; only the convergence-neutral waits remain.

**Evidence:** same dry-run command as F1 (closing line quoted verbatim); `project_id_aliases` row read (alias, winner, `applied_at` 21:52:10); per-id `search_quality` counts before/after (2→0 loser / 9 winner; 1→0 loser / 163 winner).

### F15 — The raw-spelled tool row is invisible to the canonical project's performance report [READ]

`memory_performance` canonicalizes the queried id through the gate and then filters with an exact match (`WHERE project_id = @ProjectId`). Phase series correlate: they are recorded under the canonical id. The one tool-level row per call keeps the raw spelling, so it drops out of the canonical project's report — per-tool series undercount by exactly the calls made with non-canonical spellings, while phase series stay complete. Candidate fixes, not attempted: fold at record time in `ToolExecutionActivity` via `Default.Fold`, or expand the report query to the alias family.

**Evidence:** `src/AiRaccoon/Tools/PerformanceTools.cs:26-33` (gate canonicalize, exact-match report); `src/AiRaccoon.Infrastructure/Metrics/MetricsReportService.cs:22-27` (`WHERE project_id = @ProjectId`); bank read — the orphan `memory_search` row under the guid vs 13 phase rows under `ai-raccoon`.

### F16 — Metrics are not memories [READ]

`metrics` is a separate, machine-local observability table: never embedded, never FTS-indexed, never searched, promoted, swept, or synced (stripped from pushed snapshots per ADR-0098, like `search_quality`). Its only reader is `memory_performance`; its only writer-side companions are the phase/tool recorders. Old rows die by the `metrics-retention` maintenance job (2-hour cadence, retention-days setting), not by `memory_sweep`. Folds carry metrics along (`FoldTelemetryAsync`) but metrics never schedule a fold and never become entries.

**Evidence:** `src/AiRaccoon.Infrastructure/Maintenance/MaintenanceJobs.cs:130-164` (retention job, `DELETE FROM metrics WHERE recorded_at < @cutoff`); `src/AiRaccoon.Infrastructure/Sqlite/ProjectIdsRepair.cs:398-412` (telemetry rides the fold); `MemoryTools.cs:519-544` comment (metrics never leave the machine).

### F17 — Option 1 implemented: bank row folds at write time; ride-along kept [MEASURED]

Decision per operator: option 1 only. `ToolTelemetry.RecordAsync` gained an optional `IProjectIdsMigrationGate` (resolved from request services in `Filter`; existing direct callers such as `ToolCallRecorder` keep compiling and keep raw behavior). The bank id is `Default.Fold(raw)` once migrated, raw otherwise — fail-open on missing gate, failed marker read, or pre-migration bank — with a one-way latch skipping the marker query after the first observed true. `ToolExecutionActivity` gained a trailing `bankProjectId`: span and OTel counter stay raw (minimal blast radius, dashboards untouched); only the bank measurement folds; an explicit refused sentinel still wins on the error path. Pinned metrics in the winner: kept as-is — the ride-along is what lets a loser row vanish entirely (leaving metrics would mint a permanent telemetry pin per folded id; deleting them would lose performance history for zero gain since retention ages them anyway).

**Evidence:** `src/AiRaccoon/Observability/ToolTelemetry.cs` (latch + `BankProjectIdAsync` + wiring), `src/AiRaccoon/Observability/ToolExecutionActivity.cs` (`_bankProjectId`); tests `ToolExecutionActivityTests` (+4) and new `ToolTelemetryBankFoldTests` (+4, in `ProjectIdAliasDefaultCollection` with replace/reset pairing per the collection gate); RED witnessed by neutering both `Fold` call sites — exactly `MigratedGate_FoldsBankMeasurementToWinner_SpanAndCounterKeepRaw` and `MigratedLatch_SkipsTheGateOnTheSecondCall` failed on intended assertions; GREEN after restore — full fast suite `Speed=Fast&Performance!=Benchmark`: 3761 total, 3760 passed, 1 pre-existing env-gated skip, stable across two consecutive runs.
