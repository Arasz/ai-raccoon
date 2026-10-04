# Unregistered project ids: implementation plan (2026-10-04)

Task `air-refuse-unregistered-project-id-tools`, draft PR #846. Inputs are the research record
(`2026-10-04-unregistered-project-id-research.md`) and the owner rulings D0-D5
(`2026-10-04-unregistered-project-id-feedback.md`). Every ruling is approved. This plan is about
how to build it, not whether to.

## Session work items, in order

1. A: reads meet the registration test; dropped ids are refused on reads; warning 433 names real remedies.
2. A2: seed a registration in every test that reads before it writes (runs beside A).
3. D: the repair loop waits for `finished_at` (runs beside A).
4. E: telemetry-only pin counts search_quality as telemetry (runs beside A).
5. F: grade and follow-through touch only the caller's project's row (runs beside A).
6. G: ADR-0127 and the ADR-0089 amendment (runs beside A).
7. C: settings writers refuse unregistered ids; exit code 18 (after A).
8. B1: server-side `project_id_get`, the project directory and the `/projects` endpoint (after A).
9. B2: CLI `project id register|get|check`; exit code 19 (after B1 and C).
10. H: VERSION 1.57.0, changelog, breaking changes, README, follow-up issue (after every content step).
11. J: join: whole-solution build, cross-step suites, docs gates.

## Contract handed to ai-badger

| Verb | Outcome | Exit | stdout | stderr |
|---|---|---|---|---|
| `project id register <guid> [--name <n>]` | newly registered (after alias fold) | 0 | `registered <id>` | nothing |
| | already registered, or an alias of a registered id | 0 | `already registered <canonical>` | nothing |
| | retired (dropped) id | **18** | nothing | refusal naming the repair attribution |
| | non-guid argument | **10** | nothing | usage message |
| `project id get --name <n>` | exactly one match | 0 | the stored id, no prefix (legacy raw-text ids printed as stored) | nothing |
| | no match | **18** | nothing | not-found message |
| | several matches | **19** | nothing | ambiguous message listing the ids |
| `project id check <guid>` | registered, or folds to a registered or row-holding id | 0 | `known <canonical>` | nothing |
| | unknown | **18** | `unknown <id>` | nothing |
| | retired | **18** | `retired <id>` | nothing |
| | non-guid argument | **10** | nothing | usage message |
| any verb | server unreachable, unproven, refused token, too old (no `/projects`) | the shared path's codes, unchanged: 53, 57, 60, 62 and the rest of 30-66 | nothing | the shared path's message |
| `settings access set / watch enable\|disable / watch concurrency / ingest scope add` | unregistered or retired id | **18** | nothing | refusal message |

New constants (both in ADR-0107's `Usage` decade, the only category with free slots, 18 and 19):

- `ErrorCode.Usage.ProjectUnknown = 18`: no usable project matches the name or id. Either nothing is
  registered under it, or the id is retired. Added by step C.
- `ErrorCode.Usage.ProjectAmbiguous = 19`: several registered projects share the name. Added by step B2.

The `Bank` (30-39), `Server` (50-59) and `Key` (20-29) decades are full. `Usage` fits the
meaning: the caller passed a value that names no usable project, and fixes the invocation.
Retired and unknown share 18 because `Usage` has two free slots and `get` needs both. Within
each verb an 18 is still unambiguous: `register` can only return 18 for a retired id, because
it registers an unknown one. `check` says which case it is on stdout.

Argv: `ai-raccoon [--quiet] project id register <guid> --name <repo>`. `--name` is optional.
No verb prompts or reads stdin. A non-guid fails before any server is acquired. The result line
goes to stdout even under `--quiet`.

Lookup-before-mint (ADR-0127 states it):

- ai-badger, `.ai-badger/project-id` missing: `project id get --name <repo>`. On 0, write the
  printed id. On 18, mint a guid locally and `project id register <guid> --name <repo>`. On 19, stop
  and ask a human.
- ai-badger, file present: `project id check <id>`. On 18 with `unknown`, `project id register
  <id> --name <repo>`. On 18 with `retired`, stop: the id was deliberately retired.
- ai-raccoon alone (an agent, no ai-badger): `project_id_get(name)`. On `project-not-found`, call
  `project_id_token_get(name)`. On `project-name-ambiguous`, ask the user which id.

## Design decisions (made in this plan, owner rulings permitting)

1. **One registration rule, in the real guard.** `ProjectRegistrationGuard.EnsureAsync` loses its
   Read early return. For every requirement: registered passes; a bank that holds rows passes
   (warning 433, once per id per process); a write (not a read) on an unmigrated bank
   auto-registers a non-guid id; everything else throws `UnregisteredProjectException`. Reads
   never register. The 30 test files using `AllowingRegistrationGuard` stay untouched: they
   swap the guard out, so a change confined to the real guard cannot reach them.
2. **Dropped ids are refused on reads.** `ToolGate.RequireAsync` drops `requirement is not Read &&`.
3. **The `__self_metrics__` allow-list lives in `PerformanceTools`**, not in the guard. The tool
   takes the same branch `PromotionTools` takes for `allProjects=true`: `RequireBankAvailableAsync`,
   then the sentinel as the id. In the guard it would let `memory_search` under the sentinel pass,
   and that writes search_quality rows under it.
4. **`memory_share_extract mode=propose` needs no code change.** After decision 1 its Read gate
   applies the registration test, so it refuses unregistered ids. Step A pins this with a test.
   Its access-mode requirement stays Read: changing that is an access-mode question, not this task.
5. **Settings writers are gated at the server, on `PUT /settings`.** The CLI writers already reach
   the server through `ServerSettingsStore.SetSettingAsync`, so one server-side check covers CLI
   and HTTP. The key's owner comes from the census's own attribution rule, moved to Core as
   `ProjectSettingsKeys.TryGetProjectId` (pure). Owner `*` passes. The test is
   `IProjectRegistrationGuard.EnsureAsync(canonical, AccessRequirement.Read)`: registered or holds
   rows, and it never registers as a side effect. A refusal is 409 with the refusal text as a
   plain-text body. The client throws `ProjectRefusedException(body)`, and `ConfigCommands`
   prints it and exits 18.
6. **One interface serves the CLI and the server**, mirroring `IRepairStore`: `IProjectDirectory`
   (Core) has `RegisterAsync(id, name) -> ProjectRegistration(ProjectId, Outcome)`,
   `FindByNameAsync(name) -> IReadOnlyList<string>` and `CheckAsync(id) -> ProjectIdCheck(ProjectId, Status)`.
   The server implementation is `ProjectDirectory`, which uses `IProjectRegistry`, the migration
   gate and `ProjectIdAliasMap.Default`. The CLI implementation is
   `ServerSettingsStore`/`LazyServerSettingsStore` over `/projects`. The no-match and
   several-match rule is one pure function (`ProjectNameMatch.Single(name, ids)`) that throws
   `ProjectNotFoundException` / `ProjectNameAmbiguousException`. The MCP tool and the CLI both
   call it, so the tool stays a single call.
7. **The repair loop reads `repair_requests` state off the census.** `ProjectIdCensusReport` gains
   `bool RepairOpen = false`. `SqliteRepairStore.ReportProjectIdsAsync` fills it from
   `MemorySql.HasOpenRepairRequest` and stays SELECT-only. The CLI keeps polling while it is
   true. No new endpoint and no new `IRepairStore` method. The server job order is unchanged.
8. **Grade and follow-through report what happened.** Both service methods take `projectId`, add
   `AND project_id = @ProjectId`, and return `bool` (row updated). `QualityTools` returns
   `Recorded = <that bool>`. A cross-project or unknown correlation id now answers
   `recorded: false` instead of a silent `true`.

## DAG

Waves: W1 = {A, A2, D, E, F, G}; W2 = {B1, C}; W3 = {B2}; W4 = {H}; W5 = {J}.

Gate commands use the built test assembly, because `dotnet test --filter` runs zero tests here:

```
dotnet build AiRaccoon.slnx
T=tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests.dll
dotnet exec $T --filter-class <FQN> [--filter-class <FQN> ...]
```

Every run must report a non-zero `total:`. A step that adds a file under `tests/.../Integration/`
uses `[RetryFact]`/`[RetryTheory]` and adds `AiRaccoon.Tests.Unit.RetrySurfaceGateTests` to its gate.
Every new test class carries class-level `[Trait(TestCategories.Category, ...)]` and
`[Trait(TestCategories.Speed, ...)]`. Every lane runs the repo-wide source gates before it pushes,
which means building the whole solution (`dotnet build AiRaccoon.slnx`, which includes benchmarks).

---

### A: Reads meet the registration test; dropped ids refused on reads; warning 433 fixed

- depends_on: none
- effort: medium
- files:
  - `src/AiRaccoon/Projects/ProjectRegistrationGuard.cs` (remove the Read early return; reads
    never auto-register; `Guard.IsNotNullOrWhiteSpace`; 433 message text)
  - `src/AiRaccoon/Projects/IProjectRegistrationGuard.cs` (doc contract)
  - `src/AiRaccoon/Tools/ToolGate.cs` (D2 line 69; doc comment)
  - `src/AiRaccoon/Tools/IToolGate.cs` (doc comment: "reads pass through" is gone)
  - `src/AiRaccoon/Tools/PerformanceTools.cs` (sentinel branch)
  - `src/AiRaccoon.Core/Projects/UnregisteredProjectException.cs` (message: look up with
    `project_id_get`, mint with `project_id_token_get`, or register an id you hold with
    `ai-raccoon project id register <id>`)
  - `src/AiRaccoon.Core/Projects/RetiredProjectException.cs` (doc and message: calls, not only
    writes, are refused)
  - `docs/reference/agent-memory-server.md` (lines ~422-423 registration paragraph; error-shape
    rows `project-not-registered` ~1132 and `project-retired` ~1133)
  - `docs/reference/logging-event-ids.md` (row 433 text only; no count change)
  - tests: `tests/AiRaccoon.Tests/Unit/Projects/ProjectRegistrationGuardTests.cs`,
    `tests/AiRaccoon.Tests/Unit/Mcp/ToolGateRetiredIdTests.cs`,
    new `tests/AiRaccoon.Tests/Unit/Mcp/ReadRegistrationTests.cs` (Unit/Fast),
    `tests/AiRaccoon.Tests/Integration/Mcp/ToolRefusalsTests.cs`
- acceptance criteria:
  1. A Read under an id that is unregistered and holds no rows throws
     `UnregisteredProjectException`, migrated bank or not, guid or not.
  2. A Read never calls `IProjectRegistry.RegisterAsync`, not even on an unmigrated bank.
  3. A Read under an unregistered id the bank holds rows for passes and logs 433 once.
  4. Writes behave as before, including auto-registration on an unmigrated bank.
  5. A Read under a dropped id throws `RetiredProjectException` once migrated.
  6. Still working: `project_id_token_get`, `memory_promotion_list allProjects=true`, and
     `memory_performance projectId="__self_metrics__"`. A blank id resolved from the cwd passes
     only if registered or row-holding.
  7. `memory_search` under `__self_metrics__` is refused, which proves the allow-list is
     tool-local.
  8. `memory_share_extract mode=propose` under an unregistered id is refused and writes no
     `promotion_queue` row.
  9. A refused read over the real server leaves no `metrics` row and no `search_quality` row under
     the refused id. A row tagged `refused` exists for the same call (positive control).
  10. Message 433 names `ai-raccoon project id register` and `ai-raccoon repair project-ids --map
      <file> --apply`, and never `project id convert`. Register row 433 matches.
  11. `git grep -n -e "reads pass through" -e "Reads are allowed unconditionally" -e "reads are never refused" -e "project id convert" -- src docs/reference`
      prints nothing.
- gate:
  ```
  dotnet exec $T --filter-class AiRaccoon.Tests.Unit.Projects.ProjectRegistrationGuardTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.ToolGateRetiredIdTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.ReadRegistrationTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.ToolGateCwdDefaultTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.PerformanceToolsTests \
    --filter-class AiRaccoon.Tests.Integration.Mcp.ToolRefusalsTests \
    --filter-class AiRaccoon.Tests.Integration.Projects.OrphanVerbatimRefusalTests \
    --filter-class AiRaccoon.Tests.Unit.Observability.LoggerMessageEventIdTests
  ```
- tests (write first, watch red):

| Test | Failure mode it targets | Mutation that must turn it red |
|---|---|---|
| `ProjectRegistrationGuardTests.Read_UnregisteredGuidWithNoRows_IsRefused` (replaces `AReadRequirement_IsNeverRefused`) | the Read early return survives | restore `if (requirement == Read) return;` |
| `...Read_UnregisteredGuid_AfterMigration_IsRefused` (replaces `AReadRequirement_AfterMigration_IsNeverRefused`) | same, on a migrated bank | same |
| `...Read_UnregisteredRawText_BeforeMigration_IsRefusedAndRegistersNothing` | reads inherit the write path's auto-registration | drop the `requirement != Read` term from the auto-register condition |
| `...Read_LegacyIdWithRows_PassesAndWarnsOnce` | the legacy exemption is lost for reads | move the `HasRowsAsync` branch under a write-only condition |
| `...Read_RegisteredId_PassesSilently` | over-refusal | negate the `IsRegisteredAsync` check |
| `...LegacyWarning_NamesTheRegisterVerbAndTheRepairMap` (FakeLogger) | the warning points at a verb that does not exist | revert the message to `project id convert` |
| existing `AnUnregisteredRawTextId_BeforeMigration_IsAutoRegistered` (kept) | writes lose pre-migration compatibility | make auto-registration read-and-write |
| `ToolGateRetiredIdTests.RequireAsync_ReadUnderDroppedId_IsRefusedAsRetired` (replaces `..._PassesThrough`) | D2 not applied | restore `requirement is not AccessRequirement.Read &&` |
| `ReadRegistrationTests.Performance_SelfMetricsSentinel_PassesWithAnEmptyRegistry` | the sentinel is refused after D1 | delete the `PerformanceTools` branch |
| `ReadRegistrationTests.Performance_UnregisteredGuid_IsRefused` | the allow-list is wider than the sentinel (prefix or any id) | compare with `StartsWith("__")`, or skip the gate for any id |
| `ReadRegistrationTests.Search_UnderSelfMetricsSentinel_IsRefused` | the allow-list moved into the guard | allow-list the sentinel in `ProjectRegistrationGuard` |
| `ReadRegistrationTests.PromotionList_AllProjects_PassesWithAnEmptyRegistry` | the allProjects branch is gated by accident | route allProjects through `RequireAsync` |
| `ReadRegistrationTests.ShareExtractPropose_UnregisteredId_IsRefusedAndQueuesNothing` | propose persists queue rows under an unknown id | gate propose with a requirement the guard skips (restore the Read early return) |
| `ReadRegistrationTests.BlankId_ResolvedToUnregisteredId_IsRefusedOnRead` | cwd-resolved ids bypass registration | return the resolved id before `registration.EnsureAsync` |
| `ReadRegistrationTests.BlankId_ResolvedToRegisteredId_Passes` | over-refusal of the cwd path | refuse every resolved id |
| `ToolRefusalsTests.RefusedRead_LeavesNoMetricsOrQualityRowsUnderTheId` ([RetryFact], real server) | the refused id still lands in `metrics` or `search_quality` (telemetry tags the raw id, or quality is recorded before the gate) | pass `_bankProjectId` instead of `metricProjectId` in `ToolExecutionActivity.RecordError`; or call `RecordSearchSafeAsync` before `gate.RequireAsync` |
| `ToolRefusalsTests.ReadUnderDroppedId_OverTheWire_IsProjectRetired` | D2 holds in the unit but not end to end | same as the D2 unit mutation |

  `OrphanVerbatimRefusalTests.OrphanRead_Passthrough` stays green unchanged. It now pins the
  legacy exemption on reads: the loser id writes rows first, so the read passes on rows. Run it
  to prove it.

  The metrics assertion in `RefusedRead_...` must read rows after a flush. Find the existing seam
  (`TelemetryServerHost`, or a captured `IMeasurementRecorder`) rather than sleeping. The
  positive control (`refused` row present) keeps it from passing on an empty table.

### A2: Seed registrations in tests that read before they write

- depends_on: none (seeding is behaviour-neutral under the old rule, so it runs beside A)
- effort: medium
- files and action (seed before the first call; for a shared-fixture class, seed in the fixture so
  test order cannot decide the outcome):

| File | Id read before any write | Action |
|---|---|---|
| `tests/AiRaccoon.Tests/E2E/McpServerLaunchArgsE2ETests.cs` (:58, :124) | `acme` | seed with `TelemetryServerHost.SeedProjectRegistrationAsync(dataRoot, "acme")` before launch |
| `tests/AiRaccoon.Tests/E2E/McpTokenGateE2ETests.cs` (:89) | `acme` | seed |
| `tests/AiRaccoon.Tests/E2E/OtlpMetricExportE2ETests.cs` (:70) | `acme` | seed |
| `tests/AiRaccoon.Tests/E2E/OtlpTraceExportE2ETests.cs` (:75, :102, :141, :183) | `acme` | seed |
| `tests/AiRaccoon.Tests/E2E/ProxySpawnedBackendE2ETests.cs` (:102, :125) | `acme` | seed |
| `tests/AiRaccoon.Tests/E2E/McpServerToolSurfaceE2ETests.cs` (:65 `memory_list`) | `surface-test` | seed in the fixture |
| `tests/AiRaccoon.Tests/E2E/McpServerE2ETests.cs` (shared server; several tests read `acme` first) | `acme` | seed in the fixture |
| `tests/AiRaccoon.Tests/Integration/ProxyForwardTests.cs` (:183, :188 `memory_list` must not error) | `reconnect` | seed in `InitializeAsync` |
| `tests/AiRaccoon.Tests/Integration/Setup/McpServerSetupHostTests.cs` (:296) | `acme` | seed |
| `tests/AiRaccoon.Tests/Integration/Setup/Serve/NodeRunnerTests.cs` (:72) | `acme` | seed |
| `tests/AiRaccoon.Tests/Integration/Observability/OtlpExportTests.cs` (:133 `tools.Stats`) | `wp4-probe` | `host.Services.GetRequiredService<IProjectRegistry>().RegisterAsync(...)` before the call |
| `tests/AiRaccoon.Tests/Integration/Setup/QuietLoggingTests.cs` (:128 `tools.Stats`) | `quiet-unwritable-probe` | same as above |

  Verify, change only if red under A: `E2E/ModelMigrationCrashRecoveryE2ETests.cs` (writes `acme`
  before :513), `E2E/BackendLaunchIdentityProofE2ETests.cs` (writes `flow` first),
  `E2E/CwdDefaultProjectIdE2ETests.cs` (writes `laneA` first),
  `Integration/Mcp/UnmappedExceptionDiagnosticsTests.cs` (corrupt bank: it fails at the migration
  check before the guard), `Integration/Observability/ToolTelemetryCoverageTests.cs` (empty args,
  and every tool is expected to refuse), `Integration/Projects/SingleProjectIdE2E.cs` (reads after
  the repair registers the winner).

- acceptance criteria: every listed class is green on the old guard and, at J, on the new one.
  No class is flipped to expect a refusal. Each of these tests is about something other than
  registration.
- gate:
  ```
  dotnet exec $T --filter-namespace AiRaccoon.Tests.E2E
  dotnet exec $T --filter-class AiRaccoon.Tests.Integration.ProxyForwardTests \
    --filter-class AiRaccoon.Tests.Integration.Setup.McpServerSetupHostTests \
    --filter-class AiRaccoon.Tests.Integration.Setup.Serve.NodeRunnerTests \
    --filter-class AiRaccoon.Tests.Integration.Observability.OtlpExportTests \
    --filter-class AiRaccoon.Tests.Integration.Setup.QuietLoggingTests
  ```
- tests: no new tests. The mutation that proves the seeding is load-bearing is step A itself.
  At J, comment out one seed line (say `OtlpTraceExportE2ETests`), watch it go red under A's
  guard, then restore it.

### D: The repair loop waits for the server's finish stamp

- depends_on: none
- effort: medium
- files:
  - `src/AiRaccoon.Core/Projects/ProjectIdCensusReport.cs` (`bool RepairOpen = false`, last optional parameter)
  - `src/AiRaccoon.Infrastructure/Sqlite/SqliteRepairStore.cs` (`ReportProjectIdsAsync` returns
    `report with { RepairOpen = <HasOpenRepairRequest for project-ids> > 0 }`)
  - `src/AiRaccoon/Setup/Cli/Commands/ProjectIdsRepairCommands.cs` (after commit, `RunPassAsync`
    polls one `PollInterval` at a time until `RepairOpen` is false or `TotalBudget` is spent.
    `TryStopAsync` never reports settled, converged or P3 while `RepairOpen`. A budget spent with
    the request still open ends `RepairStuck` (37), with its own message: "the server has not
    finished the request yet; check the server log for the job receipt, then re-run")
  - tests: `tests/AiRaccoon.Tests/Unit/Setup/Cli/ProjectIdsRepairLoopTests.cs`,
    `tests/AiRaccoon.Tests/Integration/Storage/SqliteRepairStoreTests.cs`,
    `tests/AiRaccoon.Tests/Integration/Setup/RepairEndpointTests.cs`
- acceptance criteria:
  1. `--apply` prints no `summary — converged`, `pinned-only` or `P3 armed` line until a census
     read reports `RepairOpen == false` after the CLI's last commit.
  2. With the request still open when the budget runs out, the exit is 37 and the message says
     the server has not finished.
  3. `--queue-only` is unchanged.
  4. `ProjectIdsRepairJob.RunAsync` is untouched: fold, chunk-index, persist the aliases, reload,
     then the finish stamp.
  5. The census stays SELECT-only (`Collect_RunsUnderQueryOnly_ProvingZeroBankWrites` stays green).
- gate:
  ```
  dotnet exec $T --filter-class AiRaccoon.Tests.Unit.Setup.Cli.ProjectIdsRepairLoopTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.Cli.ProjectIdsRepairTranscriptTests \
    --filter-class AiRaccoon.Tests.Integration.Storage.SqliteRepairStoreTests \
    --filter-class AiRaccoon.Tests.Integration.Setup.RepairEndpointTests \
    --filter-class AiRaccoon.Tests.Integration.Maintenance.ProjectIdsRepairJobTests
  ```
- tests:

| Test | Failure mode | Mutation |
|---|---|---|
| `ProjectIdsRepairLoopTests.Apply_DoesNotReportSettledWhileTheRequestIsOpen` (fake store: census settled but `RepairOpen` true for 2 reads, then false) | the CLI declares P3 before `finished_at` (the measured 10:47Z vs 10:50:32Z gap) | delete the `RepairOpen` check in `TryStopAsync` |
| `...Apply_KeepsPollingUntilTheRequestCloses` (asserts the report-read count) | one poll then stop | replace the poll loop with a single delay |
| `...Apply_BudgetSpentWithRequestOpen_ExitsStuckNamingTheServer` | an open request is reported as converged when time runs out | return `Success` on budget exhaustion |
| `SqliteRepairStoreTests.ReportProjectIds_RepairOpen_TracksTheRequestRow` (request, then stamp `FinishRepairRequest`) | the flag is not wired to the table | hard-code `RepairOpen = false` |
| `...ReportProjectIds_RepairOpen_IgnoresOtherKinds` (open chunk-index request only) | the flag reads any open request | drop the `kind` parameter |
| `RepairEndpointTests.ProjectIdsReport_CarriesRepairOpenToTheClient` (`ServerSettingsStore` round trip) | the property is lost on the wire | rename the record parameter on one side only |

### E: Quality rows count as telemetry for the telemetry-only pin

- depends_on: none
- effort: low
- files:
  - `src/AiRaccoon.Core/Projects/ProjectIdsFoldPlan.cs`: the unmapped and unregistered branch
    (~line 166) pins telemetry-only when the row owns only metrics, noise or quality rows
    (`EntryTotal == 0 && AttachmentCount == QualityRows`, behind a private `OwnsOnlyTelemetry(row)`).
    `ProjectIdCensusRow.AttachmentCount` is unchanged, so `IsRetireEligible` is unchanged.
  - tests: `tests/AiRaccoon.Tests/Unit/Projects/ProjectIdsFoldPlanTests.cs`,
    `tests/AiRaccoon.Tests/Unit/Setup/Cli/ProjectIdsRepairTranscriptTests.cs`
- acceptance criteria:
  1. An unmapped, unregistered id owning only metrics plus search_quality rows (the measured
     `d42bd180` shape: 843 + 75) pins telemetry-only and is not in `Unresolved`.
  2. The same id with one more non-telemetry attachment (a watch, a queue row) stays `Unresolved`.
  3. Mapped folds still move quality rows. A dropped id with only quality rows is still dropped.
  4. A registered id with zero entries and quality rows is still not retire-eligible.
  5. A census of only such ids ends `pinned-only` (exit 0), not `attention needed` (39).
- gate:
  ```
  dotnet exec $T --filter-class AiRaccoon.Tests.Unit.Projects.ProjectIdsFoldPlanTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.Cli.ProjectIdsRepairTranscriptTests \
    --filter-class AiRaccoon.Tests.Integration.Projects.ProjectIdsRepairTelemetryWorkspacesTests
  ```
- tests:

| Test | Failure mode | Mutation |
|---|---|---|
| `FromCensus_UnmappedUnregistered_MetricsAndQualityOnly_PinsTelemetryOnly` | quality rows block the pin | revert to `AttachmentCount == 0` |
| `FromCensus_UnmappedUnregistered_QualityOnly_PinsTelemetryOnly` | the pin requires metrics or noise to exist | keep `(MetricsRows > 0 \|\| NoiseRows > 0)` as a precondition |
| `FromCensus_UnmappedUnregistered_QualityPlusWatch_StaysUnresolved` | the pin swallows real attachments | drop the attachment term entirely |
| `FromCensus_DroppedId_QualityOnly_IsStillDropped` | the change leaks into the drop path | apply the telemetry check before the drop branch |
| `FromCensus_RegisteredEmpty_WithQualityRows_IsNotRetireEligible` | someone "fixes" `AttachmentCount` instead | subtract `QualityRows` inside `AttachmentCount` |
| `Transcript_TelemetryOnlyIdsWithQualityRows_EndsPinnedOnly` | the CLI still says attention needed | revert the plan change |

### F: Grade and follow-through touch only the caller's project's row

- depends_on: none
- effort: low
- files:
  - `src/AiRaccoon.Core/SearchQuality/ISearchQualityService.cs`: `RecordFollowThroughAsync(string
    projectId, string correlationId, string filePath, int? servedRank, ct)` and
    `RecordGradeAsync(...)` return `Task<bool>`. Doc: projectId is a predicate.
  - `src/AiRaccoon.Infrastructure/Sqlite/SqliteSearchQualityService.cs`: the follow-through SELECT
    and UPDATE, and the grade UPDATE, add `AND project_id = @ProjectId`. Return
    `rowsAffected > 0` (follow-through returns false when the SELECT finds no row).
  - `src/AiRaccoon/Tools/QualityTools.cs`: pass `canonical` to follow-through and return
    `Recorded = <bool>`. The descriptions say the row must belong to this project.
  - tests: `tests/AiRaccoon.Tests/Integration/SearchQualityServiceTests.cs`,
    `tests/AiRaccoon.Tests/Integration/SearchQualityResultFeaturesTests.cs`,
    `tests/AiRaccoon.Tests/Integration/SearchSignalPreservationStageOneTests.cs`,
    `tests/AiRaccoon.Tests/Unit/Mcp/QualityToolsTests.cs`,
    `tests/AiRaccoon.Tests/TestData.cs` (fake at :735),
    `tests/AiRaccoon.Tests/Unit/Mcp/MemorySearchKindToolTests.cs` (fake at :618)
- acceptance criteria:
  1. A grade or follow-through under project B leaves project A's row unchanged, and the tool
     answers `recorded: false`.
  2. Under the row's own project, both update it and answer `recorded: true`.
  3. The tool passes the gate's canonical (alias-folded) id, so a loser-spelled call still grades
     the row that the search recorded under the winner.
  4. Every existing call site passes the row's real project. Each one is checked against its
     `RecordSearchAsync` project, so none goes silently vacuous.
- gate:
  ```
  dotnet exec $T --filter-class AiRaccoon.Tests.Integration.SearchQualityServiceTests \
    --filter-class AiRaccoon.Tests.Integration.SearchQualityResultFeaturesTests \
    --filter-class AiRaccoon.Tests.Integration.SearchSignalPreservationStageOneTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.QualityToolsTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.MemorySearchKindToolTests
  ```
- tests:

| Test | Failure mode | Mutation |
|---|---|---|
| `RecordGrade_UnderAnotherProject_LeavesTheRowUntouched` (deliberate flip of `RecordGrade_CorrelationIdOnlyKeying_ProjectIdNotAPredicate`) | one project grades another's row | drop `AND project_id = @ProjectId` from the grade UPDATE |
| `RecordFollowThrough_UnderAnotherProject_LeavesTheRowUntouched` | same, for follow-through | drop the predicate from the UPDATE (the SELECT alone is not enough) |
| `RecordGrade_UnderItsOwnProject_UpdatesTheRow` | the predicate binds the wrong value and nothing ever updates | bind `@ProjectId` to the correlation id |
| `RecordFollowThrough_UnderItsOwnProject_AppendsThePath` | same | same |
| `RecordGrade_ReturnsFalseWhenNoRowMatched` | `recorded: true` stays a lie | `return true;` |
| `QualityToolsTests.FollowThrough_PassesTheGateCanonicalId` (gate folds `Old` to `new`) | the tool passes the raw argument | pass `projectId` instead of `canonical` |

### G: ADR-0127 and the ADR-0089 amendment

- depends_on: none
- effort: low
- files: new `docs/adr/0127-reads-need-a-registered-project-id.md` (Nygard shape: Context,
  Decision, Consequences positive/negative/neutral, Alternatives), `docs/adr/0089-the-project-id-is-a-guidv7-and-that-is-not-access-control.md`
  (an amendment note at the head of decisions 3 and 6), `docs/adr/README.md` (0127 row, and an
  "Amended 2026-10-04 by ADR-0127" clause on the 0089 row)
- acceptance criteria. ADR-0127 states:
  1. The read rule (decision 1 above), the D2 refusal, the three allow-listed shapes and the cwd
     path.
  2. That settings writers under per-project keys apply the same test (`*` exempt).
  3. That ADR-0089 decision 6's `project id generate` / `convert` are replaced by `project id
     register|get|check`, with `project_id_token_get` and `repair project-ids --map` covering
     what the other two were for.
  4. `project_id_get` and the lookup-before-mint flows for ai-badger and for ai-raccoon alone.
  5. Exit codes 18 and 19, with the reason they share the `Usage` decade.
  6. Consequences: a breaking change for clients that read under unregistered ids (1.57.0); one
     registry primary-key SELECT added to every read; the legacy row exemption keeps old banks
     readable.
  7. Alternatives rejected: auto-register on read (it recreates the accident ADR-0089 removed);
     keep reads open and drop only their telemetry (unregistered reads would still succeed, and
     the next persisted side effect would leak again); a separate read-only registry check per
     tool (the logic would drift across tools).
- gate:
  ```
  dotnet exec $T --filter-class AiRaccoon.Tests.Unit.Docs.AdrIndexTests
  ```
- tests: none new. `AdrIndexTests` must go red with 0127 on disk and no README row, then green
  with the row added (prove it once).

### C: Settings writers refuse unregistered ids

- depends_on: A (the Read test it reuses must already refuse)
- effort: medium
- files:
  - new `src/AiRaccoon.Core/Projects/ProjectSettingsKeys.cs` (pure: `TryGetProjectId(key, out id)`,
    moved out of `ProjectIdCensus.TryAttributeSetting`; same prefixes, and `global` excluded)
  - `src/AiRaccoon.Infrastructure/Sqlite/ProjectIdCensus.cs` (calls the Core function; its
    private copy is deleted)
  - `src/AiRaccoon/Settings/SettingsEndpoint.cs` (PUT: an owner other than `*` is canonicalized
    and checked with `IProjectRegistrationGuard.EnsureAsync(..., AccessRequirement.Read)`.
    `UnregisteredProjectException` returns 409 with the message as `text/plain`. No new log line)
  - `src/AiRaccoon/Settings/ServerSettingsStore.cs` (PUT 409 throws `ProjectRefusedException(body)`,
    declared beside the three existing settings exceptions)
  - `src/AiRaccoon/Setup/Cli/Commands/ConfigCommands.cs` (catch `ProjectRefusedException`: stderr
    gets the message as-is, return `ErrorCode.Usage.ProjectUnknown`)
  - `src/AiRaccoon/ErrorCode.cs` (`Usage.ProjectUnknown = 18`)
  - `docs/adr/0107-categorized-two-digit-exit-codes.md` (row 18 in the `Usage` table)
  - `docs/reference/cli-reference.md` (exit-code row 18; one sentence under `settings` on the
    registration requirement)
  - tests: new `tests/AiRaccoon.Tests/Unit/Projects/ProjectSettingsKeysTests.cs` (Unit/Fast),
    `tests/AiRaccoon.Tests/Integration/Setup/SettingsEndpointTests.cs`,
    `tests/AiRaccoon.Tests/Unit/Setup/ConfigCommandsWatchTests.cs` or
    `ConfigCommandsAccessModelTests.cs` (one CLI exit-code test, whichever already fakes the
    store's throw)
- acceptance criteria:
  1. PUT of `access.mode.project:<id>`, `watch.enabled.<id>`, `watch.concurrency.<id>`,
     `ingest.scope.<id>` (and legacy `watch.scope.<id>`) under an unregistered id with no rows is
     409, and the bank row is not written.
  2. A registered id or a row-holding id writes as before. `*` (global keys, and
     `access.mode.project:*` if sent directly) writes as before. Non-project keys are never checked.
  3. The CLI verbs exit 18 with the refusal text on stderr. `settings watch enable * true` and
     `settings ingest scope add * <path>` still succeed.
  4. The census attributes settings keys exactly as before: `ProjectIdCensus` tests unchanged and
     green.
  5. A rejected PUT never registers the id. (The Read test is used precisely because it does
     not auto-register.)
  6. Deliberate consequence: `settings ingest scope remove <unregistered-id> <path>` is also a
     PUT and is also refused. The remedy is `repair project-ids` (the drop path deletes those
     keys). H records this in the changelog.
- gate:
  ```
  dotnet exec $T --filter-class AiRaccoon.Tests.Unit.Projects.ProjectSettingsKeysTests \
    --filter-class AiRaccoon.Tests.Integration.Setup.SettingsEndpointTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.ConfigCommandsWatchTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.ConfigCommandsAccessModelTests \
    --filter-class AiRaccoon.Tests.Unit.ErrorCodeTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.Serve.CliExitCodeMeaningTests \
    --filter-class AiRaccoon.Tests.Integration.Projects.ProjectIdsRepairFoldCommittedTests \
    --filter-class AiRaccoon.Tests.Unit.RetrySurfaceGateTests
  ```
- tests:

| Test | Failure mode | Mutation |
|---|---|---|
| `ProjectSettingsKeysTests.TryGetProjectId_ReadsEachPerProjectPrefix` (theory over the five prefixes) | a prefix is missed and its writer goes ungated | delete one prefix branch |
| `...TryGetProjectId_GlobalKeys_AreNotProjectKeys` | `ingest.scope.global` is treated as project "global" and refused | drop the `!= "global"` term |
| `SettingsEndpointTests.Put_ProjectKeyForUnregisteredId_Is409AndWritesNothing` ([RetryTheory] over the four owner-named prefixes) | the HTTP writer accepts any id | remove the endpoint check |
| `...Put_ProjectKeyForRegisteredId_Writes` | over-refusal | invert the guard call |
| `...Put_ProjectKeyForRowHoldingLegacyId_Writes` | the legacy exemption is lost on settings | call `IsRegisteredAsync` alone instead of the guard |
| `...Put_WildcardOwner_Writes` | `*` refused | remove the `*` exemption |
| `...Put_RefusedOnUnmigratedBank_RegistersNothing` | the check auto-registers a raw-text id | pass `AccessRequirement.Write` to the guard |
| `...ServerSettingsStore_SetSetting_On409_ThrowsProjectRefusedWithTheBody` | the CLI shows a bare HTTP error | let 409 fall through to `EnsureSuccessStatusCode` |
| `ConfigCommands...AccessSet_UnregisteredId_Exits18WithTheRefusal` | wrong exit code (17 or 92) | map the exception to `RequestRejected` |

### B1: Server side: project directory, `/projects` endpoint and `project_id_get`

- depends_on: A (shares `agent-memory-server.md`, `logging-event-ids.md` and the guard test fakes)
- effort: high
- files:
  - `src/AiRaccoon.Core/Projects/IProjectRegistry.cs` (+`FindByNameAsync(string name)`)
  - `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.Projects.cs` (+implementation, SELECT-only)
  - `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs` (+`SelectProjectIdsByName`: `SELECT id FROM
    projects WHERE name = @name ORDER BY id`, BINARY collation, so the match is exact and
    case-sensitive)
  - new `src/AiRaccoon.Core/Projects/IProjectDirectory.cs`, plus records and enums
    `ProjectRegistration`, `ProjectRegistrationOutcome {Registered, AlreadyRegistered, Retired}`,
    `ProjectIdCheck`, `ProjectIdStatus {Known, Unknown, Retired}`
  - new `src/AiRaccoon.Core/Projects/ProjectNameMatch.cs` (pure `Single(name, ids)`),
    `ProjectNotFoundException.cs`, `ProjectNameAmbiguousException.cs`
  - new `src/AiRaccoon/Projects/ProjectDirectory.cs`. Register: canonicalize, then fold and check
    for a drop the same migration-gated way `ToolGate` does. A retired id returns `Retired`. A
    registered folded id returns `AlreadyRegistered`. Anything else is registered under the
    folded id with the given name (first name wins), returns `Registered`, and logs EventId
    **694** at Information. Check: `Known` if the canonical id is registered, or if it folds to
    an id that is registered or holds rows. `Retired` if dropped. Otherwise `Unknown`.
  - new `src/AiRaccoon/Settings/ProjectsProtocol.cs`, `src/AiRaccoon/Settings/ProjectsEndpoint.cs`:
    `GET /projects?name=` gives `200 {ids}` (an empty list is 200, never 404, because 404 means
    `EndpointMissing`); `GET /projects/check?id=` gives `200 {status, projectId}`;
    `POST /projects {projectId, name}` gives `200 {outcome, projectId}`; a non-guid id is 400.
    Token-guarded like `/repair`.
  - `src/AiRaccoon/Setup/McpServerSetup.cs` (`MapProjects()` beside `MapRepair()`)
  - `src/AiRaccoon/Setup/AppRegistrations.cs` (`IProjectDirectory -> ProjectDirectory`, server
    default, overridden in the CLI graph by B2)
  - `src/AiRaccoon/Tools/ProjectTools.cs` (new `project_id_get(name)`: `RequireBankAvailableAsync`,
    `FindByNameAsync`, `ProjectNameMatch.Single`, returns `{projectId}`. Takes no projectId and
    never mints. `project_id_token_get`'s description: "Call project_id_get first; mint only when
    it finds nothing.")
  - `src/AiRaccoon/Tools/ToolRefusals.cs` (`ProjectNotFoundException -> project-not-found`,
    `ProjectNameAmbiguousException -> project-name-ambiguous`; the ambiguous message lists the ids)
  - `src/AiRaccoon/Observability/ToolTelemetry.cs` (projection `project_id_get -> NoProjectId`)
  - `docs/reference/agent-memory-server.md` (`## Tools (29)` becomes `(30)`, "1 project tool"
    becomes 2, a tool-table row, the token_get row text, two error-shape rows)
  - `docs/reference/logging-event-ids.md` (count 217 becomes 218, row 694)
  - tests: `tests/AiRaccoon.Tests/Unit/Projects/ProjectRegistrationGuardTests.cs` and
    `tests/AiRaccoon.Tests/Unit/Mcp/ProjectTokenToolTests.cs` (registry fakes gain the method; so
    does any fake A added in `ReadRegistrationTests.cs`), new
    `tests/AiRaccoon.Tests/Unit/Projects/ProjectDirectoryTests.cs` (Unit/Fast), new
    `tests/AiRaccoon.Tests/Unit/Projects/ProjectNameMatchTests.cs` (Unit/Fast), new
    `tests/AiRaccoon.Tests/Unit/Mcp/ProjectIdGetToolTests.cs` (Unit/Fast), new
    `tests/AiRaccoon.Tests/Integration/Projects/ProjectRegistryFindByNameTests.cs`, new
    `tests/AiRaccoon.Tests/Integration/Setup/ProjectsEndpointTests.cs`,
    `tests/AiRaccoon.Tests/Unit/Mcp/McpToolContractTests.cs` (contract string)
- acceptance criteria:
  1. `project_id_get("vinted-offers")` with one matching row returns `{projectId}`. With none it
     refuses `project-not-found`. With two it refuses `project-name-ambiguous` and lists both ids.
     It writes no `projects` row in any case.
  2. The match is exact and case-sensitive. A legacy raw-text id (`ai-badger`, auto-registered
     with name = id) is returned as stored.
  3. Telemetry tags `project_id_get` as `none`, never the name.
  4. The `/projects` routes refuse an anonymous caller (401) like every other route.
  5. Register on an alias of a registered id answers `AlreadyRegistered` with the winner and
     inserts no row. Register on a dropped id answers `Retired` and inserts no row.
  6. Check covers the cases in the contract table.
  7. `ToolInventoryTests` (docs heading and table), `McpToolContractTests`,
     `ToolTelemetryProjectionTests` and `LoggerMessageEventIdTests` are green.
- gate:
  ```
  dotnet exec $T --filter-class AiRaccoon.Tests.Unit.Projects.ProjectDirectoryTests \
    --filter-class AiRaccoon.Tests.Unit.Projects.ProjectNameMatchTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.ProjectIdGetToolTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.ProjectTokenToolTests \
    --filter-class AiRaccoon.Tests.Unit.Projects.ProjectRegistrationGuardTests \
    --filter-class AiRaccoon.Tests.Integration.Projects.ProjectRegistryFindByNameTests \
    --filter-class AiRaccoon.Tests.Integration.Setup.ProjectsEndpointTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.McpToolContractTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.ToolInventoryTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.ToolRefusalsRemedyTests \
    --filter-class AiRaccoon.Tests.Integration.Observability.ToolTelemetryProjectionTests \
    --filter-class AiRaccoon.Tests.Integration.Observability.ToolTelemetryCoverageTests \
    --filter-class AiRaccoon.Tests.Unit.Observability.LoggerMessageEventIdTests \
    --filter-class AiRaccoon.Tests.Unit.RetrySurfaceGateTests
  ```
- tests:

| Test | Failure mode | Mutation |
|---|---|---|
| `ProjectNameMatchTests.Single_NoIds_ThrowsNotFound` / `..._TwoIds_ThrowsAmbiguousListingBoth` / `..._OneId_ReturnsIt` | wrong arity handling | `ids.FirstOrDefault()` without the count check |
| `ProjectRegistryFindByNameTests.FindByName_IsExactAndCaseSensitive` | `LIKE` or `COLLATE NOCASE` matching | use `LIKE @name` |
| `...FindByName_ReturnsLegacyRawTextIdAsStored` | the id is canonicalized or re-formatted on the way out | `ProjectId.Canonicalize` the result |
| `ProjectDirectoryTests.Register_UnknownGuid_RegistersWithTheName` | nothing is written | skip `RegisterAsync` |
| `...Register_AliasOfRegistered_IsAlreadyRegisteredUnderTheWinnerAndWritesNoRow` | the alias becomes a second project | register before folding |
| `...Register_DroppedId_IsRetiredAndWritesNoRow` | a retired id is resurrected | drop the `IsDropped` check |
| `...Register_SecondName_KeepsTheFirst` | the name is overwritten | `INSERT OR REPLACE` in `InsertProject` |
| `...Check_FoldsToRowHoldingWinner_IsKnown` / `..._Unregistered_IsUnknown` / `..._Dropped_IsRetired` | the wrong status for a contract row | collapse `Retired` into `Unknown`, or skip the fold |
| `...Register_LogsEventId694Once` | silent durable change | delete the log call |
| `ProjectIdGetToolTests.Get_NotFound_RefusesProjectNotFound_AndMintsNothing` | the tool mints on a miss | call `token_get`'s path on a miss |
| `...Get_TakesNoProjectId_AndPassesWithEmptyRegistryGate` | the tool is gated by registration | call `gate.RequireAsync` |
| `ProjectsEndpointTests.GetByName_NoMatch_Is200WithEmptyIds` ([RetryFact]) | a miss returns 404 and the CLI reports `EndpointMissing` | `Results.NotFound()` on an empty list |
| `...Post_NonGuid_Is400` / `...Check_NonGuid_Is400` | a raw-text id is registered over HTTP | drop the `Guid.TryParse` check |
| `...EveryRoute_RefusesWithoutTheToken` | an unguarded route | map outside the gated block |

### B2: CLI verbs `project id register | get | check`

- depends_on: B1, C (B2 shares `ServerSettingsStore.cs`, `ConfigCommands.cs`, `ErrorCode.cs`,
  ADR-0107 and `cli-reference.md` with C, and needs B1's protocol)
- effort: medium
- files:
  - `src/AiRaccoon/Settings/ServerSettingsStore.cs` and `LazyServerSettingsStore.cs` (implement
    `IProjectDirectory` over `ProjectsProtocol`)
  - `src/AiRaccoon/AppRunner.cs` (CLI graph: `services.AddSingleton<IProjectDirectory>(lazyServerStore)`)
  - `src/AiRaccoon/Setup/Cli/CliCommandTree.cs` (top-level `project` family, then `id`, then
    `register <guid> [--name]`, `get --name <name>` (required), `check <guid>`)
  - new `src/AiRaccoon/Setup/Cli/Commands/ProjectIdCommands.cs` (`Guid.TryParse` first, which
    fails with exit 10 before any backend is acquired; then one `IProjectDirectory` call, and the
    stdout, stderr and exit mapping from the contract table)
  - `src/AiRaccoon/Setup/Cli/Commands/CommandsRegistration.cs`, `ConfigCommands.cs` (three dispatch
    arms; `ProjectNotFoundException -> 18` and `ProjectNameAmbiguousException -> 19` handled in
    `ProjectIdCommands`, not in the shared catch)
  - `src/AiRaccoon/ErrorCode.cs` (`Usage.ProjectAmbiguous = 19`)
  - `docs/adr/0107-categorized-two-digit-exit-codes.md` (row 19)
  - `docs/reference/cli-reference.md` (three top-level verb rows; exit-code row 19)
  - tests: new `tests/AiRaccoon.Tests/Unit/Setup/Cli/ProjectIdCommandsTests.cs` (Unit/Fast; fake
    `IProjectDirectory`), new `tests/AiRaccoon.Tests/Integration/Setup/ProjectIdCliTests.cs`
    (real `ai-raccoon` process, [RetryFact]), `tests/AiRaccoon.Tests/Integration/Setup/CliBankWriteTests.cs`
    (`project id get` and `project id check` join the read-verb theory),
    `tests/AiRaccoon.Tests/Unit/Setup/Serve/AppRunnerSettingsRoutingTests.cs`,
    `tests/AiRaccoon.Tests/Unit/Setup/LazyServerSettingsStoreTests.cs`,
    `tests/AiRaccoon.Tests/Unit/Setup/Cli/CliCommandTreeTests.cs`
- acceptance criteria:
  1. Every row of the contract table holds over a real process against a real server.
  2. stdout carries exactly one line on success, `--quiet` included.
  3. No verb reads stdin.
  4. A non-guid exits 10 without starting or contacting a server.
  5. The CLI process never opens the bank: `AppRunnerSettingsRoutingTests` shows
     `IProjectDirectory` resolves to `LazyServerSettingsStore`, and `CliBankWriteTests` shows
     `get` and `check` commit nothing.
  6. An old server without `/projects` exits 57 (`Server.EndpointMissing`) through the shared path.
- gate:
  ```
  dotnet exec $T --filter-class AiRaccoon.Tests.Unit.Setup.Cli.ProjectIdCommandsTests \
    --filter-class AiRaccoon.Tests.Integration.Setup.ProjectIdCliTests \
    --filter-class AiRaccoon.Tests.Integration.Setup.CliBankWriteTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.Serve.AppRunnerSettingsRoutingTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.LazyServerSettingsStoreTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.Cli.CliCommandTreeTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.CliWriteOptOutsTests \
    --filter-class AiRaccoon.Tests.Unit.ErrorCodeTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.Serve.CliExitCodeMeaningTests \
    --filter-class AiRaccoon.Tests.Unit.RetrySurfaceGateTests
  ```
- tests:

| Test | Failure mode | Mutation |
|---|---|---|
| `ProjectIdCommandsTests.Register_NonGuid_Exits10WithoutTouchingTheDirectory` (fake throws if called) | the server is acquired for a usage error | validate after the call |
| `...Register_New_PrintsRegisteredLine` / `..._Existing_PrintsAlreadyRegisteredCanonical` | wrong stdout contract | swap the two strings |
| `...Register_Retired_Exits18_StdoutEmpty` | a retired id exits 0, or a line goes to stdout | map `Retired` to 0 |
| `...Get_One_PrintsBareId` / `..._None_Exits18` / `..._Several_Exits19_IdsOnStderrOnly` | the codes collapse, or the ids leak to stdout | write the ids to stdout; use 18 for both |
| `...Check_Known_Exits0` / `..._Unknown_Exits18_PrintsUnknown` / `..._Retired_Exits18_PrintsRetired` | the wrong status line | collapse retired into unknown |
| `...NoVerb_ReadsStdin` (a `StandardStreams` whose reader throws) | an interactive prompt sneaks in | add a `ReadLineAsync` |
| `ProjectIdCliTests.RegisterGetCheck_RoundTripOverARealServer` ([RetryFact]) | the protocol halves drift | rename a JSON field on one side |
| `...Register_Quiet_StdoutIsExactlyTheResultLine` | the backend disclosure or a log goes to stdout | write the disclosure to stdout |
| `AppRunnerSettingsRoutingTests.ProjectIdVerbs_ResolveTheServerBackedDirectory` | the CLI resolves `ProjectDirectory` and opens the bank | delete the `AppRunner` override line |
| `CliBankWriteTests.ReadCommand_CommitsNothingToTheBank` (+`project id get --name x`, `project id check <guid>`) | a read verb writes | make `check` call `RegisterAsync` |

### H: Release 1.57.0 and the follow-up issue

- depends_on: A, A2, B1, B2, C, D, E, F, G
- effort: low
- files: `VERSION` (1.56.1 becomes 1.57.0), new `docs/changelog/1.57.0-reads-need-a-registered-project.md`,
  `docs/changelog/README.md` (index line; the batching rule already exists, so no convention
  change), `docs/reference/breaking-changes.md` (a 1.57.0 entry at the top),
  `README.md` (Breaking changes: "The latest one is 1.57.0"; What's new: one line, features only:
  `project id register|get|check` and `project_id_get`), `docs/reference/whats-new-history.md`
  only if the README list rotates, and `docs/reference/agent-memory-server.md` (the
  `record_followthrough`/`record_grade` rows: `{recorded: bool}`, from F)
- acceptance criteria:
  1. The breaking-changes entry says what to do: reads and settings writes under an id the bank
     does not know are refused; register an existing id with `ai-raccoon project id register
     <id>`, or look it up with `project id get --name`. It links ADR-0127.
  2. The changelog lists A to F as user-visible changes, names PR #846, and notes that `scope
     remove` under an unregistered id is refused (C, item 6).
  3. A follow-up GitHub issue is filed for the two out-of-scope surfaces (sync pull merging rows
     under remote ids; watch ingestion for pre-existing `watches` rows), citing research section 4.
     The PR body links it.
- gate:
  ```
  dotnet exec $T --filter-class AiRaccoon.Tests.Unit.Docs.AdrIndexTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.ToolInventoryTests
  ```
  plus `cat VERSION` printing `1.57.0`, and `gh issue view <n>` for the follow-up.
- tests: none. This is docs and a version marker.

### J: Join

- depends_on: A, A2, B1, B2, C, D, E, F, G, H
- effort: medium
- acceptance criteria:
  1. `dotnet build AiRaccoon.slnx` has zero errors and zero new warnings.
  2. Every per-step gate above re-runs green on the merged tree, each with a non-zero `total:`.
  3. The read-before-write surfaces from A2, run together under A's guard:
     ```
     dotnet exec $T --filter-namespace AiRaccoon.Tests.E2E
     dotnet exec $T --filter-class AiRaccoon.Tests.Integration.ProxyForwardTests \
       --filter-class AiRaccoon.Tests.Integration.Setup.McpServerSetupHostTests \
       --filter-class AiRaccoon.Tests.Integration.Setup.Serve.NodeRunnerTests \
       --filter-class AiRaccoon.Tests.Integration.Observability.OtlpExportTests \
       --filter-class AiRaccoon.Tests.Integration.Setup.QuietLoggingTests \
       --filter-class AiRaccoon.Tests.Integration.Mcp.UnmappedExceptionDiagnosticsTests \
       --filter-class AiRaccoon.Tests.Integration.Observability.ToolTelemetryCoverageTests \
       --filter-class AiRaccoon.Tests.Integration.Projects.SingleProjectIdE2E \
       --filter-class AiRaccoon.Tests.Integration.Projects.OrphanVerbatimRefusalTests \
       --filter-class AiRaccoon.Tests.Integration.Projects.ProjectIdsConvergenceTests
     ```
     Then the seed-removal proof described in A2: one seed out goes red, and putting it back
     goes green.
  4. BDD runs once (`dotnet test --filter Category=bdd`). The three BDD contexts on
     `AllowingRegistrationGuard` and `FileWatcherSteps` (direct store, no HTTP) stay green
     unchanged.
  5. Docs gates: `AdrIndexTests`, `LoggerMessageEventIdTests` (218), `ToolInventoryTests` (30),
     `ErrorCodeTests`, `CliExitCodeMeaningTests`, `McpToolContractTests`.
  6. Repo-wide source gates: the NoConfigureAwait, NoHandRolledCrypto and EventId scanners run
     with the Unit namespace (`dotnet exec $T --filter-namespace AiRaccoon.Tests.Unit`).
  7. `git grep -n "AllowingRegistrationGuard" -- tests | wc -l` matches the count on `main`.
     The stub's users were not touched.
  8. Someone other than the authors runs `review-tests` on the test diff and gets a verdict.
- gate: the commands above. The pipeline runs the full suite on push.

## Context map (for the implementers)

- **Patterns to copy.** For a CLI verb served by the server: `IRepairStore` -> `SqliteRepairStore`
  (server) / `ServerSettingsStore` + `LazyServerSettingsStore` (CLI), `RepairProtocol` +
  `RepairEndpoint`, the `AppRunner` override block, and `ConfigCommands` dispatch plus its
  shared catches. For a gate-free tool branch: `PromotionTools` `allProjects`. For refusal
  prefixes: the `ToolRefusals` map. For seeding a registration in tests:
  `TelemetryServerHost.SeedProjectRegistrationAsync`.
- **Edit sequence.** Merge W1 lanes in any order. Merge A before B1 and C. Merge B1 and C before
  B2. H last, then J. A lane merges `main` and the task branch in (never rebases), and stages
  files by path.
- **Shared-file ownership.** These files have exactly one owner per wave:
  `agent-memory-server.md` (A, then B1, then H); `logging-event-ids.md` (A, then B1);
  `ServerSettingsStore.cs`, `ConfigCommands.cs`, `ErrorCode.cs`, ADR-0107 and `cli-reference.md`
  (C, then B2); `ProjectRegistrationGuardTests.cs` (A, then B1).

## Was this the simplest shape? What was cut

- **No new `ToolGate` API.** The sentinel is a three-line branch in `PerformanceTools`, the same
  shape as `allProjects`.
- **No `ShareTools` change.** D1 already refuses propose. Changing its access requirement would
  be a second, unasked-for decision.
- **No new repair endpoint, `IRepairStore` method or exit code.** One census flag, and the loop
  reuses 37 with its own message. The repair request keeps its kind-scoped identity. A stamp
  that lands on a re-posted request is harmless here, because the loop re-posts the same map
  and re-derives the census after every stamp.
- **No CLI-side registration check.** The server's `PUT /settings` is the one choke point for CLI
  and HTTP, so there is nothing to keep in sync.
- **No key rewriting on settings writes.** The id is checked as written (canonicalized). Folding
  a loser's key to the winner is the repair's job.
- **No registration cache on the read path.** Each read pays one primary-key SELECT (plus a
  rows probe for legacy ids). Writes already pay this. A cache would need invalidation on
  retire, and nothing measured asks for one.
- **Two new EventIds became one (694).** Lookups do not log, and a refused settings write does
  not log. The refusal reaches the caller, and the success path already logs 672.
- **One `IProjectDirectory` for both processes** instead of a server service plus a CLI client
  type.
- **`project id generate` / `convert` stay unbuilt.** `register` plus `project_id_token_get` and
  `repair --map` cover them. ADR-0127 says so.

## Open questions

None of these blocks a step that can start now. One could change B2's contract:

1. **Is a shared 18 for retired and not-found acceptable to the ai-badger session?** The
   coordinator asked for a distinct non-zero exit for a retired id under `register`. It is
   distinct within `register`, but numerically equal to `get`'s not-found and `check`'s unknown.
   Splitting them needs a third slot, and `Usage` has two. The only alternative is a code in a
   decade whose concept does not fit. B2 can start either way; only the constant would move.
