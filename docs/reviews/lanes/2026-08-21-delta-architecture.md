# Lane report — Architecture/layering (2026-08-21 delta campaign)

Lane: architecture (high-reasoning) · Base: `155f281e` · Read-only · 8 findings
(6 MEASURED, 2 READ; 2 MEDIUM, 2 LOW, 4 NIT). Two briefed leads disproven (F4, F5).

### F1 — Two of seven control-plane ports live in Infrastructure while their five siblings live in Core [MEASURED]
**Severity:** MEDIUM
**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/IPromotionQueuePruneStore.cs:13`, `src/AiRaccoon.Infrastructure/Watch/IWatchRegisteredStore.cs:9` vs `ISettingsStore`/`IModelMigrationStore`/`IRepairStore`/`IMaintenanceStatsStore`/`INoiseSummaryStore` all in `src/AiRaccoon.Core/Memory*/`; consumers `src/AiRaccoon/Settings/ServerSettingsStore.cs:33-34` and `LazyServerSettingsStore.cs:17-18` implement all seven.
The project's own convention is stated in `LayeringRulesTests.Rule 4` ("a port … lives beside the other ports in Core"), but that rule pins only `IMetricsReportService` by name — nothing generic guards port placement, so this drift passes CI. Cost: the CLI transport for `extract prune` and `watch registered` depends on Infrastructure types for what are otherwise Core ports. Smallest fix: move both interfaces to `Core/Memory` and add one generic ArchUnitNET rule.

### F2 — Ranking math is split across layers: the reorder moved to Core, RRF and source-affinity stayed behind [MEASURED]
**Severity:** MEDIUM
**Evidence:** `Core/Memory/Fusion/{NoFusionRegression,ModalityLeg,FusionDiff}.cs` (pure, Core) vs `Infrastructure/Sqlite/Memory/ReciprocalRankFusion.cs` and `SourceAffinityRanker.cs`; orchestration in `SqliteMemoryStore.cs:566-634`.
The nine-phase pipeline lead resolves to "partially": `NoFusionRegression.Reorder` (ADR-0078) is genuinely in testable Core, but `ReciprocalRankFusion.Fuse` and `SourceAffinityRanker.Rank` — equally pure static functions of candidate lists — remain in Infrastructure. The retrieval-tuning harness must reference Infrastructure to experiment on the two most-tuned ranking components. Smallest fix: relocate both files to `Core/Memory/Fusion`.

### F3 — SqliteMemoryStore's partial-class split is file-level decomposition, not architectural [MEASURED]
**Severity:** LOW
**Evidence:** main partial `SqliteMemoryStore.cs` = 981 lines holding all 27 public `IMemoryStore` members plus pipeline privates; partials (`Search.cs:88`, `SearchParameters.cs:16`) call shared primary-ctor members.
The split satisfies the size ratchet but coupling is unchanged: one class, one DI registration. H21 stands — `IMemoryStore` measures **27 members**. Smallest fix direction: carve real collaborators rather than more partial files.

### F4 — Settings endpoints hold no business logic — briefed lead disproven [MEASURED]
**Severity:** NIT (positive result)
**Evidence:** `SettingsEndpoint.cs:20-88`, `RepairEndpoint.cs:20-51`, `PromotionQueuePruneEndpoint.cs:20-34` — validation + transport mapping only, delegating to store ports; mapped only on the token-guarded host (`McpServerSetup.cs:138-153`). Nothing to fix.

### F5 — Embedding download/migration keeps engine orchestration out of Core — lead holds [MEASURED]
**Severity:** NIT (positive result)
**Evidence:** Core's model-migration surface is records + port only; planner/service/HF client/ONNX probe/tokenizers/reconciler all under Infrastructure. No leakage.

### F6 — Capability ports are segregated in name only: one class implements all seven, resolved by runtime casts [NIT]
**Evidence:** `LazyServerSettingsStore.cs:90-117` — six `As*Store` casts throwing `NotSupportedException` on mismatch. Deliberate ("one class, one credential, one transport") but nothing statically prevents calling an unsupported capability.

### F7 — IMemoryStore still carries four settings members duplicating ISettingsStore after the ADR-0075 carve-out [MEASURED]
**Severity:** LOW
**Evidence:** `IMemoryStore.cs:85-115`; `SqliteMemoryStore.cs:543-551` delegates to its settings snapshot. The "CLI never writes the bank via IMemoryStore" guarantee rests on convention. Fix: drop the four members, tools take `ISettingsStore`.

### F8 — BankMaintenanceHostedService has two construction paths with silently different behavior [READ]
**Severity:** NIT
**Evidence:** `BankMaintenanceHostedService.cs:33-54` — narrow primary ctor leaves `_jobRunner = null` / `_jobs = []`; a service built the narrow way runs zero jobs with no signal.

## Still open
- VecDimensionReconciler ordering hazard — retrieval/data lanes' question.
- MeasurementBuffer contention semantics — QA lane.
- H13 NodeRunner dispose shape not re-derived.
- Nightly CI coverage — resolved separately by orchestrator: nightly.yml runs unfiltered via scripts/nightly-triage.py, so Nightly IS covered.

Prior-campaign spot-checks: H18 confirmed (`ResiliencePipelineFactory.cs:62`), H20 confirmed, H21 confirmed at 27 members.

## Owner questions
- Move `IPromotionQueuePruneStore` and `IWatchRegisteredStore` to Core?
- Add a generic ArchUnitNET port-placement rule instead of per-name pins?
- Move `ReciprocalRankFusion` and `SourceAffinityRanker` into `Core/Memory/Fusion`?
- Accept partial-file split as terminal for `SqliteMemoryStore`, or schedule collaborator extraction?
- Remove the four duplicated settings members from `IMemoryStore`?
