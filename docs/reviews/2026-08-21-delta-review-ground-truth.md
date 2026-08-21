# Delta project-scope review — ground truth (Phase 0)

Date: 2026-08-21 · Base commit: `155f281e6330e136beb9f60046bbf9d9d20d42fa` (main, clean tree) ·
Worktree: `/Users/arasz/RiderProjects/ai-raccoon-review-0821` (branch `review/delta-0821`)

## Scope decision (owner-ruled)

Delta review relative to the 2026-08-14 campaign
(`docs/reviews/2026-08-14-project-scope-review.md`, base `1d1889d5`, PR #290), through the last
merged feature (#404), run in an isolated worktree.

## Measured baseline at `155f281e`

- `dotnet build` → **Build succeeded, 0 warnings, 0 errors** (11.7 s)
- `dotnet test` (unfiltered, `xUnit.MaxParallelThreads=4`) →
  **Failed: 1, Passed: 3884, Skipped: 11, Total: 3896, 24 m 45 s**
  - The 1 failure: `WatchIntegrationTests.DeletedDirectory_Cascades_RemovesChunksAndFingerprintsOfNestedFiles`
    — "StepUntilAsync gave up after 600 steps: fake-time budget expired (fake 60s/60s, real 18.2s/30s)".
    **Passes in isolation** (re-run: 1/1 passed, 1 s). Same shape as the resolved suite-hang
    observation in `docs/work/2026-08-20-tests-after-memory-store-refactor.md` (full-suite
    interaction, not reproducible alone). Not in `known-flakes.json`.
  - Skips are `Assert.Skip` env-gated probes (golden capture, real-data, harness), not silent
    `return`s. 32 files carry `Speed=Nightly`; CI's build-slow job covers `Speed=Slow` — verify
    whether Nightly is included in any CI leg (open question for lanes).
- Size: Core 8,059 lines · Infrastructure 17,559 · Host 10,483 (production 36,101) ·
  tests 87,100 · ratio ≈ 2.41:1

## The delta (1d1889d5..155f281e): 168 commits

~10.9k production insertions / 1.5k deletions across 204 files; ~33.8k test insertions across 345
files. New subsystems:

1. **Arbitrary embedding models** (#402/#404, ADR-0084): manifest-described engines,
   `model download` verb, HF tree client, download planner/service, ONNX graph probe
   (hand-rolled protobuf walker), WordPiece/SentencePiece tokenizers, dimension reconciler,
   model-migration outbox + lease + job, golden-vector tests.
2. **Nine-phase search pipeline + SearchParameters** (ADR-0083): query > settings > default
   parameter source; `SqliteMemoryStore` split into partials; timings/metrics ride out with results.
3. **Retrieval tuning harness** (#400/#407/#410): Optuna tuning, eval corpora
   (`eval-set-100.json`, `sextant-6.json`, `test-set-10.json`), regression-elimination report.
4. **Maintenance job framework** (ADR-0070/0076): job list + ledger, on-demand poll jobs
   (pending-embed, model-migration, chunk-index-repair, reingest-repair, promotion-queue-prune).
5. **Metrics store** (ADR-0074): capped MeasurementBuffer, flusher, sqlite store, performance verb.
6. **Settings endpoints** (ADR-0075): server-mediated settings/model/repair/prune endpoints,
   LazyServerSettingsStore, single-writer rule.
7. **`doctor` verb** (SchemaDoctor, exit codes 19/20), release workflow, nightly triage script.
8. Query guard moved into Core (`Core/Memory/QueryGuard/`).

## Prior-campaign findings: delta status (orchestrator-verified at path:line)

FIXED in the delta (verified):
- **B1** cross-project delete: `ContextScope.RequireWithinProject` now gates
  `DeleteContextAsync` (`SqliteMemoryStore.cs:288`); `ContextFilterProvider` is documented as the
  read/delete twin of `EntryBucket`; ADR-0051; README SECURITY line (1.13.0).
- **B3/H24** write/ingest chunking: `memory_write` chunks via `fileIngestor.ChunkToBudgetAsync`
  (`SqliteMemoryStore.cs:104-115`, `WriteChunks.cs`, ADR-0064); budget is engine-aware
  (ADR-0036 invariant, ADR-0084 §2); `ChunkBackfill` re-chunks oversized rows (ADR-0069).
- **H6** shared write bypass: naming `shared` now requests promotion
  (`MemoryWriteService.cs:40-47`, ADR-0067).
- **H12** promotion_discards reaper: `PurgeOldDiscards` wired in
  `BankMaintenanceHostedService.cs:311` (age + no-longer-in-bank conditions, ADR-0055/0026).
- **H13/H26** false crit + double-dispose: `NodeRunner.cs` finally-dispose retained but the
  catch/return structure changed (needs lane re-derivation; the old double-dispose shape at
  :123-129 still disposes after WaitForShutdownAsync — verify).
- **H14** unknown CLI token: parse errors on launch paths now return `FailedToParseCliArgs=9`
  (`AppRunner.cs:48,70`, ADR-0060).
- **H19** concrete ToolGate registration: `AddRequiredSingleton<IToolGate, ToolGate>()`
  (`AppRegistrations.cs:135`) + `LayeringRulesTests.EveryToolClass_InjectsOnlyInterfaces`
  (failed-when-written test).
- **H22** MCP-layer business logic: query guard moved to `Core/Memory/QueryGuard/`; write path
  composed into `Core` `MemoryWriteService` (cycle-avoidance documented).
- **H23** no architecture tests: ArchUnitNET wired (`LayeringRulesTests`, 4+ rules incl.
  anti-vacuity scan check).
- **H2** vacuous metrics gate: `[0,1]` assertions deleted (`BaselineMetricsTests.cs:104-111`),
  replaced by held-out floors in `HeldOutRetrievalGateTests` (A8/A9/A10 pins + mean-floor +
  reversal discrimination proof).
- **H3** in-sample-only numbers: `RetrievalTuningSets` derives TunedDocuments/HeldOut by document
  partition (leakage-aware); held-out gate is Nightly-tagged.
- **H8** promotion_list unguarded when projectId omitted: gate now runs when projectId given;
  the no-projectId branch still skips the gate (`PromotionTools.cs:36-40`) — **partially fixed,
  lane to re-derive whether the residue is reachable**.
- **H17** release notes: release.yml added; version-bump single-sourced from VERSION (1.29.0).
- **Nightly silence**: scripts/nightly-triage.py + red-nightly issue filing (issues: write).
- **ADR Status staleness**: 0013/0029/0030/0033 now read Superseded.

STILL OPEN (verified still present at this base):
- **H18**: `ResiliencePipelineFactory.cs:62` still string-matches `"EmptyDownloadException"`.
- **H20**: `WorkspaceService`/`IWorkspaceService` still in Infrastructure/Workspace/.
- **H21**: `IMemoryStore` still ~30 members (god port) — though SqliteMemoryStore is now partials.
- **H16**: CI still ubuntu-only (build.yml 4 jobs, publish.yml ubuntu-slim); no matrix.
- **H9/H10**: sync still VACUUM INTO whole bank, `projectId` only names the object key;
  remote blob trusted as SQLite (encrypted-bank case noted at SyncService.cs:221-222).
- **H1**: `ranking` still rank-derived (normalized per response, documented at
  `MemoryTools.cs:117`); `SourceAffinityRanker` lambda default 0.1 unchanged.
- **H4**: `StructureFusion.Fused` cap retained as measured/deliberate (ADR-0057) — prior
  campaign's disconfirmation stands.
- **H7**: access mode still resolves from the named project (`MemoryAccessGuard.cs:9-17`).
- **H15**: backend autostart diagnostics — lane to re-derive against the new settings-endpoint
  autostart path (LazyServerSettingsStore).
- **Owner question 9**: `tests/AiRaccoon.Tests/Resources/jsaa-memory.db` (19 MB, owner email in
  94 rows) still in repo.

## New-code leads for lanes (hypotheses, NOT verified facts)

- Manifest activation path does not re-verify per-file sha256 on load (deliberate per
  EmbeddingManifestLoader.cs comments — fingerprint covers it; is the reasoning sound when the
  manifest itself is edited in place?).
- `model download` endpoint is CLI-suppliable (`--endpoint`), default huggingface.co.
- VecDimensionReconciler: create-if-missing-or-mismatch with NO repopulate — correctness depends
  on MarkAllEmbeddedPending ordering (ADR-0076 outbox). Join-shaped hazard.
- MeasurementBuffer: reserve-before-enqueue cap; dropped-count observable.
- Settings endpoints sit behind McpTokenGate (default-closed, only /observability open).
- eval-set-100.json: 100 queries, 75 ADR-derived from ONE document family (docs/adr of this repo's
  own docs?) — corpus diversity question for the retrieval lane.
- Sextant-6 corpus is synthetic fixture-based — circularity question.
- Suite-hang shape (idle testhost ~3.5 min in) reproduced once at this base; resolved doc says
  "does not reproduce on its own". The orchestrator's first run also stalled (killed at 18 min
  idle); second run completed in 24m45s. Flaky-hang lead for the QA lane.

## Phase 4 — live-system calibration (read-only queries, 2026-08-21 ~19:00 CEST)

Instrument: direct read-only sqlite3 against `~/.ai-raccoon/memory.db` (248 MB, `user_version` 10,
`application_id` digest present). Deployed binary: **ai-raccoon 1.28.1** (`~/.dotnet/tools/.store`),
one minor behind HEAD's 1.29.0.

| Check | Result | What it changes |
|---|---|---|
| Schema version vs HEAD | live `user_version`=10 = HEAD `MemorySchema.CurrentVersion`=10 | The deployed bank's schema is current; no migration debt |
| Entries / projects | 23,556 entries; jsaa 11,089 · ai-raccoon 7,136 · ai-badger 2,225 · hermes-default 1,527 · arasz-home-page 1,230 · +3 tiny | Cross-project data real; B1-class defects would be armed if reintroduced |
| Shared tier | 229 rows (was 138) | Growing; H6 fix matters more now |
| embed_state | all 23,556 `embedded` | No stuck-pending backlog on the live bank |
| Oversized rows (>20k chars) | **0**; avg len 830, max 11,361 | Prior campaign's 42.7% over-window population is gone from new writes — chunking fixes are firing; residual over-window rows would need a tokenizer-based recount to quantify |
| `ttl_days` set | 0 of 23,556 | Still loaded-not-fired |
| `model_migration` rows | 0 (table exists) | Migration outbox **loaded, not fired** — #404's swap path has never run in production |
| vec pending tables | absent (created only during a migration) | Consistent with ADR-0076 design; nothing mid-drain |
| `maintenance_jobs` ledger | pending-embed ×160, metrics-retention ×60, vacuum ×6, repair/prune jobs registered | The job framework is live and cycling, not decorative |
| `metrics` rows | 5,909; search.* phases ×325 each; `metrics.dropped` always 0.0 | Metrics pipeline live; buffer cap never hit in practice |
| `promotion_queue` | **762 rows** (was 19), scored reasons incl. agent-requested-share classes | Queue growth is real; prune job exists but is on-demand-only — reaper question returns at larger scale |
| `promotion_discards` | 965 (unchanged) | Retention purge wired (`PurgeOldDiscards`) — verify it actually runs on a schedule |

**Loaded-not-fired verdict:** the entire arbitrary-model surface (#404: download verb, manifests,
dimension reconcile, migration drain) has never executed against the live bank — the deployed
binary predates it. Any defect found there is real but cannot have fired yet; urgency is
pre-deployment, not hotfix. Conversely the search-parameter settings (ADR-0083) and maintenance
jobs ARE live, so findings there rank higher.
