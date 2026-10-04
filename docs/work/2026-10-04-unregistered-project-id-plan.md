# Unregistered project ids: implementation plan (2026-10-04, revision 2)

Task `air-refuse-unregistered-project-id-tools`, draft PR #846. Inputs:

- the research record (`2026-10-04-unregistered-project-id-research.md`)
- the owner rulings D0-D5 (`2026-10-04-unregistered-project-id-feedback.md`)
- the plan-review rulings (`2026-10-04-unregistered-project-id-plan-review.md`)

Revision 2 folds every accepted MUST and SHOULD into the step it names. Where I verified a
reviewer claim against the source, the citation is inline. "Deviations" at the end lists the four
places where this plan departs from a ruling's wording, with the evidence.

## Session work items, in order

1. P (W0): `ProjectIdAliasMap.Apply`, the one fold-and-drop function.
2. READ (W1): A+A2, covering the read rule, retired-read refusal, ToolGate folding without the
   marker, the `McpServerFactory` seam, the seeding and the straggler run.
3. B1 (W1): the server side, meaning `IProjectDirectory`, `/projects`, `project_id_get`, exit
   constants 18/19 and their ADR-0107 and cli-reference rows.
4. SMALL (W1): three commits.
   - D: the repair loop waits for the finish stamp.
   - E: the telemetry-only pin counts quality rows as telemetry.
   - F: grade and follow-through are scoped to the project and report truthfully.
5. C (W2): settings writes resolve, refuse and rewrite project keys, and allow removals.
6. B2 (W2): CLI `project id register|get|check`, `BindCliToServer`, and the root-option parse fix if needed.
7. G+H (W3): ADR-0127, the ADR-0089 amendment, VERSION 1.57.0, changelog, breaking changes, README, follow-up issue.
8. J (coordinator): the merged-tree join, the cross-step chain, the docs gates, and `review-tests` by a non-author.

## Contract handed to ai-badger

| Verb and input | Exit | stdout (exactly one line) | stderr |
|---|---|---|---|
| `project id register <guid>`, unknown guid | 0 | `registered <id>` | empty under `--quiet` |
| `register`, already registered (guid or raw text, e.g. `ai-badger`) | 0 | `already registered <canonical>` | empty under `--quiet` |
| `register`, alias of a registered project | 0 | `already registered <winner>` (no row written) | empty under `--quiet` |
| `register`, an id that holds rows but has no registry row (guid or raw text) | 0 | `registered <id>` | empty under `--quiet` |
| `register`, unknown raw-text id | **10** | nothing | `not a guid` usage message |
| `register`, retired (dropped) id | **18** | nothing | the retired refusal |
| `register ""` (blank) | **10** | nothing | usage message; no server contacted |
| `project id get --name <n>`, one exact, case-sensitive match | 0 | the stored id with no prefix (`ai-badger` and other raw-text ids printed as stored) | empty under `--quiet` |
| `get`, no match | **18** | nothing | not-found message |
| `get`, several matches | **19** | nothing | ambiguous message listing every id |
| `get --name ""` | **10** | nothing | usage message; no server contacted |
| `project id check <id>`, registered (guid or raw text) | 0 | `known <canonical>` | empty under `--quiet` |
| `check`, folds to a registered or row-holding id, or holds rows itself | 0 | `known <resolved>` | empty under `--quiet` |
| `check`, unknown (guid or raw text) | **18** | `unknown <id>` | empty under `--quiet` |
| `check`, retired | **18** | `retired <id>` | empty under `--quiet` |
| `check ""` | **10** | nothing | usage message; no server contacted |
| any verb, server unreachable, unproven, token refused or older than `/projects` | shared path, unchanged (31, 50-59, 60-66; an old server is **57**) | nothing | shared path's message |
| `settings access set / watch enable\|disable / watch concurrency / ingest scope add` under an unknown or retired id | **18** | nothing | the refusal |
| `settings ingest scope remove` under any id (the new list is a subset of the stored list) | 0 | as today | as today |
| any settings writer under an alias | 0 | as today (the server writes the winner's key) | as today |

- **Argv:** `ai-raccoon [--quiet] project id register <id> [--name <repo>]`,
  `ai-raccoon [--quiet] project id get --name <repo>`, and `ai-raccoon [--quiet] project id check <id>`.
- **stdin:** no verb reads it.
- **stderr:** empty on success only under `--quiet`. Without it, stderr may carry the
  backend-acquire disclosure (EventId 687, `CliSettingsBackend`).
- **register's name:** the first non-null name wins, so a later `--name` fills a NULL name and
  never overwrites a set one.
- **Constants (B1):** `ErrorCode.Usage.ProjectUnknown = 18` and `ErrorCode.Usage.ProjectAmbiguous = 19`.
  `Usage` was chosen on fit: the caller passed a value that names no usable project. It is not the
  only decade with room. Port 45-49, Reach 67-69, Environment 89 and Internal 96-99 are free too
  (`src/AiRaccoon/ErrorCode.cs`).

Lookup-before-mint (ADR-0127):

- ai-badger, `.ai-badger/project-id` missing: run `project id get --name <repo>`.
  - 0: write the printed id.
  - 18: mint a guid locally, then `project id register <guid> --name <repo>`.
  - 19: stop and ask a human.
- ai-badger, file present: run `project id check <id>`.
  - 0: done.
  - 18 with `unknown`: `project id register <id> --name <repo>`.
  - 18 with `retired`: stop.
- ai-raccoon alone: call `project_id_get(name)`.
  - `project-not-found`: call `project_id_token_get(name)`.
  - `project-name-ambiguous`: ask the user.

## Design decisions

1. **One registration rule, in the real guard.** `ProjectRegistrationGuard.EnsureAsync` loses its
   Read early return. The rule, for every requirement:
   - registered: pass;
   - the bank holds rows: pass, with warning 433 once per id per process;
   - a write on an unmigrated bank: auto-register a non-guid;
   - anything else: refuse.

   Reads never register. The migration marker stays only in that auto-register branch.
2. **ToolGate folds and refuses dropped ids without the marker (R1-S2).**
   - Why: `MemorySql.RequestRepair` resets `finished_at` to NULL on every request
     (`MemorySql.cs:596-601`), and `SqliteProjectIdsMigrationGate` reads
     `finished_at IS NOT NULL` (`SqliteProjectIdsMigrationGate.cs:19-22`). Every re-repair
     therefore un-migrates the bank for the job's duration.
   - Safe on an unrepaired bank: `ProjectIdAliasMap.Default` is empty there.
   - Cost: ToolGate's `migrationGate` parameter becomes unread. That is CS9113, an error under
     `TreatWarningsAsErrors` (`Directory.Build.props:7`), so the parameter is removed from
     ToolGate and from the 35 files that construct it.
   - Two M1 ledger tests flip.
3. **One fold-and-drop function, `ProjectIdAliasMap.Apply(id) -> FoldedProjectId(ProjectId, Dropped)`**
   (pure, Core, step P). It canonicalizes, folds and reports a drop. ToolGate, `SqliteProjectDirectory`
   and `SettingsEndpoint` call it on `ProjectIdAliasMap.Default`. It takes no `migrated` flag
   (see Deviations).
4. **The `__self_metrics__` allow-list lives in `PerformanceTools`.** It is an exact, ordinal
   match taking the `PromotionTools` `allProjects` branch shape, and `__SELF_METRICS__` is refused.
5. **`memory_share_extract mode=propose` needs no code change.** Decision 1 refuses it. Step READ
   pins that with a test.
6. **Settings writes are resolved on the server.** `SettingsEndpoint` handles every project-keyed
   setting:
   - Owner: `ProjectSettingsKeys.TryGetProjectId`, Core, built on one prefix list that the census
     and `ProjectIdsRepair.SettingsKeysFor` share (R1-S6).
   - GET `?key=` and PUT both run the owner through `Apply` and use the resolved key, so CLI
     read-modify-write verbs read and write the winner's key (see Deviations).
   - PUT refuses an owner that is dropped (retired), or unknown to the guard's read test.
   - PUT allows a subset write of `ingest.scope.*`/`watch.scope.*` (a removal), checked by a pure
     `IngestScopeList.IsSubset(stored, proposed)`.
   - `*` and non-project keys pass untouched. A blank owner is 400. DELETE is unaffected.
   - Refusals are 409 with a plain-text body. The CLI exits 18.
7. **One directory interface for both processes.** `IProjectDirectory` (Core) has `RegisterAsync`,
   `FindByNameAsync` and `CheckAsync`.
   - Server: `SqliteProjectDirectory` (Infrastructure). It reads names with its own SELECT and
     registers through `IProjectRegistry`, so `IProjectRegistry` gains no member and no hand-written
     registry fake breaks.
   - CLI: `ServerSettingsStore`/`LazyServerSettingsStore` over `/projects`.
   - The MCP tool calls `IProjectDirectory` too.
8. **The repair census carries `RepairOpen`.** The CLI honours it only after its own commit.
   The job order is unchanged.
9. **`rowsAffected` decides grade and follow-through (R2-S9).** The predicated UPDATE's
   `rowsAffected` is the only source of `recorded`.
10. **Real-server suites never overlap (R3-M6).** At most one lane runs a spawn-a-server suite at
    a time, on `LoopbackPort.Reserve()` ports (never 7721) and a temp data root per test. A spawn
    suite is the `AiRaccoon.Tests.E2E` namespace, `CliBankWriteTests`, `ProjectIdCliTests`, or any
    class that launches an `ai-raccoon` OS process.

## Gate conventions

`dotnet test --filter` runs zero tests here, so every gate uses the built assembly. A run counts
only with a non-zero `total:`. `--filter-class` and `--filter-namespace` never share a call.

```
dotnet build AiRaccoon.slnx
T=tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests.dll
SOURCE_GATES="--filter-class AiRaccoon.Tests.Unit.Layering.NoConfigureAwaitTests \
  --filter-class AiRaccoon.Tests.Unit.Encryption.NoHandRolledCryptoTests \
  --filter-class AiRaccoon.Tests.Unit.Observability.LoggerMessageEventIdTests \
  --filter-class AiRaccoon.Tests.Unit.Layering.SqlHelperSourceGateTests \
  --filter-class AiRaccoon.Tests.Unit.Layering.LayeringRulesTests \
  --filter-class AiRaccoon.Tests.Unit.Layering.ToolMethodSizeTests \
  --filter-class AiRaccoon.Tests.Unit.CategoryGateCoverageTests \
  --filter-class AiRaccoon.Tests.Unit.SpeedGateCoverageTests \
  --filter-class AiRaccoon.Tests.Unit.Projects.ProjectIdAliasDefaultCollectionGateTests \
  --filter-class AiRaccoon.Tests.Unit.RetrySurfaceGateTests"
```

Every gate below ends with `dotnet exec $T $SOURCE_GATES`.

Test-class conventions:

- New Integration classes use `[RetryFact]`/`[RetryTheory]` and carry `Category=Integration`,
  `Speed=Slow`, never Nightly.
- New Unit classes carry `Category=Unit`, `Speed=Fast`.
- A class that touches `ProjectIdAliasMap.Default` joins `[Collection(ProjectIdAliasDefaultCollection.Name)]`
  and calls `ProjectIdAliasMap.ResetDefault()` on dispose.

Lanes stage by path and merge, never rebase.

## Waves and file ownership

- **W0:** P, a single commit on the task branch, before the W1 lanes cut.
- **W1:** three lanes, run in parallel.
  - READ is the only W1 lane running spawn suites.
  - SMALL merges before B1.
- **W2:** C and B2, run in parallel, once all three W1 lanes are merged.
  - B2 merges C at the end and re-runs C's gates.
  - B2's spawn suites run after READ's E2E run, never alongside it.
- **W3:** G+H.
- **J:** run by the coordinator.

Files touched by more than one lane (each lane edits only the hunk named):

| File | Owners in order | Hunks |
|---|---|---|
| `src/AiRaccoon/ErrorCode.cs` | SMALL (D: `RepairStuck` doc), then B1 (`Usage` 18/19) | far apart |
| `docs/adr/0107-…md` | SMALL (D: row 37), then B1 (rows 18-19) | far apart |
| `docs/reference/cli-reference.md` | SMALL (D: row 37), then B1 (exit rows 18-19), then C (settings sentence) and B2 (verb rows), then H | separate sections |
| `docs/reference/logging-event-ids.md` | READ (row 433 text), B1 (count line and row 694) | far apart |
| `docs/reference/agent-memory-server.md` | **B1 only in W1** (it also writes READ's doc changes), then SMALL F's two row edits move to H | B1 owns every W1 edit |
| `MemorySearchKindToolTests.cs`, `RefusedQueryRedactionTests.cs`, `SearchSignalPreservationStageOneTests.cs`, `TestData.cs` | READ (the `new ToolGate(` line), SMALL F (the quality fake) | distinct hunks; SMALL merges READ before pushing F |
| `src/AiRaccoon/Settings/ServerSettingsStore.cs`, `src/AiRaccoon/Setup/Cli/Commands/ConfigCommands.cs` | C (409 → `ProjectRefusedException`, catch → 18), B2 (directory client, dispatch arms) | distinct members |

---

### P: One fold-and-drop function

- depends_on: none
- effort: low
- files: `src/AiRaccoon.Core/Projects/ProjectIdAliasMap.cs` (`Apply(string projectId) -> FoldedProjectId`,
  a record in the same file), `tests/AiRaccoon.Tests/Unit/Projects/ProjectIdAliasMapTests.cs`
- acceptance criteria:
  1. `Apply` returns the canonical D-form winner for an alias in any guid spelling.
  2. It returns the id unchanged for a canonical, a typo or a raw-text id.
  3. It returns `Dropped = true` for a dropped id (the id itself, unfolded).
  4. It is pure: no I/O, and no state beyond the map instance.
- gate: `dotnet exec $T --filter-class AiRaccoon.Tests.Unit.Projects.ProjectIdAliasMapTests` then `dotnet exec $T $SOURCE_GATES`
- tests:

| Test | Failure mode | Mutation |
|---|---|---|
| `Apply_UppercaseBracedAlias_ReturnsCanonicalWinner` | spelling variants miss the fold | skip `ProjectId.Canonicalize` |
| `Apply_DroppedId_ReportsDroppedAndDoesNotFold` | a drop is folded or missed | return `Dropped = false` |
| `Apply_RawTextId_PassesThrough` | raw text is mangled | lowercase the input |

### READ (A+A2): reads need registration; retired reads refused; seeding

- depends_on: P
- effort: high
- files (src):
  - `src/AiRaccoon/Projects/ProjectRegistrationGuard.cs`
    - remove the Read early return; reads never auto-register; `Guard.IsNotNullOrWhiteSpace`
    - 433 text branches on guid vs raw text:
      - guid: `register it with 'ai-raccoon project id register <id>'`
      - raw text: `fold it into a registered project with 'ai-raccoon repair project-ids --map <file> --apply', or keep it with 'ai-raccoon project id register <id>'`
  - `src/AiRaccoon/Projects/IProjectRegistrationGuard.cs` (doc)
  - `src/AiRaccoon/Tools/ToolGate.cs`
    - drop the `IProjectIdsMigrationGate` parameter
    - `var folded = ProjectIdAliasMap.Default.Apply(projectId)`
    - throw `RetiredProjectException` when `folded.Dropped`, whatever the requirement
    - docs updated
  - `src/AiRaccoon/Tools/IToolGate.cs` (doc)
  - `src/AiRaccoon/Tools/PerformanceTools.cs` (sentinel branch)
  - `src/AiRaccoon.Core/Projects/UnregisteredProjectException.cs`: message names `project_id_get`,
    `project_id_token_get` and `'ai-raccoon project id register <id>'`
  - `src/AiRaccoon.Core/Projects/RetiredProjectException.cs`: calls, not only writes, are refused
  - `src/AiRaccoon/Setup/AppRegistrations.cs`: only if ToolGate is constructed explicitly there; it
    is resolved by type, so likely none
- files (tests, modified):
  - `tests/AiRaccoon.Tests/Unit/Projects/ProjectRegistrationGuardTests.cs`
  - `tests/AiRaccoon.Tests/Unit/Mcp/ToolGateRetiredIdTests.cs`
  - `tests/AiRaccoon.Tests/Unit/Mcp/ToolGateTests.cs`
  - `tests/AiRaccoon.Tests/Integration/Projects/OrphanVerbatimRefusalTests.cs`: fix the false
    rationale on `OrphanRead_Passthrough`, which passes because the write auto-registered
    `job-search-ai-assistant` (`:42`, `:76-78`); add the raw-SQL legacy test
- files (tests, new):
  - `tests/AiRaccoon.Tests/Unit/Mcp/ReadRegistrationTests.cs`: Unit/Fast; a real ToolGate and a
    real guard over an NSubstitute registry that is empty and throws on `RegisterAsync`
  - `tests/AiRaccoon.Tests/Integration/Mcp/UnregisteredIdRefusalTests.cs`: Slow; the derived theory
  - `tests/AiRaccoon.Tests/Integration/Mcp/RetiredIdReadRefusalTests.cs`: Slow, in the alias collection
  - `tests/AiRaccoon.Tests/Integration/Observability/RefusedReadTelemetryTests.cs`: Slow,
    `[Collection(ObservabilityCollection.Name)]`
- the ToolGate constructor, its 35 construction sites, each losing one argument and nothing else
  (`git grep -l "new ToolGate(" -- tests src`):
  - `BDD/CodeCorpusFeatureContext.cs`, `FileWatcherFeatureContext.cs`, `NativeMemorySteps.cs`
  - `GoldenMemorySearchResponseTests`, `CodeReindexJobTests`, `CodeGetToolTests`,
    `MemorySearchCodeIntegrationTests`, `MemorySearchRankingTests`, `SearchMetricsIsolationTests`
  - `CanonicalProjectIdReachesStorageTests`, `OrphanVerbatimRefusalTests`,
    `ProjectIdsConvergenceTests`, `SingleProjectIdE2E`
  - `SearchLimitTruncationTests`, `SearchSignalPreservationStageOneTests`, `SetTtlToolTests`,
    `ShareExtractScoringScopeTests`
  - `MemorySearchAbsoluteRelevanceTests`, `MemorySearchEvidenceEnvelopeTests`,
    `MemorySearchFloorTruncationTests`, `MemorySearchFusionSignalMetricsTests`,
    `MemorySearchKindToolTests`
  - `MemoryToolsAccessModeTests`, `MemoryToolsTests`, `PromotionToolsTests`, `ShareToolsTests`
  - `ToolGateCwdDefaultTests`, `ToolGateRetiredIdTests`, `ToolGateTests`
  - `WatchToolsAccessModeTests`, `WatchToolsRetiredIdTests`, `WatchToolsTests`
  - `McpExceptionPathInstrumentationTests`, `MemoryToolsInstrumentationTests`,
    `RefusedQueryRedactionTests`
  - `TestData.cs` if it constructs one
- seeding (R2-M1): `tests/AiRaccoon.Tests/E2E/McpServerFactory.cs` gains
  `public IReadOnlyList<string> Projects { get; init; } = []`. `CreateClientAsync` registers each
  id before the first client connects, beside `SeedGlobalAccessModeAsync`. Users:

| File | Id | Action |
|---|---|---|
| `E2E/McpServerE2ETests.cs` | `acme` | `new McpServerFactory { Projects = ["acme"] }` |
| `E2E/McpServerLaunchArgsE2ETests.cs` (`:58`, `:124`) | `acme` | factory `Projects` |
| `E2E/McpServerToolSurfaceE2ETests.cs` (`:65`) | `surface-test` | factory `Projects` |
| `Integration/ProxyForwardTests.cs` (`:46` and `ReacquireAsync` `:97`) | `reconnect` | both factories get `Projects` |
| `E2E/McpTokenGateE2ETests.cs` (`:89`) | `acme` | `TelemetryServerHost.SeedProjectRegistrationAsync` before launch |
| `E2E/ProxySpawnedBackendE2ETests.cs` (`:102`, `:125`) | `acme` | same |
| `Integration/Setup/McpServerSetupHostTests.cs` (`:296`) | `acme` | same |
| `Integration/Setup/Serve/NodeRunnerTests.cs` (`:72`) | `acme` | same |
| `Integration/Observability/OtlpExportTests.cs` (`:133`) | `wp4-probe` | `host.Services.GetRequiredService<IProjectRegistry>().RegisterAsync` |
| `Integration/Setup/QuietLoggingTests.cs` (`:128`) | `quiet-unwritable-probe` | same |

  No seeding is needed in `E2E/OtlpMetricExportE2ETests.cs` or `OtlpTraceExportE2ETests.cs`. Neither
  asserts `IsError` (verified: zero matches), and their span and export assertions hold for a
  refused call. They stay unchanged. Writer-first classes also stay unchanged (`CwdDefault…`,
  `BackendLaunchIdentityProof…`, `ModelMigrationCrashRecovery…`, `SingleProjectIdE2E`), as do
  `UnmappedExceptionDiagnosticsTests` (a corrupt bank fails first) and the two
  `ToolTelemetry*CoverageTests` (refusal is expected).
- straggler run: after the seeds, the lane runs the E2E and Integration namespaces once under
  the new guard. The guard is shared infrastructure. Any red read-before-write test is seeded
  and added to the table above.
- acceptance criteria:
  1. A Read under an unregistered, row-less id throws `UnregisteredProjectException`, migrated or
     not, guid or not, and calls no `RegisterAsync`.
  2. A Read under a row-holding unregistered id passes and logs 433 once. The raw-SQL integration
     test proves it with no projects row at all.
  3. Writes are unchanged, including pre-migration auto-registration.
  4. ToolGate folds an alias and refuses a dropped id with no marker. This covers reads, during an
     open repair request too.
  5. `memory_performance` passes under `__self_metrics__`. `__SELF_METRICS__` and any other
     unregistered id are refused. `memory_search` under the sentinel is refused.
  6. `memory_promotion_list allProjects=true` and `project_id_token_get` pass on an empty
     registry. A cwd-resolved id passes only if registered or row-holding.
  7. Every registered tool with a `projectId` (or `projectIds`) parameter refuses an unregistered
     guid over the wire. The tool list is derived from `RegisteredTools.Methods()`. The
     per-tool minimal-args table is checked against that list, so it cannot drift.
  8. `memory_share_extract mode=propose` under an unregistered id is refused and writes no
     `promotion_queue` row.
  9. A refused `memory_search` and a refused `memory_stats` leave no `metrics` measurement and no
     `search_quality` row under the refused id. A `refused` measurement for the same call is
     present.
  10. The 433 text and the `UnregisteredProjectException` text quote only real commands. B2's
      extension of `ToolRefusalsRemedyTests` proves it, so until B2 merges the message names a
      verb the task branch does not have yet.
  11. `git grep -n -i -E "reads (are )?(still )?(pass|never refused|allowed unconditionally)|never refused|project id convert" -- src`
      prints nothing. B1 runs the same grep over docs.
  12. Every seeded class above is green, and the straggler run is green.
- gate:
  ```
  dotnet exec $T --filter-class AiRaccoon.Tests.Unit.Projects.ProjectRegistrationGuardTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.ToolGateTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.ToolGateRetiredIdTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.ToolGateCwdDefaultTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.ReadRegistrationTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.PerformanceToolsTests \
    --filter-class AiRaccoon.Tests.Integration.Mcp.UnregisteredIdRefusalTests \
    --filter-class AiRaccoon.Tests.Integration.Mcp.RetiredIdReadRefusalTests \
    --filter-class AiRaccoon.Tests.Integration.Observability.RefusedReadTelemetryTests \
    --filter-class AiRaccoon.Tests.Integration.Mcp.ToolRefusalsTests \
    --filter-class AiRaccoon.Tests.Integration.Projects.OrphanVerbatimRefusalTests
  dotnet exec $T --filter-namespace AiRaccoon.Tests.E2E
  dotnet exec $T --filter-namespace AiRaccoon.Tests.Integration
  dotnet exec $T $SOURCE_GATES
  ```
- tests:

| Test | Failure mode | Mutation |
|---|---|---|
| `ProjectRegistrationGuardTests.Read_UnregisteredGuidWithNoRows_IsRefused` (replaces `AReadRequirement_IsNeverRefused`) | the Read early return survives | restore `if (requirement == Read) return;` |
| `…Read_UnregisteredGuid_AfterMigration_IsRefused` (replaces `…AfterMigration_IsNeverRefused`) | same, migrated | same |
| `…Read_UnregisteredRawText_BeforeMigration_IsRefusedAndRegistersNothing` | reads inherit auto-registration | drop the write-only term from the auto-register condition |
| `…Read_RegisteredId_PassesSilently` | over-refusal | negate `IsRegisteredAsync` |
| `…LegacyWarning_GuidId_NamesRegister` / `…_RawTextId_NamesRepairMapAndRegister` (FakeLogger) | the remedy names a missing verb, or the wrong one for the id shape | revert to `project id convert`; swap the branches |
| existing `AnUnregisteredRawTextId_BeforeMigration_IsAutoRegistered` (kept) | writes lose compatibility | apply the auto-register to reads and writes alike |
| `OrphanVerbatimRefusalTests.LegacyRowsWithoutRegistration_ReadPassesAndWarns433` (raw-SQL rows, no projects row) | the row exemption is lost on reads (the old test could not see it) | move the `HasRowsAsync` branch under a write-only condition |
| `ToolGateRetiredIdTests.RequireAsync_ReadUnderDroppedId_IsRefusedAsRetired` (replaces `…_PassesThrough`) | D2 not applied | restore `requirement is not Read &&` |
| `ToolGateRetiredIdTests.RequireAsync_DroppedIdWithOpenRepairRequest_IsStillRefused` (replaces `…WhenExplicitlyUnmigrated_PassesTheDroppedIdThrough`) | the marker window lets retired ids through | reintroduce the `IsMigratedAsync` condition |
| `ToolGateTests.RequireAsync_AliasDuringRepairWindow_FoldsToTheWinner` (replaces `…WhenExplicitlyUnmigrated_PassesTheLoserThroughUnfolded`) | alias reads refused for ~3.5 min per re-repair (R1-S2) | same |
| `ReadRegistrationTests.Performance_SelfMetricsSentinel_PassesOnAnEmptyRegistry` | the sentinel is refused | delete the `PerformanceTools` branch |
| `…Performance_SentinelCaseVariant_IsRefused` (`__SELF_METRICS__`) | case-insensitive allow-list | `OrdinalIgnoreCase` |
| `…Performance_UnregisteredGuid_IsRefused` | the allow-list is wider than the sentinel | `StartsWith("__")` |
| `…Search_UnderSelfMetricsSentinel_IsRefused` | the allow-list moved into the guard | allow-list in `ProjectRegistrationGuard` |
| `…PromotionList_AllProjects_PassesOnAnEmptyRegistry` | allProjects is gated | route allProjects through `RequireAsync` |
| `…ShareExtractPropose_UnregisteredId_IsRefusedAndQueuesNothing` | propose persists queue rows | restore the Read early return |
| `…BlankId_ResolvedToUnregistered_IsRefused` / `…_ResolvedToRegistered_Passes` | the cwd path bypasses the check, or is over-refused | return the resolved id before `registration.EnsureAsync`; refuse every resolved id |
| `UnregisteredIdRefusalTests.EveryProjectIdTool_RefusesAnUnregisteredGuid` ([RetryTheory] over `RegisteredTools.Methods()`) | a new or changed tool reads past the gate | gate one tool as `RequireBankAvailableAsync` only (e.g. `code_get`) |
| `…MinimalArgsTable_CoversEveryProjectIdTool` | the args table drifts from the registry | delete one row |
| `RetiredIdReadRefusalTests.ReadUnderDroppedId_OverTheWire_IsProjectRetired` | D2 holds in the unit but not end to end | the D2 unit mutation |
| `RefusedReadTelemetryTests.RefusedSearchAndStats_LeaveNoMeasurementOrQualityRowUnderTheId` (`TelemetryServerHost.Create`, `buffer.DrainAll()` before stop, then SQL on `search_quality`) | the refused id leaks into telemetry | (1) pass `_bankProjectId` instead of `metricProjectId` in `ToolExecutionActivity.RecordError`; (2) make the guard register the id inside the gate, so the call succeeds and records under it |

### B1: Server side: directory, `/projects`, `project_id_get`, exit constants

- depends_on: P (and merges SMALL before pushing, per the ownership table)
- effort: high
- files (src):
  - `src/AiRaccoon.Core/Projects/IProjectDirectory.cs`, holding:
    - the interface: `RegisterAsync(id, name)`, `FindByNameAsync(name)`, `CheckAsync(id)`
    - `ProjectRegistration(ProjectId, ProjectRegistrationOutcome {Registered, AlreadyRegistered, Retired, NotAGuid})`
    - `ProjectIdCheck(ProjectId, ProjectIdStatus {Known, Unknown, Retired})`
  - `src/AiRaccoon.Core/Projects/ProjectNameMatch.cs` (pure `Single(name, ids)`),
    `ProjectNotFoundException.cs` and `ProjectNameAmbiguousException.cs`
  - `src/AiRaccoon.Infrastructure/Sqlite/SqliteProjectDirectory.cs`:
    - register: `Apply`, then dropped gives `Retired`; registered gives `AlreadyRegistered`;
      row-holding or guid gives register and `Registered`, logging EventId **694** once per call;
      a non-guid that is neither registered nor row-holding gives `NotAGuid`
    - check: `Apply`, then dropped gives `Retired`; registered or row-holding gives `Known`;
      otherwise `Unknown`
    - find by name: `MemorySql.SelectProjectIdsByName` (`WHERE name = @name ORDER BY id`, BINARY),
      which also covers `%`
  - `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs`: `SelectProjectIdsByName`, and
    `InsertProject` becomes `ON CONFLICT(id) DO UPDATE SET name = excluded.name WHERE projects.name IS NULL AND excluded.name IS NOT NULL`
    (R1-N3)
  - `src/AiRaccoon.Core/Projects/IProjectRegistry.cs` (doc only: first non-null name wins)
  - `src/AiRaccoon/Settings/ProjectsProtocol.cs` and `ProjectsEndpoint.cs`:
    - `GET /projects?name=` returns `200 {ids}`; an empty list is still 200
    - `GET /projects/check?id=` returns `200 {status, projectId}`
    - `POST /projects {projectId, name}` returns `200 {outcome, projectId}`
    - a blank id or name is 400
  - `src/AiRaccoon/Setup/McpServerSetup.cs`: `MapProjects()` in the token-guarded block
  - `src/AiRaccoon/Setup/AppRegistrations.cs`: `IProjectDirectory -> SqliteProjectDirectory`
  - `src/AiRaccoon/Tools/ProjectTools.cs`:
    - `[McpServerTool(Name = TnProjectIdGet)] FindByName(string name)`, with a required name
    - it calls `RequireBankAvailableAsync`, then `IProjectDirectory.FindByNameAsync`, then
      `ProjectNameMatch.Single`
    - it takes no projectId and never mints
    - `project_id_token_get`'s description becomes: "Call project_id_get first; mint only when it
      finds nothing."
  - `src/AiRaccoon/Tools/ToolRefusals.cs`: `project-not-found` and `project-name-ambiguous`; the
    ambiguous message lists the ids
  - `src/AiRaccoon/Observability/ToolTelemetry.cs`: projection `project_id_get -> NoProjectId`
  - `src/AiRaccoon/ErrorCode.cs`: `Usage.ProjectUnknown = 18`, `Usage.ProjectAmbiguous = 19`
- files (docs):
  - `docs/adr/0107-…md`: rows 18 and 19, after D's row 37 edit
  - `docs/reference/cli-reference.md`: exit rows 18 and 19
  - `docs/reference/agent-memory-server.md`:
    - `## Tools (29)` becomes `(30)`, "1 project tool" becomes 2
    - the `project_id_get` row and the token_get row text
    - READ's registration paragraph (~`:422-423`) and the `project-not-registered`/`project-retired`
      rows (~`:1132-1133`)
    - two new error rows
  - `docs/reference/logging-event-ids.md`: 217 becomes 218, row 694
- files (tests, new):
  - `tests/AiRaccoon.Tests/Unit/Projects/ProjectNameMatchTests.cs` (Unit/Fast)
  - `tests/AiRaccoon.Tests/Integration/Projects/SqliteProjectDirectoryTests.cs` (Slow, alias collection)
  - `tests/AiRaccoon.Tests/Unit/Mcp/ProjectIdGetToolTests.cs` (Unit/Fast, NSubstitute `IProjectDirectory`)
  - `tests/AiRaccoon.Tests/Integration/Setup/ProjectsEndpointTests.cs` (Slow)
- files (tests, modified): `tests/AiRaccoon.Tests/Unit/Mcp/McpToolContractTests.cs` (contract string)
- acceptance criteria:
  1. `project_id_get` handles each case correctly:
     - one match returns `{projectId}`
     - none refuses `project-not-found`
     - two refuses `project-name-ambiguous` listing both
     - it writes no `projects` row in any case
     - it passes on an empty registry with no caller id
  2. The name match is exact, case-sensitive and literal for `%` and `_`. A raw-text id
     (`ai-badger`) comes back as stored.
  3. Register:
     - an alias returns `AlreadyRegistered <winner>` and writes no row
     - a dropped id returns `Retired` and writes no row
     - an unknown raw-text id returns `NotAGuid` and writes no row
     - a row-holding unregistered id is registered
  4. The first non-null name wins against the real store. A later name fills a NULL and never
     overwrites a set name.
  5. Check returns the statuses in the contract table.
  6. Every `/projects` route refuses an anonymous caller.
  7. Telemetry tags `project_id_get` as `none`.
  8. The docs grep from READ's criterion 11, rerun over `docs/reference` and
     `docs/adr/0089-*.md`, prints nothing except ADR-0089's own superseded text, which G amends.
     The same grep is case-insensitive over `README.md`.
  9. `npx --yes jscpd@5.3.2 --config .jscpd.json` passes (threshold 0.6).
- gate:
  ```
  dotnet exec $T --filter-class AiRaccoon.Tests.Unit.Projects.ProjectNameMatchTests \
    --filter-class AiRaccoon.Tests.Integration.Projects.SqliteProjectDirectoryTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.ProjectIdGetToolTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.ProjectTokenToolTests \
    --filter-class AiRaccoon.Tests.Integration.Setup.ProjectsEndpointTests \
    --filter-class AiRaccoon.Tests.Integration.Setup.EndpointGuardTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.McpToolContractTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.ToolInventoryTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.ToolRefusalsRemedyTests \
    --filter-class AiRaccoon.Tests.Integration.Observability.ToolTelemetryProjectionTests \
    --filter-class AiRaccoon.Tests.Unit.ErrorCodeTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.Serve.CliExitCodeMeaningTests
  npx --yes jscpd@5.3.2 --config .jscpd.json
  dotnet exec $T $SOURCE_GATES
  ```
- tests:

| Test | Failure mode | Mutation |
|---|---|---|
| `ProjectNameMatchTests.Single_None_ThrowsNotFound` / `…_Two_ThrowsAmbiguousListingBoth` / `…_One_ReturnsIt` | wrong arity handling | `ids.FirstOrDefault()` without the count check |
| `SqliteProjectDirectoryTests.FindByName_IsExactCaseSensitiveAndTreatsPercentLiterally` | `LIKE` or `NOCASE` matching | `WHERE name LIKE @name` |
| `…FindByName_RawTextId_ReturnedAsStored` | the id is re-canonicalized on the way out | `Canonicalize` the result |
| `…Register_SecondNonNullName_KeepsTheFirst` / `…_LaterNameFillsNull` | the name is overwritten, or never filled | `DO UPDATE SET name = excluded.name` without the WHERE; keep `DO NOTHING` |
| `…Register_AliasOfRegistered_IsAlreadyRegisteredUnderWinner_NoRow` | the alias becomes a second project | register before `Apply` |
| `…Register_Dropped_IsRetired_NoRow` | a retired id is resurrected | ignore `Dropped` |
| `…Register_UnknownRawText_IsNotAGuid_NoRow` / `…_RowHoldingRawText_IsRegistered` | raw text is wrongly accepted or refused | drop the row-holding branch; drop the guid test |
| `…Check_FoldsToRowHoldingWinner_IsKnown` / `…_Unknown` / `…_Retired` | the wrong status | collapse `Retired` into `Unknown` |
| `…Register_LogsEventId694` | a silent durable change | delete the log call |
| `ProjectIdGetToolTests.NotFound_RefusesAndNeverRegisters` | the tool mints on a miss | call `RegisterAsync` on a miss |
| `…TakesNoProjectId_PassesOnEmptyRegistry` | the tool is registration-gated | call `gate.RequireAsync` |
| `ProjectsEndpointTests.GetByName_NoMatch_Is200EmptyIds` | a miss reads as 57 in the CLI | `Results.NotFound()` |
| `…Post_BlankId_Is400` | a blank id reaches the store | drop the blank check |

### SMALL: D, E, F as three commits

#### D: The repair loop waits for the finish stamp

- depends_on: none
- effort: medium
- files:
  - `src/AiRaccoon.Core/Projects/ProjectIdCensusReport.cs` (`bool RepairOpen = false`)
  - `src/AiRaccoon.Infrastructure/Sqlite/SqliteRepairStore.cs` (fills it from `HasOpenRepairRequest`, SELECT-only)
  - `src/AiRaccoon/Setup/Cli/Commands/ProjectIdsRepairCommands.cs`:
    - after its own commit, `RunPassAsync` polls one `PollInterval` at a time until `RepairOpen`
      is false or `TotalBudget` is spent
    - `TryStopAsync` ignores `RepairOpen` at pass 0, so a stale request changes nothing (R1-S7)
    - running out of budget with the request open exits 37, naming the server
  - `src/AiRaccoon/ErrorCode.cs` (`RepairStuck` doc), `docs/adr/0107-…md` (row 37) and
    `docs/reference/cli-reference.md` (row 37)
  - tests:
    - `tests/AiRaccoon.Tests/Unit/Setup/Cli/ProjectIdsRepairLoopTests.cs`
    - `tests/AiRaccoon.Tests/Integration/Storage/SqliteRepairStoreTests.cs`
    - `tests/AiRaccoon.Tests/Integration/Setup/RepairEndpointTests.cs`
- acceptance criteria:
  1. No `converged`, `pinned-only` or `P3` line prints until a census read after this run's own
     commit reports `RepairOpen == false`.
  2. A request still open when the budget ends exits 37 with "the server has not finished".
  3. A stale open request at pass 0 with a settled census ends exactly as today.
  4. `--queue-only` and `ProjectIdsRepairJob.RunAsync` are unchanged.
  5. `ReportProjectIdsAsync` makes zero writes.
- gate:
  ```
  dotnet exec $T --filter-class AiRaccoon.Tests.Unit.Setup.Cli.ProjectIdsRepairLoopTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.Cli.ProjectIdsRepairTranscriptTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.Cli.RepairCommandsTests \
    --filter-class AiRaccoon.Tests.Integration.Storage.SqliteRepairStoreTests \
    --filter-class AiRaccoon.Tests.Integration.Setup.RepairEndpointTests \
    --filter-class AiRaccoon.Tests.Integration.Maintenance.ProjectIdsRepairJobTests \
    --filter-class AiRaccoon.Tests.Integration.Projects.ProjectIdCensusTests \
    --filter-class AiRaccoon.Tests.Integration.Projects.SingleProjectIdCensusTests \
    --filter-class AiRaccoon.Tests.Integration.Projects.ProjectIdsConvergenceTests \
    --filter-class AiRaccoon.Tests.Unit.ErrorCodeTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.Serve.CliExitCodeMeaningTests
  dotnet exec $T $SOURCE_GATES
  ```
- tests:

| Test | Failure mode | Mutation |
|---|---|---|
| `ProjectIdsRepairLoopTests.Apply_DoesNotReportSettledWhileTheRequestIsOpen` | P3 is claimed before `finished_at` (10:47Z vs 10:50:32Z) | delete the `RepairOpen` check |
| `…Apply_KeepsPollingUntilTheRequestCloses` (counts report reads) | one poll, then stop | a single delay instead of the loop |
| `…Apply_BudgetSpentWithRequestOpen_Exits37NamingTheServer` | an open request reported as converged | return `Success` on exhaustion |
| `…Apply_StaleOpenRequestAtPassZero_EndsAsToday` | a stale row hangs or flips the verdict | honour `RepairOpen` at pass 0 |
| `SqliteRepairStoreTests.ReportProjectIds_RepairOpen_TracksTheRequestRow` / `…_IgnoresOtherKinds` | the flag is unwired, or reads any kind | hard-code false; drop the `kind` parameter |
| `…ReportProjectIds_RunsUnderQueryOnly` (`PRAGMA query_only`, same pattern as `Collect_RunsUnderQueryOnly…`) | the report starts writing | add a write to the report path |
| `RepairEndpointTests.ProjectIdsReport_CarriesRepairOpenToTheClient` | the flag is lost on the wire | rename the parameter on one side |

#### E: Quality rows count as telemetry for the pin

- depends_on: D (same lane)
- effort: low
- files: `src/AiRaccoon.Core/Projects/ProjectIdsFoldPlan.cs` (private `OwnsOnlyTelemetry(row)`:
  `EntryTotal == 0 && AttachmentCount == QualityRows`; `AttachmentCount` and `IsRetireEligible`
  unchanged), `tests/AiRaccoon.Tests/Unit/Projects/ProjectIdsFoldPlanTests.cs`, and
  `tests/AiRaccoon.Tests/Unit/Setup/Cli/ProjectIdsRepairTranscriptTests.cs` (E owns its edits)
- acceptance criteria:
  1. An unmapped, unregistered id owning only metrics, noise or quality rows pins telemetry-only.
  2. One more non-telemetry attachment keeps it unresolved.
  3. Folds and drops still move or delete quality rows.
  4. A registered empty id with quality rows is not retire-eligible.
  5. A census of only such ids ends `pinned-only` (exit 0).
- gate:
  ```
  dotnet exec $T --filter-class AiRaccoon.Tests.Unit.Projects.ProjectIdsFoldPlanTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.Cli.ProjectIdsRepairTranscriptTests \
    --filter-class AiRaccoon.Tests.Integration.Projects.ProjectIdsRepairTelemetryWorkspacesTests \
    --filter-class AiRaccoon.Tests.Integration.Projects.ProjectIdsConvergenceTests
  dotnet exec $T $SOURCE_GATES
  ```
- tests:

| Test | Failure mode | Mutation |
|---|---|---|
| `FromCensus_UnmappedUnregistered_MetricsAndQualityOnly_PinsTelemetryOnly` | quality rows block the pin | revert to `AttachmentCount == 0` |
| `…_QualityOnly_PinsTelemetryOnly` | metrics or noise required | keep the `(MetricsRows > 0 \|\| NoiseRows > 0)` precondition |
| `…_QualityPlusWatch_StaysUnresolved` | the pin swallows real attachments | drop the attachment term |
| `FromCensus_DroppedId_QualityOnly_IsStillDropped` | the drop path changes | check telemetry before the drop branch |
| `FromCensus_RegisteredEmpty_WithQualityRows_IsNotRetireEligible` | `AttachmentCount` "fixed" instead | subtract `QualityRows` in `AttachmentCount` |
| `Transcript_TelemetryOnlyIdsWithQualityRows_EndsPinnedOnly` | the CLI still says attention needed | revert the plan change |

#### F: Grade and follow-through are project-scoped and truthful

- depends_on: E (same lane); merges READ before pushing (shared test files)
- effort: medium
- files:
  - `src/AiRaccoon.Core/SearchQuality/ISearchQualityService.cs`: `RecordFollowThroughAsync(projectId, correlationId, filePath, servedRank)`
    and `RecordGradeAsync(...)` return `Task<bool>`; projectId is a predicate
  - `src/AiRaccoon.Infrastructure/Sqlite/SqliteSearchQualityService.cs`:
    - the follow-through SELECT and UPDATE, and the grade UPDATE, add `AND project_id = @ProjectId`
    - the return value is `rowsAffected > 0` from the UPDATE alone
  - `src/AiRaccoon/Tools/QualityTools.cs`: passes `canonical` to both and returns
    `Recorded = <bool>` (today it is hard-coded true, `QualityTools.cs:36-37,58-59`); the
    descriptions say the row must belong to this project
  - the six fakes, each updated:
    - `tests/AiRaccoon.Tests/TestData.cs` (`:735`)
    - `tests/AiRaccoon.Tests/Unit/Mcp/MemorySearchKindToolTests.cs` (`:618`)
    - `tests/AiRaccoon.Tests/Unit/Mcp/QualityToolsTests.cs` (`:85`)
    - `tests/AiRaccoon.Tests/Unit/Memory/SearchDispatcherEvidenceTests.cs` (`:219`)
    - `tests/AiRaccoon.Tests/Unit/Memory/SearchDispatcherTests.cs` (`:180`)
    - `tests/AiRaccoon.Tests/Unit/Observability/RefusedQueryRedactionTests.cs`
  - call sites:
    - `tests/AiRaccoon.Tests/Integration/SearchQualityServiceTests.cs`
    - `tests/AiRaccoon.Tests/Integration/SearchQualityResultFeaturesTests.cs`
    - `tests/AiRaccoon.Tests/Integration/SearchSignalPreservationStageOneTests.cs`
  - `docs/reference/agent-memory-server.md`'s two `{recorded}` rows move to H (B1 owns that file in W1)
- acceptance criteria:
  1. Under another project, both calls leave the row untouched and return false.
  2. Under the row's own project, both update it and return true.
  3. The tool's `recorded` equals the service result.
  4. A loser-spelled call reaches the winner's row through the gate's canonical id.
  5. Every call site passes the project its `RecordSearchAsync` used.
- gate:
  ```
  dotnet exec $T --filter-class AiRaccoon.Tests.Integration.SearchQualityServiceTests \
    --filter-class AiRaccoon.Tests.Integration.SearchQualityResultFeaturesTests \
    --filter-class AiRaccoon.Tests.Integration.SearchSignalPreservationStageOneTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.QualityToolsTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.MemorySearchKindToolTests \
    --filter-class AiRaccoon.Tests.Unit.Memory.SearchDispatcherTests \
    --filter-class AiRaccoon.Tests.Unit.Memory.SearchDispatcherEvidenceTests \
    --filter-class AiRaccoon.Tests.Unit.Observability.RefusedQueryRedactionTests
  dotnet exec $T $SOURCE_GATES
  ```
- tests:

| Test | Failure mode | Mutation |
|---|---|---|
| `RecordGrade_UnderAnotherProject_LeavesTheRowUntouched` (deliberate flip of `RecordGrade_CorrelationIdOnlyKeying_ProjectIdNotAPredicate`) | cross-project grading | drop the predicate from the grade UPDATE |
| `RecordFollowThrough_UnderAnotherProject_LeavesTheRowUntouched` | same, for follow-through | drop the predicate from the UPDATE only |
| `RecordGrade_UnderItsOwnProject_UpdatesAndReturnsTrue` / `RecordFollowThrough_…` | the predicate binds a wrong value | bind `@ProjectId` to the correlation id |
| `RecordGrade_ReturnsFalseWhenNoRowMatched` / `RecordFollowThrough_ReturnsFalseWhenNoRowMatched` | `true` regardless | `return true;`; derive the result from the SELECT |
| `QualityToolsTests.Grade_ServiceFalseThenTrue_RecordedFollowsTheService` / `FollowThrough_…` | the tool hard-codes `true` | `new GradeResult(true)` |
| `QualityToolsTests.FollowThrough_PassesTheGateCanonicalId` | the raw argument is passed | pass `projectId` |

### C: Settings writes resolve, refuse and rewrite project keys

- depends_on: READ, B1 (`ErrorCode.Usage.ProjectUnknown`), P
- effort: high
- files (src):
  - `src/AiRaccoon.Core/Projects/ProjectSettingsKeys.cs`: pure
    - `Prefixes`, one list of `(prefix, excludesGlobal)`
    - `TryGetProjectId(key, out id)`
    - `WithProjectId(key, id)`
    - `KeysFor(id)`
  - `src/AiRaccoon.Infrastructure/Sqlite/ProjectIdCensus.cs`: `TryAttributeSetting` calls Core
  - `src/AiRaccoon.Infrastructure/Sqlite/ProjectIdsRepair.cs`: `SettingsKeysFor` becomes
    `ProjectSettingsKeys.KeysFor`; order kept for the loser/winner `Zip`
  - `src/AiRaccoon.Core/Ingestion/IngestScopeList.cs`: pure `IsSubset(stored, proposed)`
  - `src/AiRaccoon/Settings/SettingsEndpoint.cs`:
    - GET `?key=` and PUT resolve a project key through `Apply` and `WithProjectId`
    - PUT returns 400 for a blank owner, and 409 for retired, or for unknown to
      `IProjectRegistrationGuard.EnsureAsync(resolved, Read)` (unless a scope subset)
    - `*`, non-project keys, prefix GET and DELETE are untouched
  - `src/AiRaccoon/Settings/ServerSettingsStore.cs`: a PUT 409 throws `ProjectRefusedException(body)`
  - `src/AiRaccoon/Setup/Cli/Commands/ConfigCommands.cs`: catch, write stderr as-is, return
    `ErrorCode.Usage.ProjectUnknown`
- files (docs): `docs/reference/cli-reference.md` (one sentence under `settings`)
- files (tests):
  - new `tests/AiRaccoon.Tests/Unit/Projects/ProjectSettingsKeysTests.cs` (Unit/Fast)
  - `tests/AiRaccoon.Tests/Integration/Setup/SettingsEndpointTests.cs` (+alias collection on the
    new tests' class, or a new `SettingsEndpointProjectKeyTests` class if the existing one cannot
    join the collection)
  - `tests/AiRaccoon.Tests/Integration/Setup/ServerSettingsStoreTests.cs`: `KeysAreEscaped…`
    (`:95-101`) switches to a non-project key with the same characters (`test.escape:a&prefix=b`),
    so it stays about escaping (R2-M3)
  - `tests/AiRaccoon.Tests/Unit/Setup/ConfigCommandsAccessModelTests.cs` and `ConfigCommandsWatchTests.cs`
  - new `tests/AiRaccoon.Tests/Unit/Ingestion/IngestScopeListTests.cs` (Unit/Fast; no scope-list test exists today): `IsSubset` cases, mutation `return true`
- acceptance criteria:
  1. The four writers named in D3 (`access set`, `watch enable|disable`, `watch concurrency`,
     `ingest scope add`) under an unknown id exit 18 and write nothing.
  2. A dropped id gives 18 with the retired text.
  3. Uppercase and braced guid spellings resolve to the canonical key.
  4. An alias writes and reads the winner's key, and `scope add` under an alias keeps the
     winner's existing paths.
  5. `scope remove` (a subset) passes under any id.
  6. `watch disable` under an unknown id is refused.
  7. `*` and global keys pass.
  8. A blank owner is 400.
  9. A refused PUT never registers anything, even on an unmigrated bank.
  10. The census and repair attribute exactly the same keys as before.
- gate:
  ```
  dotnet exec $T --filter-class AiRaccoon.Tests.Unit.Projects.ProjectSettingsKeysTests \
    --filter-class AiRaccoon.Tests.Integration.Setup.SettingsEndpointTests \
    --filter-class AiRaccoon.Tests.Integration.Setup.ServerSettingsStoreTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.ConfigCommandsWatchTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.ConfigCommandsAccessModelTests \
    --filter-class AiRaccoon.Tests.Integration.Projects.ProjectIdCensusTests \
    --filter-class AiRaccoon.Tests.Integration.Projects.SingleProjectIdCensusTests \
    --filter-class AiRaccoon.Tests.Integration.Projects.ProjectIdsConvergenceTests \
    --filter-class AiRaccoon.Tests.Integration.Projects.ProjectIdsRepairFoldCommittedTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.Cli.RepairCommandsTests \
    --filter-class AiRaccoon.Tests.Unit.Ingestion.IngestScopeListTests
  dotnet exec $T $SOURCE_GATES
  ```
- tests:

| Test | Failure mode | Mutation |
|---|---|---|
| `ProjectSettingsKeysTests.TryGetProjectId_EachPrefix` (theory over the shared list) / `…_GlobalExcludedWhereItWasBefore` | a prefix is missed, or `global` is treated as a project | delete one prefix; drop `excludesGlobal` |
| `…KeysFor_MatchesTheCensusAttribution` | the repair and census lists diverge again | reorder or drop one prefix in `KeysFor` |
| `SettingsEndpoint….Put_UnknownId_Is409WritesNothing` ([RetryTheory] over the four writers' keys) | the HTTP writer accepts any id | remove the check |
| `…Put_RegisteredOrRowHolding_Writes` | over-refusal, or the legacy exemption lost | invert the guard; call `IsRegisteredAsync` alone |
| `…Put_DroppedId_Is409Retired` | a retired id gets settings | ignore `Dropped` |
| `…Put_UppercaseBracedGuid_WritesCanonicalKey` | a key nobody reads is written | skip `WithProjectId` |
| `…ScopeAddUnderAlias_KeepsWinnersPaths` (GET then PUT through `ServerSettingsStore`) | the winner's list is overwritten | resolve PUT but not GET |
| `…Put_ScopeSubset_UnderUnknownId_Writes` / `…Put_ScopeSuperset_UnderUnknownId_Is409` | removal refused, or growth allowed | drop `IsSubset`; invert it |
| `…Put_WatchDisable_UnknownId_Is409` | the subset rule leaks to non-scope keys | apply `IsSubset` to every prefix |
| `…Put_Wildcard_Writes` / `…Put_BlankOwner_Is400` | `*` refused; a blank owner written | remove the `*` exemption; drop the blank check |
| `…Put_RefusedOnUnmigratedBank_RegistersNothing` | the check auto-registers | pass `AccessRequirement.Write` |
| `ServerSettingsStoreTests.SetSetting_On409_ThrowsProjectRefusedWithBody` | a bare HTTP error | let 409 reach `EnsureSuccessStatusCode` |
| `ConfigCommandsAccessModelTests.AccessSet_Refused_Exits18` | wrong exit code | map to `RequestRejected` |

### B2: CLI `project id register | get | check`

- depends_on: B1, READ; runs beside C, then merges C and re-runs C's gate
- effort: high
- files (src):
  - `src/AiRaccoon/Settings/ServerSettingsStore.cs` and `LazyServerSettingsStore.cs`: implement
    `IProjectDirectory` over `ProjectsProtocol`
  - new `src/AiRaccoon/Settings/CliServerBinding.cs`: static extension
    `BindCliToServer(this IServiceCollection, LazyServerSettingsStore)`, the whole override
    block plus `IProjectDirectory` (R3-M3)
  - `src/AiRaccoon/AppRunner.cs`: calls it
  - `src/AiRaccoon/Setup/Cli/CliCommandTree.cs`: the `project id register <id> [--name]`,
    `get --name` (required) and `check <id>` verbs
  - `src/AiRaccoon/Setup/Cli/CliArgs.cs`: only if the R3-M1 test goes red. Value-taking root
    options are then normalized from `--opt value` to `--opt=value`, deriving the set from the
    root's options the way `ContainsVerb` does (`CliArgs.cs:142-175`)
  - new `src/AiRaccoon/Setup/Cli/Commands/ProjectIdCommands.cs`: a blank argument is 10 before
    any backend; then one directory call; stdout, stderr and the exit follow the contract table
  - `src/AiRaccoon/Setup/Cli/Commands/CommandsRegistration.cs` and `ConfigCommands.cs`: dispatch arms
- files (docs): `docs/reference/cli-reference.md` (three verb rows)
- files (tests):
  - new `tests/AiRaccoon.Tests/Unit/Setup/Cli/ProjectIdCommandsTests.cs` (Unit/Fast)
  - new `tests/AiRaccoon.Tests/Integration/Setup/ProjectIdCliTests.cs` (Slow, real process)
  - `tests/AiRaccoon.Tests/Unit/Setup/CliArgsTests.cs`
  - `tests/AiRaccoon.Tests/E2E/NoBankE2ETests.cs`: explicit argv rows
    `project id check <guid>`, `project id get --name x` and `project id register <guid>`, because
    the family check at `:51-54` demands a runnable leaf and every `project id` leaf takes an argument
  - `tests/AiRaccoon.Tests/Integration/Setup/CliBankWriteTests.cs`: `get` and `check` join the
    read-verb theory
  - `tests/AiRaccoon.Tests/Unit/Setup/CliCommandsDoNotOpenTheBankTests.cs` (`:130-134`) and
    `tests/AiRaccoon.Tests/Integration/Setup/Serve/VecDimensionReconcileAtStartTests.cs`
    (`:256-260`): use `BindCliToServer`
  - `tests/AiRaccoon.Tests/Unit/Setup/Serve/AppRunnerSettingsRoutingTests.cs`,
    `tests/AiRaccoon.Tests/Unit/Setup/LazyServerSettingsStoreTests.cs`,
    `tests/AiRaccoon.Tests/Unit/Setup/Cli/CliCommandTreeTests.cs`
  - `tests/AiRaccoon.Tests/Unit/Mcp/ToolRefusalsRemedyTests.cs`: a theory over the commands quoted
    by the 433 messages (both branches) and `UnregisteredProjectException`; the placeholders
    `<id>` and `<file>` are added
- acceptance criteria:
  1. Every contract-table row holds over a real process against a real server, including:
     - 19
     - get not found
     - check unknown and check retired
     - raw-text get, check and register
     - a register for an unknown raw-text id exits 10
     - 57 from a server without `/projects`
  2. stdout is one line. stderr is empty on success under `--quiet`. No verb reads stdin.
  3. A blank argument exits 10 with no server contacted.
  4. `--install-scope project serve`, `--install-scope project` and
     `--install-scope project settings access list` parse exactly as before the `project` verb.
  5. The CLI never opens the bank. `IProjectDirectory` resolves to the lazy server store, and
     `get` and `check` commit nothing.
  6. `NoBankE2ETests` sees `project` verbs exit 31 at a typo root and leave it empty.
  7. Every command quoted by a refusal message parses against the real tree.
  8. jscpd passes.
- gate:
  ```
  dotnet exec $T --filter-class AiRaccoon.Tests.Unit.Setup.Cli.ProjectIdCommandsTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.CliArgsTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.Cli.CliCommandTreeTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.Serve.AppRunnerSettingsRoutingTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.LazyServerSettingsStoreTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.CliCommandsDoNotOpenTheBankTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.CliWriteOptOutsTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.ToolRefusalsRemedyTests \
    --filter-class AiRaccoon.Tests.Integration.Setup.Serve.VecDimensionReconcileAtStartTests
  # spawn suites, never alongside READ's E2E run:
  dotnet exec $T --filter-class AiRaccoon.Tests.Integration.Setup.ProjectIdCliTests \
    --filter-class AiRaccoon.Tests.Integration.Setup.CliBankWriteTests \
    --filter-class AiRaccoon.Tests.E2E.NoBankE2ETests
  # after merging C: C's gate command, unchanged
  npx --yes jscpd@5.3.2 --config .jscpd.json
  dotnet exec $T $SOURCE_GATES
  ```
- tests:

| Test | Failure mode | Mutation |
|---|---|---|
| `CliArgsTests.InstallScopeProject_BeforeAVerb_StillParses` (theory over the three argv) | the new verb swallows the option value (R3-M1) | add the verb with no normalization |
| `ProjectIdCommandsTests.Blank_Exits10WithoutTouchingTheDirectory` (fake throws if called) | usage errors reach the server | validate after the call |
| `…Register_New_PrintsRegistered` / `…_Existing_PrintsAlreadyRegistered` / `…_NotAGuid_Exits10` / `…_Retired_Exits18StdoutEmpty` | the outcome-to-contract mapping is wrong | swap two strings; map `Retired` to 0 |
| `…Get_One_PrintsBareId` / `…_None_Exits18` / `…_Several_Exits19IdsOnStderrOnly` | the codes collapse, or ids leak to stdout | ids to stdout; 18 for both |
| `…Check_Known_Exits0` / `…_Unknown_Exits18PrintsUnknown` / `…_Retired_Exits18PrintsRetired` | the wrong status line | collapse retired into unknown |
| `…NoVerb_ReadsStdin` (the reader throws) | a prompt sneaks in | add `ReadLineAsync` |
| `ProjectIdCliTests.EveryContractRow_OverARealServer` ([RetryTheory]) | the protocol halves drift | rename a JSON field on one side |
| `…OldServerWithoutProjects_Exits57` (a stub listener that answers 404) | an old server is misreported | map 404 to 18 |
| `…Quiet_StderrEmpty_StdoutOneLine` | the disclosure or a log line goes to stdout | write 687 to stdout |
| `AppRunnerSettingsRoutingTests.ProjectIdVerbs_ResolveTheServerBackedDirectory` | the CLI opens the bank | delete the `IProjectDirectory` line in `BindCliToServer` |
| `CliCommandsDoNotOpenTheBankTests` (unchanged assertions, on the extracted binding) | the copies drift from `AppRunner` | edit `AppRunner` without the helper |
| `NoBankE2ETests.EveryAutoLaunchVerb_AtATypoRoot_ExitsNoBank…` (+3 rows) | `project` verbs create a bank | bypass `BankPresenceGuard` |
| `ToolRefusalsRemedyTests.RegistrationRemedies_ParseAgainstTheRealCliTree` | a message names a missing verb | revert the 433 text to `project id convert` |

### G+H: ADR, release 1.57.0, follow-up issue

- depends_on: READ, B1, SMALL, C, B2
- effort: low
- files:
  - new `docs/adr/0127-reads-need-a-registered-project-id.md`
  - `docs/adr/0089-…md` (amendment notes on decisions 3 and 6)
  - `docs/adr/README.md` (row 0127, and the 0089 row's "Amended by ADR-0127")
  - `VERSION` (1.57.0)
  - new `docs/changelog/1.57.0-reads-need-a-registered-project.md` and the `docs/changelog/README.md` index line
  - `docs/reference/breaking-changes.md` (1.57.0 at the top)
  - `README.md` (Breaking changes "latest one is 1.57.0"; one What's new line, features only:
    `project id register|get|check` and `project_id_get`)
  - `docs/reference/whats-new-history.md`, only if the README list rotates
  - `docs/reference/agent-memory-server.md` (F's `{recorded: bool}` rows)
- acceptance criteria. ADR-0127 states each of these:
  1. the read rule and the retired-read refusal
  2. ToolGate folding without the marker, and why: `RequestRepair` reopens `finished_at`
  3. the three allow-listed shapes and the cwd path
  4. the settings rules: resolve, refuse, rewrite, subset removals
  5. `register|get|check` replacing decision 6's `generate`/`convert`
  6. `project_id_get`, both lookup-before-mint flows, and raw-text handling
  7. exit codes 18 and 19, chosen on fit
  8. the skew between a new CLI and an old server: exit 57
  9. the per-read cost: one bank open plus a primary-key SELECT, and a rows probe for
     unregistered ids
  10. alternatives rejected: auto-register on read; keep reads open and only drop telemetry; a
      marker latch (the `IsMigratedAsync_AfterASecondRequest_ReopensToFalse` ledger pins reopening
      as intended)

  The release and the follow-up:
  - The breaking-changes entry tells the reader what to do, and links ADR-0127.
  - The changelog names PR #846. It also notes that settings writes under unregistered raw-text
    ids are refused, removals excepted.
  - The follow-up issue covers sync-pull merges under remote ids and watch ingestion for
    pre-existing `watches` rows, citing research section 4. The PR body links it.
- gate:
  ```
  dotnet exec $T --filter-class AiRaccoon.Tests.Unit.Docs.AdrIndexTests \
    --filter-class AiRaccoon.Tests.Unit.Setup.VersionContractTests \
    --filter-class AiRaccoon.Tests.Unit.Mcp.ToolInventoryTests
  dotnet exec $T $SOURCE_GATES
  ```
  plus `cat VERSION` printing 1.57.0, and `gh issue view <n>`.
- tests: none new. Prove `AdrIndexTests` red with 0127 on disk and no README row, then green with it.

### J: Join (coordinator, merged tree)

- depends_on: every step
- effort: medium
- acceptance criteria:
  1. `dotnet build AiRaccoon.slnx` succeeds with zero warnings.
  2. Every step gate re-runs green, each with a non-zero `total:`. Spawn suites run one at a time.
  3. Cross-step chain (R2-S13), written by J into `ProjectIdCliTests` against one real server.
     Each link runs in this order:
     - `project id register <g>` exits 0
     - `memory_search` under `<g>` passes
     - `memory_search` under an unregistered guid is `project-not-registered`
     - `settings access set <g> rw` exits 0
     - `settings access set <unregistered> rw` exits 18
     - `project id check <g>` reads `known`
  4. Seed-removal proof (R2-M2): remove the seed from `McpTokenGateE2ETests` (`:89-92` asserts
     `IsError` false) and watch it go red. Restore it and watch it go green.
  5. The read-before-write and telemetry surfaces run together:
     - the `AiRaccoon.Tests.E2E` namespace
     - `ProxyForwardTests`, `McpServerSetupHostTests`, `NodeRunnerTests`, `OtlpExportTests`,
       `QuietLoggingTests`, `UnmappedExceptionDiagnosticsTests`
     - `ToolTelemetryCoverageTests`, `ToolTelemetryMeasurementCoverageTests`, `RefusedQueryRedactionTests`
     - `SingleProjectIdE2E`, `OrphanVerbatimRefusalTests`, `ProjectIdsConvergenceTests`
  6. BDD runs in CI form (`dotnet test --filter Category=bdd`) with a non-zero total. BDD runs on
     `AllowingRegistrationGuard`, so it does not validate D1. READ's tests do.
  7. The stub is unchanged in effect: `git diff origin/main -- tests | grep -E '^[-+].*AllowingRegistrationGuard'`
     prints nothing. This replaces the file count, which READ's ToolGate signature change
     legitimately touches.
  8. `dotnet exec $T --filter-namespace AiRaccoon.Tests.Unit`, `dotnet exec $T $SOURCE_GATES`, and jscpd.
  9. `review-tests` is run by a non-author on the test diff and returns a verdict.

## Deviations from ruling wording, with evidence

1. **R1-M2's `(canonical, map, migrated)` signature drops `migrated`.**
   - R1-S2 removes the marker from ToolGate, the only fold that consults it
     (`ToolGate.cs:66`).
   - `SqliteProjectDirectory` and `SettingsEndpoint` follow ToolGate.
   - So every caller would pass a constant. `ProjectIdAliasMap.Apply(id)` is the function, and
     `Fold` already canonicalizes (`ProjectIdAliasMap.cs:123-133`).
2. **R1-M2's PUT-only rewrite would lose data, so GET `?key=` resolves too.**
   - `settings ingest scope add` does GET, then modify, then PUT (`WatchCommands.cs:50-52`).
   - The CLI's `ProjectIdAliasMap.Default` is empty, so it sends the loser key.
   - With only PUT rewritten, the CLI reads the loser's empty list, adds one path, and the
     rewrite overwrites the winner's whole list with that one path.
3. **R1-M1's "a legacy id answers `already registered`" is right only for ids with a registry row.**
   - A row-holding raw-text id with no `projects` row is not registered, so the plan registers
     it and prints `registered <id>`. The output stays true, and warning 433 stops.
   - Already-registered legacy ids (the auto-registered raw-text ones) do answer
     `already registered`.
4. **R3-S4 "registry fakes use NSubstitute" is moot, not ignored.**
   - The name lookup lives in `SqliteProjectDirectory` (Infrastructure), so `IProjectRegistry`
     gains no member and no hand fake breaks.
   - This also removes the W1 collision on `ProjectRegistrationGuardTests` between READ and B1.
   - New fakes of `IProjectDirectory` use NSubstitute.

## Was this the simplest shape? What was cut

- No ToolGate API beyond removing a parameter.
- No `ShareTools` change.
- No new repair endpoint, `IRepairStore` member or exit code.
- No CLI-side registration check: the server is the choke point.
- No registration cache on reads.
- One new EventId (694).
- One directory interface for both processes.
- No `generate`/`convert` verbs.
- Cut by evidence: OTLP E2E seeding (they never assert `IsError`).

## Open questions

None blocks a step.
