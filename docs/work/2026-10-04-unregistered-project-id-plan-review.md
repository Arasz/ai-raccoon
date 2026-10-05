# Plan review: unregistered project ids (2026-10-04)

Three independent reviewers ran in parallel, all read-only:

- **R1:** correctness and architecture (code-reviewer)
- **R2:** QA and test design (Explore lane)
- **R3:** feasibility and lane safety (Explore lane)

Each finding is listed with the coordinator's ruling. **Accept** means it is folded into the plan
revision. Full evidence (file:line) is in the reviewers' hand-backs; the load-bearing citations
are repeated here.

## MUST: all accepted

| Id | Step | Finding | Ruling |
|---|---|---|---|
| R1-M1 | B1/B2/A | Six of the eight live projects have raw-text ids (`ai-raccoon`, `ai-badger`, `jsaa`, …). `get` can print them, and `check`/`register` exit 10 on any non-guid, so get→check breaks. The new 433 text would also send legacy-id owners to `register`, which then exits 10. | `check` accepts any non-blank id: a guid is canonicalized, anything else passes through. `register` refuses a non-guid only when it is neither registered nor row-holding; a legacy id answers `already registered`. The 433 remedy branches on guid vs raw text. Add raw-text rows to the contract and the tests. The ai-badger session has been told. |
| R1-M2 | C | The settings check neither folds aliases nor detects retired ids, because the CLI never loads the alias map. `ingest scope add cfe47dab-…` would be refused while the MCP tools and `project id check` accept the same id. | Add one pure Core function, `(canonical, map, migrated) → (id, dropped)`. ToolGate, ProjectDirectory and SettingsEndpoint all use it. SettingsEndpoint rewrites an alias key to the winner (IngestScopeKeys P4 rule) and refuses a dropped id as retired. Add tests for the alias and dropped cases. |
| R2-M1 | A2 | `ProxyForwardTests.ReacquireAsync` builds a second `McpServerFactory` (a new bank) that is never seeded. | Add one seam: `McpServerFactory` takes the project ids to register and seeds them inside `CreateClientAsync`. It replaces the roughly ten hand-placed seed calls. |
| R2-M2 | J | The seed-removal proof targets the OTLP tests, which never assert `IsError`, so the test cannot go red. | Prove it on `McpTokenGateE2ETests` (`:89-92`) or `NodeRunnerTests` (`:72-76`). Drop the claim that OTLP verifies the seed. |
| R2-M3 / R3-M4 | C | `ServerSettingsStoreTests.KeysAreEscaped…` (`:97`) PUTs `access.mode.project:a&prefix=b` on an empty bank, so C turns it red. | Seed `a&prefix=b`, or switch to a non-project key that has special characters. Add the class to C's files and gate. |
| R2-M4 / R3-M5 | F | Six `ISearchQualityService` fakes break; the plan names three. | Add `SearchDispatcherEvidenceTests`, `SearchDispatcherTests`, `RefusedQueryRedactionTests` and `QualityToolsTests` to F's files and gate. Re-rate F as medium. |
| R2-M5 | F | No test catches the tool hard-coding `recorded: true` (`QualityTools.cs:36-37,58-59`). | Add tool tests where the fake service returns false and then true, and a service-level follow-through `ReturnsFalseWhenNoRowMatched`. Use the predicated UPDATE's `rowsAffected` as the only source of truth (R2-S9). |
| R2-M6 | all | Per-step gates skip the repo-wide source gates. | One `SOURCE_GATES` block is appended to every step gate: NoConfigureAwait, NoHandRolledCrypto, LoggerMessageEventId, SqlHelperSourceGate, LayeringRules, ToolMethodSize, CategoryGateCoverage, SpeedGateCoverage, ProjectIdAliasDefaultCollectionGate and RetrySurfaceGate. |
| R3-M1 | B2 | A top-level `project` verb may break `--install-scope project <verb>`, because `project` is an `InstallScope` value. | Test first: `CliArgs.TryParse` over `--install-scope project serve`, `--install-scope project`, and `--install-scope project settings access list`. If it goes red, normalize `--opt value` to `--opt=value` for value-taking root options, derived from the root. Use `ContainsVerb` (there is no `IsTopLevelVerb`). |
| R3-M2 | B2 | `NoBankE2ETests` requires every family to have a runnable leaf, but all the `project id` leaves take arguments. | Add explicit argv rows, so a valid guid on a missing bank exits NoBank. Add the class to B2's gate. |
| R3-M3 | B2 | `CliCommandsDoNotOpenTheBankTests` and `VecDimensionReconcileAtStartTests` hand-copy AppRunner's CLI override block. | Extract `BindCliToServer(IServiceCollection, LazyServerSettingsStore)`, used by AppRunner and both tests. |
| R3-M6 | waves | Real-server suites must not run concurrently. | At most one spawn-a-server lane at a time. Use ephemeral ports (never 7721) and a temp data root per test. |

## SHOULD: accepted unless noted

- **R1-S1 (call 8): removals stay allowed.** This overrides the architect's flagged call 8. A PUT
  of `ingest.scope.<id>` or the legacy `watch.scope.<id>` whose new list is a subset of the stored
  list is exempt. The check is pure Core. DELETE routes are unaffected. `watch disable` stays
  refused, per D3.
- **R1-S2: ToolGate folds through `ProjectIdAliasMap.Default` with no migration-marker check.**
  Today `RequireRepair` resets `finished_at`, so alias reads would be refused for the whole repair
  window (about 3.5 minutes). Only the guard's auto-register branch keeps the marker.
- **R1-S3 / R2-S5:** the stated rationale for `OrphanRead_Passthrough` is false; that test's read
  passes because the write auto-registered the id. Add an integration test that seeds rows by raw
  SQL with no registration, then reads, and expects a pass plus 433.
- **R1-S4 / R2-S6:** the stale-text grep is made case-insensitive and widened:
  `git grep -n -i -E "reads (are )?(still )?(pass|never refused|allowed unconditionally)|never refused"`,
  run over src, docs, `docs/adr/0089` and README. Extend `ToolRefusalsRemedyTests` with the
  commands the new messages name (at B2).
- **R1-S5 / R2-S10:** D and C gates gain `ProjectIdCensusTests`, `SingleProjectIdCensusTests`,
  `RepairCommandsTests` and `ProjectIdsConvergenceTests`. Add a query-only assertion on
  `SqliteRepairStore.ReportProjectIdsAsync`.
- **R1-S6:** the per-project settings prefixes are defined once in Core and used by
  `ProjectSettingsKeys.TryGetProjectId` and `ProjectIdsRepair.SettingsKeysFor`.
- **R1-S7:** D honours `RepairOpen` only after this run's own commit; a stale open request at pass
  0 changes nothing.
- **R1-S8 / R3-S7:** A and A2 merge into one lane, so the shared branch is never red.
- **R1-N3:** register keeps the first non-null name, so a later `--name` fills a NULL name.
- **R2-S1/S2/S3:** RetrySurface, Category/Speed trait and ProjectIdAliasDefaultCollection gates
  apply wherever they are relevant. New Integration classes are `Speed=Slow`, never Nightly.
  Dropped-id over-the-wire tests get their own class with `[Collection(ProjectIdAliasDefaultCollection.Name)]`
  and `ResetDefault()`.
- **R2-S4:** the allow-list tests use a real ToolGate and a real guard over an empty registry that
  throws on register, never `AllowingRegistrationGuard`. A sentinel case variant
  (`__SELF_METRICS__`) is refused.
- **R2-S7:** a derived theory over `RegisteredTools.Methods()` asserts that every tool with a
  `projectId` parameter refuses an unregistered id. The explicit allow-list is
  `project_id_token_get`, `project_id_get`, `memory_promotion_list(allProjects)` and
  `memory_performance(__self_metrics__)`.
- **R2-S8:** the refused-read telemetry test uses `TelemetryServerHost.Create` plus
  `buffer.DrainAll()`, in a new class. Replace mutation 2 with "register inside the gate".
- **R2-S11/S12:** C's test is parameterised over all four verbs, plus scope-remove (subset allowed),
  retired, and uppercase/braced guid. B1 tests name case-sensitivity with `%`. First-name-wins is
  tested over the real store. B2 covers every contract row over a real process: 19, get not found,
  check unknown, check retired, non-guid 10, and 57 from an old server.
- **R2-S13:** J adds a cross-step real-server chain (register → search passes; unregistered
  refused; `settings access set` registered ok / unregistered 18) in `ProjectIdCliTests`. Replace
  the brittle AllowingRegistrationGuard count. Add `ToolTelemetryMeasurementCoverageTests` and
  `RefusedQueryRedactionTests`. BDD uses the CI form with a non-zero total, and states that BDD does
  not validate D1.
- **R2-S14:** H's gate gains `VersionContractTests`. jscpd (`npx jscpd`, threshold 0.6) runs in B1,
  B2 and J.
- **R3-S2:** stderr is empty only under `--quiet`; the ai-badger session has been told.
- **R3-S3:** D owns the ADR-0107 row 37 wording and is the first owner of `cli-reference.md`. ADR-0127
  states the skew between a new CLI and an old server.
- **R3-S4/S5/S6/S8:** B1 gates gain `EndpointGuardTests`. `project_id_get` takes a required name,
  gets a unique method name and a `Tn` const, and goes through `IProjectDirectory` (R1 layering
  note). Registry fakes use NSubstitute. A blank-owner key returns 400. The legacy raw-text
  scope-add consequence goes into the changelog.
- **R1-N1:** the claim that "Usage is the only decade with free slots" is false. Usage 18/19 is kept
  on fit.
- **R1-N2:** ADR-0127's consequences state the per-read cost (a bank open plus a PK SELECT).

## Flagged calls

- **(7)** Grade and follow-through returning `recorded: false`: endorsed by R1. Kept.
- **(8)** Refusing scope removal: overruled; see R1-S1.

## Revised waves

At most three lanes at a time, and one spawn-a-server lane at a time.

- **W1** runs three lanes:
  - A+A2: the read rule, the McpServerFactory seam, the straggler run, and the retired-read refusal.
  - B1: the server side, plus the ErrorCode 18/19 constants, ADR-0107 rows 18-19 and the
    cli-reference exit rows.
  - SMALL: D, E and F as three commits. E owns `ProjectIdsRepairTranscriptTests`.
- **W2**, after the W1 lanes merge:
  - C.
  - B2. It merges C afterwards and re-runs C's gates. Its real-process suite runs after A+A2's
    E2E run, never alongside it.
- **W3:** G+H.
- **J:** run by the coordinator on the merged tree, with `review-tests` done by a non-author.
