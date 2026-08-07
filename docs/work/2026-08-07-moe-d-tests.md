# MoE Panel — Expert D: Test architecture, coverage and test honesty

Subject: AiRaccoon test suite (`tests/AiRaccoon.Tests/`, 165 `.cs` files, 1416 discovered tests)
Reviewed in worktree: `.ai-badger/worktrees/full-project-review` (branch `main`, HEAD `db31c5e`)
Date: 2026-08-07

---

## Verdict

This is a serious test suite with a real spine: `SqliteMemoryStore` is exercised against a real
SQLite file copied per-test rather than mocked, the E2E layer drives the actual MCP protocol over
`WebApplicationFactory`, the golden-file retrieval gate does an exact hash-and-rank diff against a
committed oracle, and five separate `[Collection]` opt-outs each carry a written rationale naming
the flake they were built to stop. That is better discipline than most suites this size. But three
structural facts undercut it. First, **the PR CI gate runs 1008 of 1416 tests** — `build.yml:28`
filters on `Speed=Fast`, which silently excludes every E2E test, most integration tests, and the
*entire* 105-scenario Reqnroll BDD layer, because feature files carry no `@Speed` tag. Second, the
BDD layer that the project treats as its behavioural contract is substantially hollow: **42 of 172
step definitions in `NativeMemorySteps.cs` have empty bodies**, making 9 scenarios fully vacuous and
9 more partly so — including three access-control scenarios whose `Then the tool errors with
access-denied` binding is `{ }`. That vacuity has already cost something: the green scenario "All 17
tools are still listed" names a `memory_configure` tool that does not exist in the codebase. Third,
**a fresh worktree cannot run the suite green** — 48 tests fail on one missing gitignored ONNX
model, and the same missing precondition produces a graceful `Assert.Skip` in one test and a hard
`InvalidOperationException` in 48 others. Measured wall clock for the full run: **4m39s**.
Underneath those three, one specific hole stands out on merit: **`SyncService`'s If-Match
conflict-retry loop (`SyncService.cs:126-183`) — re-pull, re-merge, re-snapshot, retry, log-and-
rethrow on exhaustion — has no test at all**, even though `FakeCloudStore` can already throw the
exception needed to drive it. That is the most intricate untested code in the repository and it
handles user data. The honest summary is that the xUnit layer is load-bearing and good; the BDD
layer is ceremony wearing the costume of a contract; the CI gate does not run the parts of the
suite that would catch the worst regressions; and the sync merge path is flying without one.

---

## Findings

| ID | `file:line` | Sev | What's wrong | Why it matters |
|---|---|---|---|---|
| **D1** | `.github/workflows/build.yml:28` | **Critical** | PR gate is `dotnet test --filter "Speed=Fast"`. Measured: `--list-tests` returns **1008** filtered vs **1416** unfiltered. Excluded: all 5 E2E classes (0 hits for `E2E.` in the filtered list), 12 of 17 `Integration/` classes, and **all 105 Reqnroll scenarios** (feature files carry `@FR-*`/`@AC-*` tags, never `@Speed`; `tests/AiRaccoon.Tests/BDD/*.cs` contain no `Speed` trait). Full suite runs only in `nightly.yml:36`. | 29% of the suite — including every real-SQLite store test, workspace isolation, encryption round-trip, and the whole MCP wire surface — cannot block a merge. "Done means proven" names a gate that goes green; this gate is green on a third less evidence than the author thinks. |
| **D2** | `tests/AiRaccoon.Tests/BDD/NativeMemorySteps.cs` — 42 empty bindings, incl. `:1134`, `:1050`, `:1053`, `:1080`, `:1083`, `:1086`, `:1106`, `:1109`, `:1112`, `:1115`, `:1358` | **Critical** | 42 of 172 step definitions have empty or comment-only bodies. Cross-referencing the compiled feature files against the bindings, **9 scenarios are fully vacuous** (every `When`/`Then` is an empty binding) and **9 more contain at least one empty assertion step**. Full list in [Appendix A](#appendix-a). | These scenarios report *Passed* in the test run (verified in the run log). A Gherkin file is the project's stated behavioural contract; 18 of its scenarios are decoration. Worse, `NativeMemorySteps.cs:1134` `ThenAccessDenied() { }` is the assertion for three separate access-control scenarios. |
| **D3** | `tests/AiRaccoon.Tests/BDD/NativeMemorySteps.cs:863` | **High** | `[Then(@"memory_delete with a known hash errors with access-denied")] public async Task ThenMemoryDeleteErrorsWithAccessDenied() => _lastWrite.ShouldNotBeNull();` — the assertion is that a *previous write* returned non-null. It has no relationship to deletion, to access mode, or to an error. | A named security assertion that pins something entirely unrelated. It cannot fail for the reason its name gives. This is the single most dishonest line in the suite. |
| **D4** | `docs/work/features-native-memory/native-memory.feature:198` + `tests/AiRaccoon.Tests/Unit/Mcp/ToolInventoryTests.cs:38` | **High** | The vacuous scenario "All 17 tools are still listed" enumerates `memory_configure`. `grep -rn "memory_configure" src` returns **nothing** — the tool does not exist. `ToolInventoryTests.cs:38` asserts `tools.Count.ShouldBe(22)` and `McpServerToolSurfaceE2ETests.cs:30-56` lists 22 real names, none of them `memory_configure`. The empty binding's comment claims "Verified by ToolInventoryTests", which verifies a *different* list. | Direct proof that D2 costs real money: the spec drifted from 17 tools to 22, dropped one, and the scenario meant to catch exactly that stayed green because its body is `{ }`. The delegation comment is also factually wrong. |
| **D5** | `tests/AiRaccoon.Tests/Unit/Embedding/BundledModelGateTests.cs:25` (and 47 others) vs `tests/AiRaccoon.Tests/Unit/Embedding/BundledModelLoggingTests.cs:49` | **High** | Measured full run in a fresh worktree: **1363 passed / 48 failed / 5 skipped**, all 48 failures from one root cause — `System.InvalidOperationException : Bundled embedding model 'model_qint8_arm64.onnx' not found next to the tool`, thrown from `src/AiRaccoon.Infrastructure/Embedding/BundledModel.cs:79`. The asset is gitignored (`.gitignore:21`). The *same* missing precondition is handled gracefully by `Assert.Skip("bundled ONNX model not present (gitignored; absent on CI)")` in one test and by a hard failure in 48. | `dotnet test` is the project's declared test command (CLAUDE.md). A fresh clone or worktree cannot reach green without an undocumented bootstrap (`ai-raccoon model set local`), and neither `build.yml` nor `nightly.yml` runs one. The inconsistent handling means nobody can tell "the model is missing" from "embedding is broken". |
| **D6** | `tests/AiRaccoon.Tests/Unit/Retrieval/SweepRunnerTests.cs:25` and `:40` | **High** | `Matrix_IsDeterministicAcrossReads() => SweepMatrix.Points.Select(p => p.Id).ShouldBe(SweepMatrix.Points.Select(p => p.Id));` and `RrfGrid_IsDeterministicAcrossReads() => SweepMatrix.RrfGrid.ShouldBe(SweepMatrix.RrfGrid);`. `SweepMatrix.Points` / `RrfGrid` are `{ get; } = Build()` (`SweepMatrix.cs:27,35`) — initialised once at type init. Both sides read the identical cached object. | Cannot fail under any implementation. To test determinism, `Build()` must be invoked twice independently. The name and doc-comment claim run-to-run stability; the test proves an in-memory list equals itself. |
| **D7** | `tests/AiRaccoon.Tests/Unit/Watch/WatchServicePortTests.cs:34` | **High** | `Port_AddAndRemove_AreAwaitableWithoutThrowing` awaits `StubWatchService.AddAsync` / `RemoveAsync`, both `=> Task.CompletedTask` (`:14-16`), and asserts nothing. No production type is loaded. | Zero-assertion test against a hand-written no-op. Cannot detect any change to `IWatchService` or its real implementation. If the intent is to pin the port's shape, that is a compile-time concern, not a `[Fact]`. |
| **D8** | `tests/AiRaccoon.Tests/Unit/Encryption/BitwardenCliSecretManagerTests.cs:79` and `tests/AiRaccoon.Tests/Integration/EncryptionBitwardenIntegrationTests.cs:116` | **High** | Both mutate the process-global `BWS_ACCESS_TOKEN` with bare `Environment.SetEnvironmentVariable`, neither takes `TestData.EnvVarGate`. Neither class carries a `[Collection]`, so they run in parallel by default. `BitwardenCliSecretManager.Run` does not set `ProcessStartInfo.Environment`, so the child `bws` inherits whatever is global at `Process.Start`. The fake `bws` script validates the token (`EncryptionBitwardenIntegrationTests.cs:55`). | The project already solved this exact class of race for `AIRACCOON_DB_PASSPHRASE` — `TestData.cs:12` `EnvVarGate` is correctly taken at all four passphrase sites. The mitigation simply was not extended to the second env var. Genuine cross-class TOCTOU flake. |
| **D9** | `tests/AiRaccoon.Tests/Unit/Watch/WatchEventSourceTests.cs:14`, `:218`, `:303` | **Medium** | Drives a **real** `FileSystemWatcher` with `Thread.Sleep(25)` polling to 3000ms and a bare `Thread.Sleep(300)` before a negative assertion — but carries no `[Collection]`, unlike every sibling real-FS-watcher class (`Integration/WatchIntegrationCollection.cs:11`, `BDD/FileWatcherCollection.cs:12`, both of which document the flake they prevent). It is also `Speed=Fast`, so it *does* run in PR CI. | The one real-FS-watcher class not covered by the project's own documented mitigation. Separately, `Thread.Sleep(300)` + `ShouldNotContain` can only ever prove "no event within 300 ms" — a slow CI box turns a real regression into a pass. |
| **D10** | `tests/AiRaccoon.Tests/Unit/Setup/McpServerSetupHostTests.cs:159-169` | **Medium** | `RunAsync_HttpHost_StartsAndStopsCleanly` — `RunAsync`, `await Task.Delay(300)`, `StopAsync`, `await runTask`. No assertion of any kind; no liveness probe. | The name promises the host started. Replace `RunAsync`/`StopAsync` with no-ops and the test still passes. Also burns 300 ms of real wall clock for nothing. |
| **D11** | `tests/AiRaccoon.Tests/Unit/Setup/Serve/ServeRunnerTests.cs:406-413`, `tests/AiRaccoon.Tests/Unit/Setup/McpServerSetupHostTests.cs:293-300` | **Medium** | `FreePort()` binds a `TcpListener` on port 0, reads the assigned port, then `Stop()`s and hands the bare number to a server that rebinds later. ~10 call sites. | Classic release-then-rebind port race under parallel execution. The codebase already knows the fix — `ServeRunnerTests.cs:366-403` `TryHoldLoopbackPort` keeps the listener alive precisely "so … no port race" — but that pattern is not reused generically. |
| **D12** | `docs/work/features-file-watcher/file-watcher.feature` (430 lines, 62 scenarios) | **Medium** | A near-duplicate of the live `docs/features/file-watcher/file-watcher.feature` (435 lines, 63 scenarios). Only the latter is compiled (`AiRaccoon.Tests.csproj:45-47`). `git log` shows the stale copy last touched in PR #2 while the live one moved on in PR #22; they now differ by 7 lines including a whole missing scenario. | A file that looks like an executable spec, reads like an executable spec, and is not executed. Anyone editing it changes nothing and gets no feedback. |
| **D13** | `tests/AiRaccoon.Tests/BDD/NativeMemorySteps.cs:449` | **Medium** | `ThenSchemaForbidsBothWorkspaceAndCommitted` asserts the CHECK constraint by string-matching the DDL in `sqlite_master`: `sql.ShouldContain("(workspace_id IS NULL AND scope IN ('shared','project','custom')) OR …")`. | Pins the *text* of the constraint, not its effect. If SQLite ever failed to enforce it, or the constraint were reworded semantically-equivalently, the test's verdict would be wrong in both directions. The behavioural version — attempt the illegal insert, expect a failure — is strictly stronger and no harder to write. |
| **D14** | `tests/AiRaccoon.Tests/BDD/NativeMemorySteps.cs:255` | **Medium** | `[Then("no external server process or download is required")]` asserts `settings['embedding.engine'] == 'local:bundled'`. | A configuration value cannot demonstrate the absence of a process launch or a network fetch. A regression where the local engine downloads its model at first run passes this step. Same shape as `:1086` `ThenNoExtensionDownloaded() { }`, which at least does not pretend. |
| **D15** | `tests/AiRaccoon.Tests/BDD/NativeMemorySteps.cs:1072` | **Low** | `[Then("memory_stats for project \"acme-web\" is unchanged")]` asserts `stats.EntryCount.ShouldBe(0)`. | Degenerate fixture: "unchanged" measured from zero. It cannot distinguish *unchanged* from *cleared*, and would read identically if the scenario's setup silently stopped writing. Fixture realism requires a non-zero starting count. |
| **D16** | `tests/AiRaccoon.Tests/Unit/Retrieval/GoldenFileTests.cs:45-56` | **Low** | `CommittedGoldenFile_EveryHitCarriesHashRankingPathAndSnippet` asserts `golden.Queries.ShouldNotBeEmpty()` but then loops `foreach (var hit in query.Hits)` with no per-query count assertion. | A query with zero hits passes vacuously. One extra `query.Hits.ShouldNotBeEmpty()` closes it. |
| **D17** | `tests/AiRaccoon.Tests/Unit/Setup/McpServerSetupHostTests.cs:172` | **Low** | Doc comment reads "the full 20 tools: 17 memory + 3 watch"; the test body one line down asserts `toolNames.Count.ShouldBe(22)`. | Stale comment contradicting its own test. Same 17-vs-22 drift as D4, here caught by the assertion but not by the prose. Violates the project's "minimal comments" invariant twice over. |
| **D18** | `tests/AiRaccoon.Tests/Unit/{search,storage,sweep,sync,workspace}/` | **Low** | Five test folders are lowercase while the other thirteen are PascalCase. `DegradationPolicyTests.cs` lives under `Unit/Rating/`, not a `Unit/Degradation/` mirroring `src/*/Degradation/`. | Minor, but it breaks the "find the test next to its concept" property that the rest of the tree earns. |
| **D19** | `tests/AiRaccoon.Tests/Unit/storage/`, `Unit/sync/`, `Unit/search/`, `Integration/` | **Medium** | 58 public async methods in `src/` take a `CancellationToken`. Cancellation is genuinely exercised only in `Unit/Setup/Serve/IdleWatchdogTests.cs`, `Unit/Maintenance/BankMaintenanceHostedServiceLifecycleTests.cs`, `Unit/Extraction/ExtractionHostedServiceTests.cs` and `Unit/Embedding/BundledModelEnsureDownloadsTests.cs`. A grep for `.Cancel()` / `OperationCanceledException` across `Unit/storage`, `Unit/sync`, `Unit/search` and `Integration/` returns **no cancellation test at all** — every occurrence is `CancellationToken.None` or `TestContext.Current.CancellationToken`. | The lifecycle-critical paths are covered, which is the right priority. But a `SqliteMemoryStore.SearchAsync` or `SyncService` that ignores its token — hanging a shutdown or leaking a connection — would be a silent regression. |
| **D23** | `src/AiRaccoon.Infrastructure/Sync/SyncService.cs:126-183` | **Critical** | The entire ETag/If-Match conflict-resolution loop is untested. `SyncService` retries up to `MaxPushRetries` on `SyncConflictException`, and on each retry re-pulls, re-merges (`MergeRemoteAsync`), waits for a WAL checkpoint, re-runs `VACUUM INTO` and re-reads the snapshot (`:145-177`); on exhaustion it logs `Log.SyncConflictExhausted` and rethrows (`:180-182`). **No test anywhere drives `SyncConflictException` into `SyncService`** — `grep -rn "SyncConflictException\|MaxPushRetries\|SyncConflictExhausted" tests/` hits only `AzureBlobCloudStoreTests.cs:185,214` (the *store* throwing it) and `Observability/ToolCallMetricsTests.cs` (the string as a metric tag). I checked the one candidate, `SyncServiceTests.cs:424 MemorySync_WithConflictingRemoteEntry_MergesContentAddressed` — it uses a bare `FakeCloudStore` with no ETag seeded, so it exercises content-addressed merge, never the 412 path. | This is the most complex untested code in the repo: a retry loop that re-merges and re-snapshots real data. A regression that dropped a retry, re-merged into the wrong direction, or silently swallowed the exhaustion would lose user writes with no test going red. `FakeCloudStore.cs:27` can already throw the exception — the fixture to test this exists and is unused. |
| **D24** | `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs` | **High** | There is **no schema-version marker at all** — repo-wide grep for `user_version`, `schema_version`, `SchemaVersion` across `src/` returns nothing. `MigrateAsync` infers state from column/index presence. Consequently there is no guard against an older binary opening a bank written by a newer version; it will be silently accepted and migrated against an unknown shape. | Not a test gap but a design gap that *makes* a test gap: the brief's question "is the newer-version guard tested?" has no answer because the guard does not exist. With cloud sync in the picture, a newer client's snapshot reaching an older client is a realistic path to silent corruption. Worth an ADR, not just a test. |
| **D25** | `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:799`, `:849` | **High** | `EmbedIfConfiguredAsync` and `EmbedBatchAsync` call `generator.GenerateAsync(...)` with **no try/catch**. A failing or timing-out OpenAI-compatible provider propagates straight out of `WriteAsync`/`IngestFileAsync`. `tests/AiRaccoon.Tests/Unit/Embedding/FakeEmbeddingEndpoint.cs:88-97` only ever returns 200 with valid JSON — no test simulates a 5xx, a timeout, or a malformed body at write time. | The intended contract is genuinely ambiguous from the tests: does the whole write fail, or should the row stay `pending` for `memory_embed_pending` to retry? Both readings are defensible and neither is pinned, so a change in either direction is silent. Given the store already has a `pending` state, "write succeeds, embedding deferred" is very likely the intent — and nothing enforces it. |
| **D26** | `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:725-728` | **Medium** | `QueryFtsBatchAsync` ends `catch (SqliteException) { return []; }` — a deliberate degrade documented at `:710-711` ("a failed keyword modality degrades to the vector list"). No test forces this catch and asserts search still returns vector-only results. | The catch is unfiltered: it swallows *every* `SqliteException`, not just tokenizer limits — a locked database, a missing table, or a malformed generated SQL all silently become "zero keyword hits". Hybrid search would quietly become vector-only and every retrieval gate would still pass, because those gates measure ranking, not modality health. |
| **D27** | `src/AiRaccoon.Infrastructure/Sync/SyncService.cs:298-307` | **Medium** | `applyTombstones` deletes any row matching `(hash, scope)` against a merged tombstone with no `created_at`/watermark comparison, so a row legitimately re-created with the same hash *after* a remote tombstone is deleted again on the next sync. `SyncServiceTests.cs:188-255 MemorySync_TombstonePropagation_NoResurrection` proves only that a stale remote copy doesn't resurrect a row the same client deleted — it never re-creates the tombstoned hash and re-syncs. | Content-addressed hashes make re-creating a deleted fact routine (write the same text again). Silent re-deletion of a fact the user just re-added is a data-loss bug that would be very hard to attribute. |
| **D28** | `src/AiRaccoon.Infrastructure/Watch/WatchCatchUp.cs:50-65`, `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:374-380` | **Medium** | Two untested failure branches. `WatchCatchUp.ScanCoreAsync` has `catch (Exception ex) { Log.ScanError(...); }` — a catch-all swallow; none of the 8 tests in `Unit/Watch/WatchCatchUpTests.cs` forces an enumeration failure or asserts EventId 310 fires. `SqliteMemoryStore.DeleteSourcePathAsync` has `BEGIN IMMEDIATE` / `catch` / `ROLLBACK` / `throw`; all four of its tests (`Integration/SqliteMemoryStoreIntegrationTests.cs:254,271,291,314`) are happy-path, so no test proves the rollback leaves prior rows intact. | A directory removed mid-scan is a normal occurrence for a file watcher, and the swallow is the only thing standing between it and a crash — untested. A broken rollback in a multi-row delete is silent partial data loss. |
| **D29** | `tests/AiRaccoon.Tests/BDD/NativeMemorySteps.cs:449-457` (extends D13) | **Medium** | Beyond the workspace-XOR-scope CHECK already flagged in D13, the `scope IN ('shared','project','custom')` and `embed_state IN ('pending','embedded')` CHECKs (`MemorySchema.cs:33,44`) have **no** behavioural test either. The single counter-example is `Unit/storage/SqliteMemoryStoreSchemaTests.cs:226-240 UniqueIndex_RejectsDuplicateBucketInsert`, which does it correctly — attempts the illegal insert and expects the failure. | The right pattern already exists in the codebase, one file away, and was not applied to the CHECK constraints. A migration that dropped or malformed a CHECK goes green. |
| **D30** | `src/AiRaccoon.Infrastructure/Sqlite/SqliteConnectionFactory.cs:89-102` | **Low** | `BuildConnectionString` skips setting `Password` entirely when `key is null` — a distinct code path from wrong-but-present key. `Unit/storage/SqliteConnectionFactoryEncryptionTests.cs:175-216` covers wrong key and resolver-returns-different-key, but nothing opens an *encrypted* bank with a **null/unset** key and asserts a clean, diagnosable failure. | The one hole in an otherwise exemplary encryption suite, and it is the most likely real-world case: an operator whose `AIRACCOON_DB_PASSPHRASE` is simply not exported. Worth pinning the error message, not just the failure. |
| **D22** | `src/AiRaccoon.Infrastructure/Sync/CloudSyncConnectionFactory.cs` | **Low** | No test file anywhere references `CloudSyncConnectionFactory` (`grep -rl` across `tests/` returns nothing), unlike its siblings `SyncCloudStoreFactory`, `AzureBlobCloudStore`, `S3CloudStore` and `NullCloudStore`, which each have dedicated tests under `tests/AiRaccoon.Tests/Unit/sync/`. | The one untested component in an otherwise well-covered sync layer. Low severity because the surrounding taxonomy is well pinned — but it is the seam where a mis-wired connection would land. |
| **D21** | `src/AiRaccoon.Infrastructure/Sync/SyncService.cs:25` (`SemaphoreSlim`), `Watch/WatchPipeline.cs` (`Channel<>` + `lock`), `Watch/WatchEventSource.cs` (`lock (_gate)`), `Maintenance/TickSignal.cs` (`lock`), `Embedding/EmbeddingService.cs:31,66` (`ConcurrentDictionary`) | **Medium** | These five hold explicit concurrency control that no test ever contends — every test drives them sequentially. `EmbeddingService.CreateGenerator`'s `GetOrAdd` race (two callers, same fingerprint) is never raced; `WatchEventSource`'s `lock (_gate)` guards state that a real `FileSystemWatcher` mutates from a thread-pool thread while the hosted service calls `Start`/`Stop`, yet every test in `WatchEventSourceTests.cs` is single-threaded. | A regression that removed or mis-scoped `SyncService`'s gate (double upload, lost update) would be completely silent. Awaiting calls one after another is not a concurrency test. **Correction to an earlier draft of this finding: `WatchScheduler` does NOT belong on this list** — see the strengths section. |
| **D20** | `tests/AiRaccoon.Tests/` (21 sites) | **Low** | 21 separate sites construct `new SqliteMemoryStore(...)` with their own hand-rolled `SqliteConnectionFactory` + options + chunker + embedding wiring. `TestData.cs` offers only `CreateTempRoot`, `CreateInfrastructureOptions`, `CreateBundledModel` and two math helpers — there is no store builder. | Copy-pasted setup. A constructor signature change touches 21 files; more importantly, each site is free to differ subtly (which chunker, which key resolver) with nothing making that choice visible. |

### Appendix A — the vacuous BDD scenarios

Computed by parsing the four compiled feature files, matching each `When`/`Then` to its binding
regex, and checking the binding body. Scenarios where **every** act/assert step resolves to an
empty binding:

| Feature | Scenario |
|---|---|
| `docs/work/features-agent-memory/agent-memory.feature:15` | The usage prompts are listed |
| `docs/work/features-agent-memory/agent-memory.feature:22` | Every tool requires a project id |
| `docs/work/features-agent-memory/agent-memory.feature:139` | No secrets are tracked |
| `docs/work/features-native-memory/native-memory.feature:48` | Forgetting knobs are denied in rw |
| `docs/work/features-native-memory/native-memory.feature:53` | Forgetting knobs are applied in full |
| `docs/work/features-native-memory/native-memory.feature:198` | All 17 tools are still listed |
| `docs/work/features-native-memory/native-memory.feature:207` | No sqliteai extension is provisioned at runtime |
| `docs/work/features-native-memory/native-memory.feature:214` | A markdown note is split with token bounds and overlay |
| `docs/work/features-native-memory/native-memory.feature:218` | Code fences are not split |

Scenarios with at least one empty act/assert step:

| Feature | Scenario | Empty step |
|---|---|---|
| `agent-memory.feature:129` | A local-only install syncs its bank to the cloud database | `Then any user-scope instance correlates with it only through that cloud database` |
| `native-memory.feature:14` | The bank opens exactly one database file | `When I inspect the bank directory` |
| `native-memory.feature:33` | ro mode allows reading only | `Then the tool errors with access-denied` |
| `native-memory.feature:62` | The global mode can be tightened to ro | `Then the tool errors with access-denied` |
| `native-memory.feature:111` | A keyword-only query with no vector hits still returns keyword results | `Then keyword results are returned above the minimum score`; `Then no error is raised` |
| `native-memory.feature:116` | Fusion parameters are configurable | `Then the ranking reflects the configured parameters` |
| `native-memory.feature:201` | memory_configure accepts a base URL | `Then the provider is configured with that endpoint` |
| `native-memory.feature:204` | No Semantic Kernel dependency is introduced | `When I scan project package references` |
| `docs/features/file-watcher/file-watcher.feature:414` | A ro agent's add is rejected with an access error | `Then the tool errors with access-denied` |

Note the aggravating detail on `native-memory.feature:33`: its `Given project "acme-web" is in mode
ro` binding (`NativeMemorySteps.cs:1038-1039`) is *also* empty, so the mode is never set, and its
`When I call memory_write` binding (`:978`) writes straight through `_store.WriteAsync`, bypassing
`MemoryTools`' guard entirely. The scenario performs a successful write and then asserts nothing
about denial.

**Mitigating fact, stated plainly:** the underlying access-mode behaviour *is* genuinely covered
elsewhere — `Unit/Access/AccessModePolicyTests.cs` (10 tests), `Unit/Access/AccessModeGuardTests.cs`
(8 tests incl. `Ensure_Denied_ThrowsAccessDeniedWithRequiredAndCurrentMode`),
`Unit/Mcp/WatchToolsAccessModeTests.cs` (`RoMode_AddIsDenied`, `RoMode_RemoveIsDenied`,
`RoMode_StatusIsAllowed`) and `Unit/Mcp/MemoryToolsAccessModeTests.cs`. This is why D2 is scoped as
"the contract document is hollow", not "access control is untested".

---

## The 4 (actually 5) skipped tests

The reported figure of 4 is the Reqnroll count. The measured run reports **5 skipped**.

| Skip | Mechanism | Why | Verdict |
|---|---|---|---|
| `The server asks the agent which hashes to keep via MRTR` | `@ignore`, `agent-memory.feature:145` | Open question OQ-4; the feature file itself records the V1 fallback ("plain tool call with an explicit keep list, as specified above") in a comment on the next line. | **Correctly skipped.** The V1 behaviour it defers to has its own scenarios. Not a hole. |
| `Project isolation is enforced on the cloud side via row-level security` | `@ignore`, `agent-memory.feature:151` | Open question OQ-5; fallback documented inline (the cloud DB is the correlation point). | **Correctly skipped** — it describes a cloud-side control that does not exist in this codebase. |
| `The memory inspects itself through memory_inspect` | `@ignore`, `native-memory.feature:66` | Explicitly "Part 2: introspection tool…". No `memory_inspect` tool exists in `src/`. | **Correctly skipped** — unbuilt feature. |
| `The store emits metrics and tracing for its own operations` | `@ignore`, `native-memory.feature:69` | Marked "Part 2". | **This one is a hole.** Metrics and tracing *have since shipped* — `src/AiRaccoon/Observability/` exists and `tests/AiRaccoon.Tests/Unit/Observability/` has 5 test files covering `ToolExecutionActivity`, `MemoryToolsInstrumentation` and `McpExceptionPathInstrumentation`. The scenario is skipped as unbuilt while the thing it describes is built and tested. Stale `@ignore`. |
| `BundledModelLoggingTests.EnsureAsync_WhenAssetsVerified_LogsDebug` | `Assert.Skip`, `BundledModelLoggingTests.cs:49` | Conditional: bundled ONNX model absent. | **Environmentally correct, but see D5** — 48 sibling tests hard-fail on the identical precondition instead of skipping. The inconsistency is the finding, not this skip. |

---

## Test architecture

**Layering.** Four tiers, cleanly named: `Unit/` (pure logic + fakes), `Integration/` (real SQLite
and native extensions), `E2E/` (full server over HTTP via `WebApplicationFactory`), `BDD/` (Reqnroll
step definitions). `TestCategories.cs:9-13` documents the taxonomy and the filter syntax. Traits are
applied consistently — 103 `Fast` / 33 `Slow` markers.

**Mirroring.** `Unit/` largely mirrors the production domain folders (`Access`, `Chunking`,
`Embedding`, `Encryption`, `Extraction`, `Maintenance`, `Memory`, `Observability`, `Rating`,
`Watch`, `Workspace`). Deviations: five lowercase folders (D18), `Retrieval` and `Mcp` and `Setup`
have no direct `src/` counterpart (they map to `benchmarks/`, `src/AiRaccoon/Tools/` and
`src/AiRaccoon/Setup/` respectively — reasonable), and `Degradation` is tested under `Rating/` and
`sweep/` rather than a folder of its own. No generic technical buckets — no `Services/`, no
`Helpers/`. This satisfies the screaming-architecture invariant better than most test trees.

**Where the BDD sits, and whether it earns its keep.** The `.feature` files live in `docs/` — two in
`docs/features/` (shipped contracts) and two in `docs/work/features-*/` (working drafts) — and are
pulled into the test project by `<ReqnrollFeatureFiles Include="..\..\docs\...">` links
(`AiRaccoon.Tests.csproj:38-51`). Keeping the spec in `docs/` and compiling it from there is a good
decision: the contract lives where a human reads it, and it is executable.

The verdict splits by feature:

- **`FileWatcherSteps.cs` (1509 lines, 151 bindings, 1 empty) — earns its keep.** The file-watcher
  domain is genuinely stateful and temporal: watch registration, debounce, digest, rename, delete,
  access tier. Gherkin's Given/When/Then reads better than 63 xUnit methods would, and the bindings
  do real work against a real store.
- **`EncryptionBitwardenSteps.cs` (385 lines, 40 bindings, 1 empty) — earns its keep.** Same
  reasoning; the scenarios describe an operator workflow with a real subprocess.
- **`NativeMemorySteps.cs` (1537 lines, 172 bindings, **42 empty**) — does not.** This is the file
  that turned into ceremony. A quarter of its bindings are placeholders, ten of them carrying a
  comment that delegates the assertion to a unit test ("verified by ToolInventoryTests", "verified at
  build time"). That delegation pattern is the core problem: it converts a *failing* signal into a
  *passing* one. If a scenario's assertion genuinely belongs in a unit test, the scenario should be
  deleted or `@ignore`d, not stubbed green.

**Overlap.** The BDD and xUnit layers overlap substantially on the native-memory domain — workspace
lifecycle, sharing, sweep, sync all have both scenario coverage and dedicated unit tests
(`Unit/workspace/`, `Unit/sweep/`, `Unit/sync/`, `Unit/Memory/`). Given that the xUnit tests are the
ones that actually assert, and the ones that run in PR CI, the native-memory Gherkin is currently
paying maintenance cost for signal it does not produce.

---

## Determinism and flakiness

Measured: **2** `Thread.Sleep` occurrences, **48** `Task.Delay`, longest real wall-clock timeout
**90 s** (`ServeRunnerTests.cs:316`).

The dominant pattern is sound and deliberate: `FakeTimeProvider` in 29 files, with a short real
`Task.Delay(10–100 ms)` only to yield the scheduler so a background timer observes the *fake* clock,
bounded by a real deadline that throws `TimeoutException`. `BankMaintenanceHostedServiceLifecycleTests.cs:42-65`
states this explicitly ("no polling sleeps; the fake clock drives the timer"). Large
`TimeSpan.FromMinutes(30)` values are `FakeTimeProvider.Advance` calls, not real waits.

Parallelism: no `xunit.runner.json`, no assembly-level `[CollectionBehavior]` — xUnit v3 defaults
(class-per-collection, collections in parallel). Five explicit opt-outs, each with a written reason
naming the specific global it protects (`Console.SetOut`, a global `ActivitySource`, real
`FileSystemWatcher` + wall-clock polls, process env for the E2E server). `ObservabilityCollection.cs:14`
even cites the flake it was created for. This is the right instinct, applied unevenly — D8, D9 and
D11 are all cases where the project invented a mitigation and then did not apply it to a sibling.

Temp state is safe: `TestData.CreateTempRoot` (`TestData.cs:14-19`) is `GetTempPath()/prefix/GUID`,
unique per call. The shared `Resources/jsaa-memory.db` is consumed by 7 integration classes, but
every one `File.Copy`s it into a per-test data root before opening (e.g. `QueryConstructionTests.cs:41-43`)
— read-only source, copy-on-use. No shared-write hazard.

**The "environmental ReferenceAssets copy flake", mechanism resolved.** `ReferenceAssets.cs:194-222`
resolves the assets directory in four steps: `AIRACCOON_HARNESS_ASSETS` env var → walk up from
`AppContext.BaseDirectory` looking for the source-tree `manifest.json` → fall back to the
`CopyToOutputDirectory` copy (`AiRaccoon.Tests.csproj:34`) → throw `InvalidOperationException`
naming both the missing path and the escape hatch. Git history shows the original bug was a
directory-casing mismatch with no fallback. **The failure mode today is loud, not silent** — a
missing manifest throws at first touch of the type; a missing or SHA-mismatched binary populates
`EnsureResult.Errors`, which `ReferenceAssetGateTests.cs:110-115` asserts empty with a remediation
message. This is the right design and I found no silent-pass path. The residual risk is different:
`ReferenceAssets.EnsureAsync` performs real `HttpClient` fetches to GitHub/HuggingFace when the
local copy is absent, so these tests need network. That is deliberate and fails loudly. Note the
same fallback-path-walk idiom appears in `QueryConstructionTests.cs:306-316` and
`FindProjectRoot()` (`:318`), which locate assets by walking up for `AiRaccoon.slnx` — these depend
on the repo layout, not just the build output.

**Golden-file honesty — cleared.** `GoldenFileTests.cs:17-38` has a regeneration escape hatch
(`AIRACCOON_HARNESS_REGENERATE_GOLDEN=1` writes the file and returns before asserting), but it is
opt-in, documented, absent from both workflows, and mirrors the standard snapshot-update convention.
The default path runs `GoldenFile.Differences` (`GoldenFile.cs:68-120`), a real exact
hash/path/order comparison with a 1e-6 ranking tolerance. `ReferenceAssetGateTests` pins SHA-256
against a hardcoded constant with no self-heal. I looked for a rubber-stamp here and did not find
one.

---

## Cost

**Measured, this worktree, this machine** (`/usr/bin/time -p dotnet test`, cold build):

```
real 279.10   user 172.05   sys 52.38
Total tests: 1416   Passed: 1363   Failed: 48   Skipped: 5
xUnit reported total time: 3.31 minutes
```

So **4m39s wall clock including build**, ~3m20s of test execution. That is *not* slow enough to
discourage running it — it is a perfectly reasonable full-suite cost for 1416 tests against real
SQLite. The `Speed=Fast` filter exists to make an even faster inner loop, which is a good idea
poorly applied (D1): the filter's job should be "fast feedback while you work", not "the merge
gate". I did not separately time the `Speed=Fast` subset; the 1008/1416 split is measured, the
subset's wall clock is **UNVERIFIED**.

The one gratuitous cost I found is D10's `Task.Delay(300)` in a test that asserts nothing.

---

## Refactor opportunities

Ordered by value. The "shippable" column is the parallel-execution plan.

**R1 — Make the PR gate run the suite it claims to. (D1)**
Current shape: `build.yml:28` filters `Speed=Fast`; 408 tests never gate a merge. Proposed shape:
either drop the filter from `build.yml` (measured cost: ~4m39s cold, acceptable) or, if the fast
loop must be preserved, keep `Speed=Fast` as a *first* job and add a required `Speed=Slow` + BDD
job — and tag the feature files `@Speed:Fast`/`@Speed:Slow` so Reqnroll scenarios stop falling
through the filter entirely.
Blast radius: `.github/workflows/build.yml` (1 line), optionally 4 `.feature` files (tag lines).
Effort **S**. Risk: **medium** — turning on 408 previously-ungated tests will surface whatever has
rotted behind them, and D5 means the job will be red until the model bootstrap is fixed.
**Must serialise with R2** (the gate cannot go green while 48 tests fail on a missing asset).

**R2 — Make a fresh clone able to run `dotnet test`. (D5)**
Current shape: 48 tests hard-throw on a gitignored ONNX file; one test skips gracefully on the same
condition; no workflow bootstraps it. Proposed shape: pick one policy and apply it everywhere —
either a bootstrap step in both workflows plus a documented `dotnet test` prerequisite in the
README, or extend the `Assert.Skip` guard to every test that needs the model (a small shared helper,
e.g. `TestData.RequireBundledModel()`). The former is better: skipping 48 tests silently is how D1
happened.
Blast radius: `.github/workflows/build.yml` + `nightly.yml`, README/contributing docs, and — if the
skip route is chosen instead — ~10 test files under `Unit/Embedding/`, `Integration/`, `E2E/`.
Effort **S**–**M**. Risk: low.
**Independently shippable.** R1 depends on it, not the other way round.

**R3 — Empty the BDD placeholders honestly. (D2, D3, D4, D13, D14, D15)**
Current shape: 42 empty bindings in `NativeMemorySteps.cs` producing 18 vacuous or partly-vacuous
green scenarios, plus one binding (`:863`) asserting something unrelated. Proposed shape: for each,
choose one of three — (a) implement the assertion, (b) delete the scenario if a unit test already
owns it, (c) `@ignore` it with a reason. Never leave `{ }`. Start with `:1134`
`ThenAccessDenied` and `:863` — those are the security-shaped ones. While in there, fix the
17-vs-22 tool drift in `native-memory.feature:198` (D4) and convert the DDL string-match at `:449`
to a behavioural insert-and-expect-failure (D13).
Blast radius: `tests/AiRaccoon.Tests/BDD/NativeMemorySteps.cs` (~1537 LOC, heavily touched),
`docs/work/features-native-memory/native-memory.feature`, `docs/work/features-agent-memory/agent-memory.feature`,
`docs/features/file-watcher/file-watcher.feature` (one step). Effort **L**. Risk: medium — expect
some newly-implemented assertions to fail, which is the point.
**Must serialise with R1** — both touch what the gate runs, and R3's newly-honest scenarios should
land before or with the gate that starts enforcing them. Does not conflict with R2 or R4–R7.

**R4 — Delete or fix the four dishonest xUnit tests. (D6, D7, D10, D16)**
Current shape: two self-comparison tautologies, one zero-assertion stub test, one zero-assertion
host test, one vacuous loop. Proposed shape: `SweepRunnerTests` — call `Build()`/`BuildRrfGrid()`
twice via a test-visible seam and compare, or delete the two "determinism" facts as unfalsifiable;
`WatchServicePortTests.Port_AddAndRemove` — delete; `McpServerSetupHostTests.RunAsync_HttpHost` —
add a real liveness probe or reduce to an explicit `Should.NotThrowAsync`;
`GoldenFileTests` — add `query.Hits.ShouldNotBeEmpty()`.
Blast radius: 4 files, ~30 LOC. Effort **S**. Risk: very low.
**Independently shippable.** No overlap with any other item.

**R5 — Extend the existing flake mitigations to their missed siblings. (D8, D9, D11)**
Current shape: `EnvVarGate` guards `AIRACCOON_DB_PASSPHRASE` but not `BWS_ACCESS_TOKEN`;
`WatchEventSourceTests` drives a real `FileSystemWatcher` without the `[Collection]` its siblings
have; `FreePort()` releases the port before the server rebinds while `TryHoldLoopbackPort` next
door does it correctly. Proposed shape: take `TestData.EnvVarGate` at the two `BWS_ACCESS_TOKEN`
sites (or add a second gate); add `WatchEventSourceTests` to a serial collection; replace
`FreePort()` with a hold-the-listener helper shared by both call sites.
Blast radius: `TestData.cs`, `Unit/Encryption/BitwardenCliSecretManagerTests.cs`,
`Integration/EncryptionBitwardenIntegrationTests.cs`, `Unit/Watch/WatchEventSourceTests.cs`,
`Unit/Setup/Serve/ServeRunnerTests.cs`, `Unit/Setup/McpServerSetupHostTests.cs`. ~6 files, small
diffs. Effort **S**. Risk: low — the `[Collection]` addition serialises a class, marginally slowing
the run.
**Independently shippable**, but note it touches `TestData.cs`, which **R7 also touches** — serialise
those two or split the `TestData.cs` edit into whichever lands first.

**R6 — Delete the stale duplicate feature file and the stale `@ignore`. (D12, skip #4)**
Current shape: `docs/work/features-file-watcher/file-watcher.feature` is an uncompiled 430-line
near-duplicate that has already drifted; `native-memory.feature:69`'s `@ignore` marks
metrics/tracing as unbuilt when `src/AiRaccoon/Observability/` ships and has 5 test files.
Proposed shape: delete the duplicate; un-`@ignore` the metrics scenario and bind it to the existing
observability behaviour (or delete it and cite `Unit/Observability/` as owner).
Blast radius: 2 files. Effort **S**. Risk: very low.
**Independently shippable** — unless R3 lands first, in which case fold the `@ignore` half into it.

**R7 — Introduce a store fixture/builder. (D20)**
Current shape: 21 sites hand-roll `new SqliteMemoryStore(new SqliteConnectionFactory(options,
resolver), timeProvider, chunker, embedding)`. Proposed shape: a `MemoryStoreBuilder` in
`TestHelpers/` with named defaults and explicit overrides (`.WithFakeClock()`, `.WithRealChunker()`,
`.Encrypted(passphrase)`), so each test's *deviation* from the default is the visible part.
Blast radius: new file + 21 call sites across `Unit/storage/`, `Unit/Extraction/`, `Integration/`,
`BDD/`, `E2E/`. Effort **M**. Risk: low but wide — a pure mechanical refactor over many files.
**Must serialise with R5** (shared `TestData.cs`) and is best kept away from **R3** (both touch
`BDD/` heavily). Otherwise independent.

**R9 — Test the sync conflict-retry loop. (D23, D27)**
Current shape: `SyncService.cs:126-183`'s If-Match retry/re-pull/re-merge/re-snapshot loop and its
exhaustion path have no test; the tombstone-vs-genuine-resurrect case is unexercised. Proposed
shape: `FakeCloudStore` can already throw `SyncConflictException` (`FakeCloudStore.cs:27`) — drive
it into `SyncService.MemorySyncAsync` for (a) conflict-then-success on retry 2, asserting the
re-merged rows landed and the ETag watermark advanced, (b) conflict on all `MaxPushRetries`,
asserting the rethrow and that `Log.SyncConflictExhausted` fired, (c) delete a row, sync, re-create
the same content, sync again, assert it survives.
Blast radius: `tests/AiRaccoon.Tests/Unit/sync/SyncServiceTests.cs` only (plus possibly a small
`FakeCloudStore` seam for "fail the first N pushes"). Effort **M**. Risk: low for the tests;
**(c) may well go red**, which would be a genuine data-loss bug found.
**Independently shippable.** Highest expected value per hour of any item here.

**R10 — Decide and pin the embedding-failure contract. (D25, D26)**
Current shape: `GenerateAsync` at `SqliteMemoryStore.cs:799,849` is uncaught, so a provider outage
fails the whole write; `QueryFtsBatchAsync:725` swallows every `SqliteException` into an empty
keyword list. Proposed shape: decide whether a provider failure should leave the row `pending`
(likely, given the state exists) and pin it with a failing `FakeEmbeddingEndpoint` mode (5xx,
timeout, malformed body); narrow the FTS catch to the tokenizer-limit case it documents and add a
test that a pathological MATCH degrades to vector-only results rather than to silence.
Blast radius: `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs`,
`tests/.../Unit/Embedding/FakeEmbeddingEndpoint.cs`, plus new tests. Effort **M**. Risk: medium —
this changes production behaviour, so it needs the contract decided first, not just tests added.
**Independently shippable**, but the *decision* should precede the code.

**R8 — Add cancellation and contention tests to the store and sync layers. (D19, D21)**
Current shape: no cancellation test anywhere in `Unit/storage/`, `Unit/sync/`, `Unit/search/` or
`Integration/`. Proposed shape: for `SqliteMemoryStore.SearchAsync`/`WriteAsync`/`IngestDirectoryAsync`
and `SyncService`'s push/pull, add a pre-cancelled-token test asserting `OperationCanceledException`,
and one mid-flight cancellation on the long-running ingest path. Then copy the `Barrier(2)` pattern
already proven in `SqliteMemoryStoreTests.cs:650-760` (or the `GatedRunner` idiom from
`WatchSchedulerTests.cs:22-41`) onto `SyncService`'s `SemaphoreSlim` and `EmbeddingService`'s
`GetOrAdd` — two concurrent pushes must not double-upload; two concurrent generator requests for
the same fingerprint must yield one engine.
Blast radius: 5–7 new test methods in existing files (`Unit/sync/SyncServiceTests.cs`,
`Unit/Embedding/`, `Unit/storage/`). Effort **S**–**M**. Risk: low — may reveal
that a token is genuinely ignored or a lock genuinely mis-scoped, which is the finding.
**Independently shippable**, but overlaps `Unit/sync/SyncServiceTests.cs` with **R9** — land R9
first, then add contention on top.

Suggested parallel plan: **R2 → R1** in one lane; **R3 → R6** in a second; **R9 → R8** in a third
(shared `Unit/sync/SyncServiceTests.cs`); **R4**, **R10** free in a fourth; **R5 → R7** serialised
in a fifth. R9 is the highest expected value; R1/R2 are the highest structural value.

---

## What is already good

Not padding — these are the things I tried to poke holes in and could not.

- **`SqliteMemoryStore` is tested against a real SQLite file, not a mock and not `:memory:`.** For a
  store whose contract *is* FTS5 + vector SQL + CHECK constraints, that is the only honest choice.
  Each test gets its own temp root; the shared corpus DB is copied, never opened in place.
- **The E2E layer speaks the real protocol.** `McpServerToolSurfaceE2ETests.cs:80-131` calls
  `ListToolsAsync` and then round-trips every tool not already covered, parsing the actual JSON
  envelope (`GetProperty("data").GetProperty("deleted").GetInt32().ShouldBe(1)`). It uses
  `WebApplicationFactory`'s in-memory `TestServer`, so there is no real port to collide on.
- **`ToolInventoryTests.cs:38` and `McpServerSetupHostTests.cs:185` both pin the tool count at 22**,
  and the latter's doc comment records exactly why (`PR #30 dropping .WithTools<WatchTools>()` made
  the watch trio silently vanish). A regression gate that names the regression it exists for is the
  gold standard.
- **The `[Collection]` opt-outs are reasoned, not cargo-culted.** Five of them, each naming the
  specific process-global it protects, two of them citing an observed flake. Most suites either
  serialise everything or nothing.
- **The golden-file gate is a real oracle.** Exact hash + path + order comparison with a 1e-6
  ranking tolerance, a SHA-256-pinned asset manifest, and an explicit remediation message. The
  regeneration escape hatch is opt-in and appears in neither workflow.
- **`Should.NotThrow` is not abused.** All 6 call sites are followed by specific assertions in the
  same test; none stands in for a real check. I went looking for this anti-pattern specifically.
- **Recording fakes are populated by production code, not by the test.** `RecordingMetrics` in
  `PromotionQueueServiceTests` and the `ActivityListener` in `MemoryToolsInstrumentationTests` are
  spies on real execution, not "verify the plan" mocks.
- **FluentValidation's camelCase property-path config** (`src/AiRaccoon.Core/Validation/ValidatorConfiguration.cs`,
  a `[ModuleInitializer]` with no direct test) is nonetheless pinned indirectly and adequately by
  `Unit/Memory/SearchQueryTests.cs:81-138` and `MemoryWriteRequestTests.cs:44,57`, which assert
  `e.PropertyName == "projectId"`. I flagged this as a gap, then refuted it.
- **Encryption key handling is the best-covered subsystem in the project.** I went in expecting a
  gap here and found none. Every question worth asking has a named test:
  wrong passphrase against an existing encrypted bank
  (`Unit/storage/SqliteConnectionFactoryEncryptionTests.cs` —
  `OpenBankAsync_WithWrongPassphrase_FailsToOpen`, `OpenBankWithKeyAsync_WrongKey_ThrowsSqliteException`,
  `OpenBankAsync_ResolverReturnsDifferentKey_ThrowsSqliteException`); missing key
  (`Resolve_NoSidecar_EnvNull_ReturnsNull`); **key rotation**, including WAL mode and an explicit
  "does not write key material next to the bank" assertion (`RekeyBankAsync_PlaintextBank_…`,
  `RekeyBankAsync_PassphraseBankInWalMode_…`, `RekeyBankAsync_DoesNotWriteKeyMaterialNextToBank`);
  and resolver failure in six distinct shapes — binary missing, non-zero exit with and without
  stderr, garbage stdout, timeout, passphrase-protected key, unsupported RSA key type
  (`Unit/Encryption/BitwardenCliSecretManagerTests.cs`, `BitwardenEncryptionKeyProviderTests.cs`).
  Corrupt-sidecar handling fails loudly and names the path
  (`Resolve_SidecarCorrupt_ThrowsLoudNamingThePath`). Given the project's "no hand-rolled crypto"
  invariant, this is the right place to have spent the effort. Almost all of it is `Speed=Slow`.
- **The sync error taxonomy is fully pinned.** All four types in
  `src/AiRaccoon.Infrastructure/Sync/SyncExceptions.cs` — `SyncConflictException`,
  `SyncNetworkException`, `SyncCorruptFileException`, `SyncAuthFailedException` — plus
  `SyncNotConfiguredException` are each referenced by at least one test under
  `tests/AiRaccoon.Tests/Unit/sync/`. That covers the If-Match precondition failure on both S3
  (`S3CloudStore.cs:133`) and Azure (`AzureBlobCloudStore.cs:139`), and both the local and remote
  snapshot integrity checks (`SyncService.cs:83,214,220`). Failure-path coverage here is better
  than in most codebases' happy-path-only sync layers.
- **Cancellation *is* tested where it matters most** — the hosted services and the idle watchdog,
  i.e. the paths where ignoring a token hangs a shutdown.
- **Schema migration is genuinely covered, and covered well.**
  `tests/AiRaccoon.Tests/Unit/storage/SqliteMemoryStoreSchemaTests.cs` opens a legacy bank with no
  `source_file` column and asserts the `ALTER TABLE` path
  (`src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:197-224`), proves idempotency
  (`:276 OpenBank_MigrationIsIdempotent_OnAlreadyIndexedBank`), and even covers a crash between a
  migration's DROP/CREATE and its repopulate (`:137`).
  `Integration/SectionTargetedRetrievalTests.cs:253-261` covers the Wave-6 column additions.
  This is the strongest area of the suite. Note it is entirely `Speed=Slow`, so **none of it runs
  in PR CI** — which is the clearest illustration of why D1 matters.
- **The store's concurrency tests are the real thing.**
  `tests/AiRaccoon.Tests/Unit/storage/SqliteMemoryStoreTests.cs:650-760` has five tests that use a
  `Barrier(2)` to force two tasks into `AddContentAsync` / `ShareAsync` *simultaneously* against
  real SQLite, then assert the converged outcome (`count.ShouldBe(1)`, `Distinct().ShouldHaveSingleItem()`)
  — not merely "no exception". `:652` even documents the specific cross-project promote race it
  closes. This is exactly how a write-race should be pinned. Also `Speed=Slow`, also not in PR CI.
- **`WatchScheduler`'s concurrency limit is genuinely tested, via a pattern a naive grep misses.**
  `tests/AiRaccoon.Tests/Unit/Watch/WatchSchedulerTests.cs:22-41`
  (`RunBatch_TenJobsAcrossThreeWatches_AtMostFourConcurrent_AllComplete`) uses a `GatedRunner` to
  hold jobs in flight, waits on `AllExpectedStarted`, then asserts `runner.MaxConcurrent.ShouldBe(4)`
  before releasing and asserting all 10 complete. That pins the *limit*, not just "it finished" —
  stronger than the `Task.WhenAll` idiom. I initially listed `WatchScheduler` as untested because I
  searched for `Task.WhenAll`/`Parallel.For`; that was my error, corrected in D21.

---

## Durable project facts

1. **The PR CI gate runs 1008 of 1416 tests.** `.github/workflows/build.yml:28` is
   `dotnet test --filter "Speed=Fast"`; only `nightly.yml:36` runs everything. Measured via
   `dotnet test --list-tests` with and without the filter.
2. **Reqnroll scenarios carry no `Speed` trait**, so *any* `Speed=…` filter drops all 105 of them.
   Feature-file tags are `@FR-*`/`@AC-*`/`@OQ-*` only. Source: the four files listed in
   `tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj:38-51`.
3. **`.feature` files live in `docs/`, not in the test project** — `docs/features/` (shipped) and
   `docs/work/features-*/` (drafts) — linked in via `<ReqnrollFeatureFiles>`
   (`tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj:38-51`). Only four of the five `.feature` files on
   disk are compiled; `docs/work/features-file-watcher/file-watcher.feature` is not.
4. **A fresh clone/worktree fails 48 tests** on the gitignored bundled ONNX model
   (`.gitignore:21`, thrown from `src/AiRaccoon.Infrastructure/Embedding/BundledModel.cs:79`).
   The bootstrap is `ai-raccoon model set local`; no workflow runs it. Measured: 1363/48/5.
5. **Full-suite cost is 4m39s wall clock** (`/usr/bin/time -p dotnet test`, cold build, this
   worktree, 2026-08-07). xUnit's own figure for the test phase is 3.31 minutes.
6. **`TestData.EnvVarGate`** (`tests/AiRaccoon.Tests/TestData.cs:12`) is the project's serialisation
   primitive for process-global env vars. It is applied at all four `AIRACCOON_DB_PASSPHRASE` sites
   and at **neither** `BWS_ACCESS_TOKEN` site. Its doc comment names only 2 of the 4 passphrase
   classes and is stale.
7. **`Resources/jsaa-memory.db` is a read-only shared corpus** consumed by 7 integration classes;
   every consumer `File.Copy`s it to a per-test root first (pattern at
   `tests/AiRaccoon.Tests/Integration/QueryConstructionTests.cs:41-43`, `:306-316`). Do not add a
   consumer that opens it in place.
8. **`ReferenceAssets.ResolveAssetsDirectory`** (`tests/AiRaccoon.Tests/Unit/Retrieval/ReferenceAssets.cs:194-222`)
   has a four-step resolution chain — `AIRACCOON_HARNESS_ASSETS` env var, source-tree walk-up,
   `CopyToOutputDirectory` fallback (`AiRaccoon.Tests.csproj:34`), then throw. This is the fix for
   the historical copy flake (a `Retrieval`/`retrieval` casing mismatch); the current failure mode is
   loud, never silent.
9. **`memory_configure` does not exist.** The real surface is 22 tools, enumerated at
   `tests/AiRaccoon.Tests/E2E/McpServerToolSurfaceE2ETests.cs:30-56` and counted at
   `tests/AiRaccoon.Tests/Unit/Mcp/ToolInventoryTests.cs:38`. Two feature-file scenarios and one doc
   comment still say 17 or 20.
10. **Access-mode enforcement lives in the tool layer**, not the store: `access.EnsureAsync(...)` is
    called at the top of `src/AiRaccoon/Tools/{Memory,Share,Workspace,Watch,Sync,Promotion,Sweep}Tools.cs`
    and in `src/AiRaccoon/Access/ForgettingPolicyService.cs:29,40`. Any test that calls
    `_store.WriteAsync` directly bypasses it entirely — which is exactly what the vacuous
    `native-memory.feature:33` scenario does.
11. **Two regeneration/self-heal env vars exist** and must never be set in CI:
    `AIRACCOON_HARNESS_REGENERATE_GOLDEN` (`GoldenFileTests.cs:10`) and
    `AIRACCOON_HARNESS_WRITE_REPORT` (`ParityGateTests.cs:18`). Both currently appear in no workflow.
    A third, `AIRACCOON_HARNESS_ASSETS`, overrides reference-asset resolution (see fact 8).
12. **Encryption and schema migration are already well covered — extend, don't re-derive.**
    `Unit/Encryption/` + `Unit/storage/SqliteConnectionFactoryEncryptionTests.cs` cover wrong key,
    rekey (incl. WAL) and six resolver-failure modes (the one hole is *unset* key vs an encrypted
    bank); `Unit/storage/SqliteMemoryStoreSchemaTests.cs` covers legacy-bank migration, idempotency
    and a crash mid-migration.
13. **The sync exception *types* are tested; the sync *conflict-resolution logic* is not.**
    `Unit/sync/` asserts each of the four `SyncExceptions.cs` types is thrown by the cloud stores,
    but nothing drives `SyncConflictException` into `SyncService.MemorySyncAsync`, so the retry loop
    at `SyncService.cs:126-183` is untested. `FakeCloudStore.cs:27` is the ready-made fixture.
    `CloudSyncConnectionFactory` has no test referencing it at all.
14. **There is no schema-version marker anywhere in `src/`** — no `PRAGMA user_version`, no
    `schema_version` key. `MemorySchema.MigrateAsync` infers state from column/index presence, and
    nothing rejects a bank written by a newer binary. Relevant to any sync or multi-version work.
15. **`SqliteMemoryStore` has two silent-degrade paths worth knowing about**: `GenerateAsync` at
    `:799`/`:849` is uncaught (a provider outage fails the whole write), and `QueryFtsBatchAsync`
    at `:725` swallows *every* `SqliteException` into an empty keyword list, so hybrid search can
    silently become vector-only. Neither is tested.
