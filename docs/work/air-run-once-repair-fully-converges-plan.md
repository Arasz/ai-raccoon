# FIX PLAN — air-run-once-repair-fully-converges (plan only, no code)

## 0. Context map (mandatory gate, before any implementer touches files)

**Primary files** (behavior changes here):
- `src/AiRaccoon.Core/Projects/ProjectIdsFoldPlan.cs` — planner predicates, new pinned buckets (D1/D2/D3)
- `src/AiRaccoon.Core/Projects/ProjectIdCensusReport.cs` — row model, `AttachmentCount`, telemetry exclusion (D3)
- `src/AiRaccoon.Core/Projects/ProjectIdAliasMap.cs` — durable-map backing, `Default` source (D4)
- `src/AiRaccoon.Infrastructure/Sqlite/ProjectIdsRepair.cs` — broadened fold predicate, dedup, vec invalidation (D1)
- `src/AiRaccoon.Infrastructure/Sqlite/ProjectIdCensus.cs` — SELECT-only, any new pinned counters (D2/D3)
- `src/AiRaccoon.Infrastructure/Maintenance/ProjectIdsRepairJob.cs` — re-derive, result reporting, durable-map persist (D4/D5)
- `src/AiRaccoon/Setup/Cli/Commands/ProjectIdsRepairCommands.cs` — run-until-fixed loop, receipts, closing summary (D5/D6)

**Secondary files** (consumers / choke points, mostly wiring):
- `src/AiRaccoon.Infrastructure/Sqlite/ProjectRows.cs` — single home of scope literals (extend, don't duplicate; `ProjectRowsSingleDefinitionTests` guards this)
- ToolGate write path, watch boundaries, `IngestScopeKeys`/`WatchConfigKeys`/`AccessModePolicy` key helpers (already call `Default.Fold` — D4 arms them), Sync pull/push fold, migrations (new map table), `IRepairStore`/RepairEndpoint/`repair_requests` outbox, `ChunkIndexRepair`, `PendingEmbedJob`/`CodeReindexJob` drain ordering.

**Tests**: `tests/AiRaccoon.Tests` — per-package failing tests first (TDD); `design-tests` runs on the ACs before any implementation phase begins. Exact P-INT/d-426 test names that assert the NULL-keep were not re-verified this session (see H-list) — Package A re-discovers them.

**Edit sequence**: A → (B ∥ D) → C → E → F → G. Shared-file sections serialise (see §4).

**Patterns to follow**: one `BEGIN IMMEDIATE` txn per apply step (rollback + open-request retry); `[LoggerMessage]` partial-`Log` classes; `CommunityToolkit.Diagnostics` guards; Ordinal map semantics; tombstone-create-only-for-dropped; PK-rewrite-on-fold; ChunkIndexRepair-after-rewrite then PendingEmbed drain.

---

## 1. Decisions D1–D6 (positions taken; autonomous session — calls logged, moving on)

**D1 — custom/shared committed entries on the fold path: FOLD them to the winner.**
Broaden the applier from `LabeledProjectScope` (project-scope + non-NULL label) to *all committed scopes* (`project`+`custom`+`shared`, any label incl. NULL), with the existing winner-dedup (`COALESCE(context_label,'')`, no tombstone on dedup-collapse) and vec invalidation extended to the same predicate. *Reasoning*: narrowing the planner to match the applier would converge the loop but leave loser-owned committed content searchable under retired ids — redefinition, not repair, and it contradicts P3's purpose. The d-426 NULL-keep was air-merge caution (don't move unattributed bulk rows mid-merge), not a semantic invariant: no consumer keys on `(project_id, NULL-label)` stability, and the dedup subquery is already NULL-safe. Shared rows fold too: a loser id is deleted from `projects` anyway, so loser-keyed shared rows are orphaned provenance either way; dedup absorbs true cross-project duplicates. *Rejected*: keep-and-pin committed rows (leaves the bank visibly unfixed); force-onto-dropped (destroys preservable content + mints tombstones against the create-only-for-dropped rule's intent).

**D2 — planner honesty: never schedule a fold with zero executable moves.**
`OwnsMoveableContent` is redefined as *exactly the applier's executable predicate* (single shared predicate helper or mirrored truth table; no drift). Any map-attributed id with zero executable rows lands in a new **pinned bucket with a reason line** (`pinned-shared-only` disappears under D1; remaining pins: open-workspace blockers, telemetry-only, unresolvable-with-attachments). The CLI summary gains a first-class line distinguishing **converged** (zero actionable, zero pins) vs **pinned-only** (zero actionable, N pins with reasons) vs **actionable remaining**. *Rejected*: silent `continue`-drop of attributed-but-unmovable ids (today's invisible bucket — the exact dishonesty that hid this bug).

**D3 — metrics/noise/workspaces ownership: telemetry follows, workspaces block.**
(a) `metrics`/`noise` rows re-key to the winner with a trivial UPDATE when the map resolves the id (no vec/FTS shadows on those tables — H4); they are **excluded from `AttachmentCount`** for retire/unresolved verdicts (telemetry is regenerable derived data; cf. telemetry-never-syncs ADR-0098). Unresolvable metrics-only ids (e.g. `b0e32c16`) → `pinned-telemetry-only`, convergence-neutral. (b) `workspaces`/`WorkspaceEntries` **never move across projects** (isolation invariant); an otherwise-foldable loser with open workspaces → `pinned-open-workspaces` naming the workspace ids, and retire never fires while they exist. *Reasoning*: asymmetric value-at-risk — telemetry is recomputable, live workspace scratch is user state. The loop classifies open-workspace pins as *waiting* (writer-activity), not stuck.

**D4 — P3 enforcement: durable bank table + refuse-drops / fold-through-aliases.**
(a) The durable map lives in a **new bank table + migration** (e.g. `project_id_aliases(alias PK Ordinal, winner, kind, applied_at)`), written only by the repair job on successful apply (append the applied one-shot map; rows immutable thereafter). *Rejected*: settings keys — user-editable opaque KV, wrong sync/transactional semantics. (b) Choke points, all reading the cached durable map: **ToolGate** (write path) — writes under a *dropped* id are **refused** with an error naming attribution; writes under an *alias loser* **fold through** to the winner (stale-config writers keep working, data lands canonical); key helpers + watch boundaries inherit via `Default.Fold` once `Default` loads durable state; sync pull keeps folding loser ids, push of loser-keyed rows is refused/folded symmetrically. (c) Sync interplay for the map table: insert-only rows, alias-PK first-writer-wins, same-alias-different-winner conflict surfaces as `unresolved` for a human (H2 to confirm against `SyncService` merge). *Why refuse vs fold-through split*: dropped ids are test residue — resurrecting them is pure harm; alias losers are live-but-renamed — refusing them breaks running agents for a bookkeeping event.

**D5 — run-until-fixed loop UX: one invocation, bounded passes, three stop classes.**
`repair project-ids --apply` becomes derive→commit-request→poll (~15s maintenance)→reap→re-derive until done, bounded (e.g. ≤10 passes / ≤10 min, exact bound set at implementation). Per-pass receipts; stop classes: **converged** | **pinned-only** | **stuck** (identical actionable set across 2 passes with zero rows moved → abort with diagnosis) | **writers-active** (rows move but census totals grow → advise quiesce, loop to bound, then report). Writer-vs-stuck distinction is measured from per-pass moved-counts + census totals, never guessed. `--queue-only` preserves the old fire-and-forget for scripts. CLI stays read-and-request-only per ADR-0075 (polling via `IRepairStore` reads; each re-apply is a new `repair_requests` row through the server).

**D6 — 'fully fixed', formally.** The loop and summary line assert all four: (i) live re-derive yields **zero folds/drops/retires/unresolved**; (ii) every remaining non-canonical census row sits in a **pinned bucket with a reason line**; (iii) **stability**: two consecutive derives yield identical pinned sets with zero moved rows; (iv) **P3 armed**: durable map persisted + a probe write under a retired loser id is refused-or-folded-through per D4. Summary line shape: `converged|pinned-only: 0 fold, 0 drop, 0 retire, 0 unresolved, N pinned (reasons…), P3 armed`.

---

## 2. Packages (each: ACs + proving test/run)

### Package A — planner honesty + pinned buckets (Core; D2 foundation, unblocks all)
- A1: `OwnsMoveableContent` ≡ applier executable predicate (post-D1: committed-scopes-owned); add `Pinned` list with reason lines to `ProjectIdsFoldPlan`; attributed-but-unmovable ids never silently `continue`.
- A2: CLI scoreboard distinguishes converged / pinned-only / actionable; per-pin reason lines printed.
- **ACs**: (1) NULL-only + custom/shared fixture ids from the research record produce folds that the applier fully drains (no zero-move step); (2) open-workspace / telemetry-only fixtures produce pins with reasons, never folds; (3) summary line matches D6 vocabulary.
- **Tests** (fail first): `FromCensus` truth-table tests over synthetic `ProjectIdCensusReport`s (zero-move fold impossible by construction); CLI scoreboard golden-output tests. **Run**: `dotnet test --filter ProjectIdsFoldPlan`.

### Package B — fold applier broadening (Sqlite; D1)
- B1: `FoldEntriesAsync` + `InvalidateVecAsync` move off `LabeledProjectScope` to committed-scopes-owned (project+custom+shared, any label); winner-dedup + no-tombstone-on-dedup unchanged; per-step txn discipline unchanged.
- B2: update/extend d-426 keep-predicate tests (P-INT asserts) to the new contract; `ProjectRows` keeps the single literal home.
- **ACs**: (1) scratch bank with project-NULL + custom-labeled + shared-NULL loser rows folds to zero loser rows in one apply; (2) dedup collisions still tombstone-free; (3) vec/FTS shadows consistent, `ChunkIndexRepair`+embed-drain ordering intact.
- **Tests**: apply-level SQLite tests per surface (moved/deduped/vec-invalidated counts); second-run-no-op (`TotalChanges==0`). **Run**: `dotnet test --filter ProjectIdsRepair`.

### Package C — telemetry/workspace ownership (Core census+plan; D3, after A)
- C1: `AttachmentCount` excludes metrics/noise; metrics/noise re-key on fold; `pinned-telemetry-only` bucket.
- C2: workspaces never move; `pinned-open-workspaces` names workspace ids; retire blocked with reason while they exist.
- **ACs**: (1) metrics-only `b0e32c16`-shaped id no longer blocks retire/unresolved verdicts; (2) workspace-owning loser pins (never moves); (3) census stays SELECT-only.
- **Tests**: census/plan unit tests + apply tests proving workspace rows byte-identical post-fold. **Run**: `dotnet test --filter "ProjectIdCensus|Workspaces"`.

### Package D — durable alias-map table + migration (D4 storage; ∥ with B)
- D1: new table + migration; job persists the applied one-shot map on success; rows immutable; Ordinal semantics; sync rule documented (insert-only, alias-PK first-writer-wins, conflict→unresolved).
- **ACs**: (1) post-apply, durable map round-trips the applied entries; (2) direct-SQL invalid-map rows still refuse safely (existing guard); (3) migration is idempotent, downgrade-safe note.
- **Tests**: migration + persist/round-trip tests; invalid-map refusal test. **Run**: `dotnet test --filter "AliasMap|Migration"`.

### Package E — P3 choke points (D4 enforcement; after D)
- E1: ToolGate refuse-dropped / fold-through-alias; watch boundaries refuse retired ids; key helpers armed via durable-backed `Default` (cached, reload on map change).
- E2: sync pull/push interplay for loser ids + map-table conflict surfacing.
- **ACs**: (1) write under dropped id refused with attribution error; (2) write under alias loser lands under winner; (3) no steady-state regression when map empty (pass-through preserved).
- **Tests**: gate unit/integration tests incl. empty-map pass-through; sync-conflict test. **Run**: `dotnet test --filter "ToolGate|P3|Enforcement"`.

### Package F — run-until-fixed loop + closing summary (CLI/job; D5+D6; after A+B+C)
- F1: `--apply` loop with bounded passes, per-pass receipts, stuck vs writers-active distinction from moved-counts/census totals, `--queue-only` escape.
- F2: D6 verdict + closing-summary line (converged|pinned-only…P3 armed); quiesce guidance text.
- **ACs**: (1) single invocation converges a quiesced multi-pass fixture; (2) live-writer fixture ends writers-active with quiesce guidance, not a false converged; (3) ADR-0075 holds (CLI issues requests+reads only — assert no bank open for write in CLI process).
- **Tests**: loop-state-machine tests with fake `IRepairStore`/census sequences; CLI golden-output tests. **Run**: `dotnet test --filter "RepairLoop|ProjectIdsRepairCommands"`.

### Package G — INTEGRATION (last; cross-package; D6 end-to-end)
- G1: scratch-bank E2E: NULL-only + custom/shared + metrics-only + live-writer conditions → **one invocation converges to the D6 verdict**.
- G2: post-fix probe: write under retired loser id refused-or-folded-through per D4; re-derive stability (two identical derives, zero moves).
- **ACs (= top-level AC)**: every package AC checked and met, plus the E2E proof above.
- **Tests**: full-stack test driving CLI→server→job→drain on a scratch bank; red-proofed (each assertion first shown failing against pre-fix behavior). **Run**: `dotnet test --filter Integration.ProjectIdsConvergence` + manual live-bank smoke per the manual-checklist skill (notes, not edits).

---

## 3. ADRs to file (`docs/adr/`, Nygard shape: Context / Decision / Consequences +/−/0 / Alternatives)
1. **Fold all committed scopes incl. NULL-context** (D1) — overturns d-426 keep; consequence (−): bulk-row `project_id` churn on first post-fix repair.
2. **Telemetry excluded from verdicts; workspaces immovable blockers** (D3).
3. **Durable alias-map table + refuse/fold-through P3** (D4) — incl. sync conflict rule.
4. **Bounded run-until-fixed loop with measured stuck-vs-writers distinction** (D5) + **D6 fully-fixed predicate** (may fold into one ADR).

## 4. Parallelism
- **Parallel**: B ∥ D (disjoint files: `ProjectIdsRepair.cs` vs new table/migration + job-persist section — coordinate the one shared section in `ProjectIdsRepairJob.cs`: serialise that hunk).
- **Serial**: A → C (both reshape plan buckets); A → F (verdict vocabulary); D → E (storage before chokes); everything → G.
- Shared-file serialisation points: `ProjectIdsFoldPlan.cs` (A then C), `ProjectIdsRepairJob.cs` (D-persist hunk vs F-receipt hunk — merge order D,F), `ProjectIdsRepairCommands.cs` (F only after A2's vocabulary lands).

## 5. Hypotheses NOT verified (labels; implementer must close each before coding that package)
- **H1**: exact names/locations of d-426 P-INT keep-predicate tests asserting NULL-keep (A/B must re-discover; I did not enumerate `tests/`).
- **H2**: `SyncService` merge shape for a new table (row-merge vs snapshot) — D's conflict rule assumes row-merge; if snapshot, map rides free and E2 simplifies.
- **H3**: live-bank per-id counts quoted in the brief were taken as evidence, not re-queried (bank may have drifted; G re-establishes ground truth on a scratch bank).
- **H4**: `metrics`/`noise` tables carry no vec/FTS shadow triggers (C's trivial re-key depends on it).
- **H5**: `promotion_queue_entries_ad` trigger interplay under the broadened entries predicate (B must prove queue-before-entries ordering still suffices for custom/shared/NULL rows).
- **H6**: `uq_entries_committed_bucket` exact columns (B's dedup-covers-all-scopes claim assumes the winner-dedup subquery spans the folded scopes).
- **H7**: maintenance poll interval ≈15s and `repair_requests` open-row retry semantics under rapid re-apply (F's poll/timeout constants).
- **H8**: `IProjectIdsMigrationGate` (seen in `Core/Projects/`) isn't already a P3 gate stub that D4 must extend rather than replace.
