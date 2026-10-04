# Unregistered project ids: research record (2026-10-04)

Task `air-refuse-unregistered-project-id-tools`. The goal is that every tool or entry point that
persists anything keyed by a project id refuses an id the bank does not know. The task also covers
three follow-up defects in the project-ids repair. Each finding carries how it is known: **read**
(source cited), **measured** (live bank, command given), or **INFERRED**.

## Trigger (measured, live bank `~/.ai-raccoon/memory.db`, 2026-10-04)

- The `repair project-ids` census found 12 ids that matched no known project. All of them owned
  only `metrics` and `search_quality` rows written by reads (`memory_search`, `memory_stats`,
  `memory_watch_status`). None held entries.
- The largest was `d42bd180-…`: 843 metrics rows and 75 search_quality rows. It is vinted-offers'
  `.ai-badger/project-id`, a uuid4 that the ai-badger 0.187.5 scaffold wrote and that was never
  registered. Writes under it were refused with `project-not-registered`.
- Fixed in the bank by an alias map (`~/.ai-raccoon/project-id-map.json`) and `repair project-ids
  --apply`. After that, a write under `d42bd180` lands in `project:01a102bb-…`.

## 1. Where the read exemption lives (read)

- `src/AiRaccoon/Tools/ToolGate.cs` `RequireAsync` runs these steps in order:
  1. bank available
  2. cwd resolve for a blank id
  3. canonicalize
  4. alias fold and refusal of dropped ids, only once migrated and only for non-Read calls
  5. access guard
  6. registration guard
- `src/AiRaccoon/Projects/ProjectRegistrationGuard.cs:31-33`: `if (requirement == Read) return;`.
  This is the only place that lets reads through.
- The registration guard on writes:
  - A registered id passes.
  - An id the bank holds rows for (`HasRowsAsync`: entries in project/custom scope, or
    code_entries) passes and logs warning 433.
  - On a bank that has not been migrated, a non-guid id is auto-registered.
  - Anything else is refused.
- The store layer never checks the registry. Only the ToolGate path enforces registration.

## 2. What a read persists under its id (read)

- **Every tool call** goes through the telemetry filter (`src/AiRaccoon/Observability/ToolTelemetry.cs`).
  - A successful call writes one `metrics` row keyed by the raw `projectId` argument.
  - A refused call writes one row tagged `refused`, not the bogus id.
  - So refusing in the gate keeps bogus ids out of `metrics`.
- **`memory_search`** writes after the gate:
  - one `search_quality` row on every call (`SqliteSearchQualityService` INSERT, raw query text
    included)
  - about 13 search-phase `metrics` rows (`MemoryTools.RecordSearchMeasurements`)
  - access bumps on hit rows (`SqliteMemoryStore.cs:485`). The bump also applies to shared-tier
    rows, whatever the caller's id.
- **`memory_share_extract mode=propose`** is gated as Read but writes `promotion_queue` rows
  (`ShareExtractService.ProposeAsync` → `PromotionQueueService.UpsertAsync`).
- **`memory_record_grade` / `memory_record_followthrough`** update `search_quality` by
  `correlation_id` only. The `projectId` argument plays no part in the SQL, so one project can
  grade another project's row.

## 3. Read-gated MCP tools (read)

These tools are gated as Read, so an unregistered id passes them:

- `memory_get`, `memory_search`, `memory_list`, `memory_stats`
- `code_get`, `memory_performance`
- `memory_promotion_list` (the gate runs only when a projectId is given)
- `memory_watch_status`, `memory_workspace_status`
- `memory_sweep` with dryRun
- `memory_share_extract` with propose

Every Write and Destructive tool already refuses an unregistered id. `project_id_token_get` takes
no id.

These must keep working under a stricter gate:

- `project_id_token_get`
- `memory_promotion_list allProjects=true`
- `memory_performance projectId="__self_metrics__"`. The sentinel is unregistered and has to be
  allow-listed (`MetricsReportService.cs:60-61`).
- background writers keyed by `__self_metrics__`. They never touch ToolGate.
- a cwd-resolved blank id. It must still pass the registration check.

## 4. Entry points that write per-project state outside ToolGate (read)

None of these validate the id against the registry:

- `PUT /settings` accepts any key (`SettingsEndpoint.cs:45-56`).
- The CLI writes per-project keys with no registry check:
  - `settings access set <id>` → `access.mode.project:<id>`
  - `settings watch enable/disable <id>` → `watch.enabled.<id>`
  - `settings watch concurrency <id>` → `watch.concurrency.<id>`
  - `settings ingest scope add <id> <path>` → `ingest.scope.<id>`
- Watch ingestion keeps ingesting for any `watches` row. Only `memory_watch_add` is gated.
- `memory_sync` pull merges rows under ids the remote supplies. The `projects` table is not
  synced, so those rows rely on the "bank holds rows" exemption.

## 5. Tests that pin the read exemption (read)

- `tests/AiRaccoon.Tests/Integration/Projects/OrphanVerbatimRefusalTests.cs:167-190`
  (`OrphanRead_Passthrough`, ledger `refuse-reads`)
- `tests/AiRaccoon.Tests/Unit/Projects/ProjectRegistrationGuardTests.cs`. INFERRED: it has a case
  where reads pass unconditionally.
- These E2E tests read before any write, under ids nothing registered:
  - `McpServerToolSurfaceE2ETests.cs:65` (`surface-test`)
  - several `memory_stats acme` calls in the OTLP, launch-args, token-gate and proxy E2E tests
  - The seeding helper is `TelemetryServerHost.SeedProjectRegistrationAsync`.
- About 28 test files and three BDD contexts use the stub `AllowingRegistrationGuard`. A change
  confined to the real guard leaves them untouched.

## 6. Repair defects (read)

- **The CLI declares the repair done too early.**
  - `ProjectIdsRepairCommands.RunPassAsync` sleeps one poll, re-derives the census, and stops
    once nothing is actionable.
  - The job (`ProjectIdsRepairJob.RunAsync`) folds the rows, then runs a whole-bank
    `ChunkIndexRepair`. Only after that does it run `PersistAppliedAsync`,
    `LoadAndCacheAsync` and `FinishRepairRequest`.
  - `IRepairStore` exposes no finished/status query. The SQL primitives exist:
    `MemorySql.HasOpenRepairRequest` and `HasFinishedRepairRequest`.
  - Measured: the CLI printed `P3 armed (11 alias…)` at 10:47Z. The server stamped `finished_at`
    at 10:50:32Z, and the alias table went to 19 aliases and 32 drops.
- **Quality rows block the telemetry-only pin.**
  - `ProjectIdCensusRow.AttachmentCount` includes `QualityRows`.
  - So an unmapped, unregistered id that owns only metrics and search_quality rows misses the
    telemetry-only pin (`ProjectIdsFoldPlan.cs:166`) and lands in "needs a human".
  - ADR-0098 classifies search_quality as telemetry.
  - Folds and drops must still move or delete quality rows (`FoldQualityAsync`).
- **Warning 433 names a command that does not exist.**
  - The text says "Convert it with `project id convert`".
  - ADR-0089 decision 6 planned that verb, but it was never implemented
    (`git log -S'project id convert'`).
  - The working remedy is `repair project-ids --map <file> --apply`, or `project_id_token_get`.
  - `docs/reference/logging-event-ids.md:163` quotes the message.

## 7. ai-badger side (read, repo `~/RiderProjects/ai-badger`)

- These write a uuid4 to `.ai-badger/project-id` and never call ai-raccoon:
  - `features/common/skills/welcome-ai-badger/scripts/project_id.py:13-17`, called from
    `scaffold.py:713`
  - `features/common/skills/den-refresh/scripts/refresh.py:230-242` (`ensure_project_id`)
- ADR-0025 makes that file the bus identity and keeps ai-badger independent of ai-raccoon.
- The memory hook (`ai-raccoon-memory/scripts/memory_context.py:578-587, 780-792`) sends the file's
  id as `projectId` to `memory_search`.
- `memory_first_gate.py:236-241` tells agents to search under a third id, the directory basename.
- ai-raccoon can register only through `project_id_token_get`, which mints a new guidv7. No CLI
  or tool registers an id that already exists (`IProjectRegistry.RegisterAsync` accepts any
  string). The ADR-0089 decision 6 verbs `project id generate` and `project id convert` were never
  built.
