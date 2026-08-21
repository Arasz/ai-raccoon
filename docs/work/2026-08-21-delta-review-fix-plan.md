# Plan — 2026-08-21 delta review, approved fixes

**Date:** 2026-08-21 · **Base:** `4cbce9d0` (main) · **Status:** plan, pre-implementation
**Sources:** `docs/reviews/2026-08-21-delta-review.md` · `docs/reviews/2026-08-21-delta-review-ground-truth.md` · `docs/work/2026-08-21-delta-review-owner-rulings.md` (14/14 APPROVE) · `docs/reviews/lanes/2026-08-21-delta-*.md`

Every item below carries its acceptance criteria and its named gate before work starts
(`proof-of-done`), the gate is written first and watched RED (`tdd-mandatory`,
`prove-the-check-fails`), and each names its sequencing against the two in-flight branches.

**Urgency calibration.** Ground truth measured the arbitrary-model surface as **loaded, not
fired**: `model_migration` rows = 0, vec pending tables absent, deployed binary 1.28.1 predates
#404. D1/D2/D3 are therefore **pre-deployment**, not hotfix. The live-and-firing surfaces —
maintenance jobs (D6), settings channel (C2), auto-start (C1) — rank higher by exposure.

## In-flight work this plan must not collide with

| Branch | State | Files it owns |
|---|---|---|
| **PR #405** `task/code-mem-implementation` | draft, 167 files | `MemorySchema.cs`, `SqliteMemoryStore.cs` + `.Replace.cs`, `MemorySql.cs`, `SqliteConnectionFactory.cs`, `SyncService.cs`, `ServerSettingsStore.cs`, `ConfigCommands.cs`, `SettingsEndpoint.cs`, `AppRegistrations.cs`, `MemoryTools.cs`, `ToolRefusals.cs`, `FileIngestor.cs`, `WatchIntegrationTests.cs`, `SchemaDoctorTests.cs`, `EmbeddingManifestValidatorTests.cs`, `README.md`, `docs/reference/logging-event-ids.md`, … |
| **PR #415** (Defect A — probe retry) | in flight now | `ServerProbe.cs`, `NodeRegistration.cs`, `ServerProbeVerdictTests.cs`, its plan doc — **and nothing else** |
| **restart follow-up** (Defect B — stale re-ingest chunks; no PR yet) | held until #405 merges | `FileIngestor.cs`, `SqliteMemoryStore.cs`, `SqliteMemoryStore.Replace.cs` |

**Corrected 2026-08-21 (from the restart session directly):** the restart plan
(`docs/work/2026-08-21-restart-probe-and-reingest-stale-chunks-plan.md`) proposed the
Polly-native shape in `ResiliencePipelineFactory` and raising a `NodeRunner` log level, but the
implementation deliberately diverged: PR #415 uses an in-delegate conversion precisely so the
probe fix does not reach into a factory shared by `ServerProbe`, `ServerRestart` and
`ObservabilityRunner`. **`ResiliencePipelineFactory.cs` and `NodeRunner.cs` are NOT in #415.**
There is one restart PR, not two. The 1.29.0 checklist `server-lifecycle` failure
**remains unexplained** — #415 fixes a real measured defect (attempts 1→3) but does not explain
that symptom. Nothing in this plan claims otherwise.

---

## Wave 1 — unblocked TODAY (no #405, no restart-PR dependency)

Four parallel lanes; no two share a file.

### Lane A — model supply chain (D1 then D2, same lane: they share one helper)

#### D1 — Activation re-verifies manifest sha256 pins against on-disk files
**Resolves:** B1' (blocker) = retrieval F1 + security F3/F4.
**Where:** `src/AiRaccoon.Infrastructure/Embedding/EmbeddingManifestLoader.cs:19-22` (the comment
claiming tampering is detected — false for content swaps), `:58-67` (`Load` checks only
`File.Exists` per pinned file); `EmbeddingService.cs:239-253` (`EngineFingerprint` hashes only
manifest bytes).
**Change:** in `Load`, replace each `File.Exists` in the `foreach (var file in files)` loop with a
sha256 comparison against the manifest's pin; refuse activation with an actionable message naming
the file and both digests. Introduce the one file-hash helper (Lane A owns it; D2 reuses it) —
an injectable component, not a static class (`static-classes` invariant). Correct the doc comment
in the same commit. Do **not** widen `EngineFingerprint`: the fingerprint answers "should we
re-embed", the pin answers "should we run at all" — two questions, two mechanisms.
**Acceptance:** a model directory whose `model.onnx` was replaced in place, manifest untouched,
fails activation instead of embedding; an intact directory activates unchanged; the message names
the offending file.
**Gate (RED first):** new `tests/AiRaccoon.Tests/Integration/Embedding/EmbeddingManifestPinVerificationTests.cs`
— `ASwappedPinnedFile_RefusesActivation` (RED today: `Load` returns a descriptor),
`AnIntactModelDirectory_StillActivates` (proves the check can pass), `AMissingPinnedFile_StillFailsWithTheOldMessage`.
New file, not `EmbeddingManifestValidatorTests.cs` — #405 rewrites that one.
**DI note:** the hash helper needs no new registration — inject it through the existing
`EmbeddingManifestLoader` constructor chain (already registered at `AppRegistrations.cs:279`).
A new `AddRequiredSingleton` line would touch `AppRegistrations.cs`, which **is in #405**; avoid
it, or accept a one-line merge and say so in the PR.
**Cost to state:** activation now hashes the ONNX weights (~90 MB). Activation is per engine
fingerprint and cached, so this is a per-process-start cost, not per-call. **This is why D3 must
not run per tool call** — see D3.
**Sequencing:** free of #405 and both restart PRs. Start now.

#### D2 — Non-LFS provenance files get integrity pins (owner: option B)
**Resolves:** M1 = security F2.
**Where:** `src/AiRaccoon.Infrastructure/Embedding/Download/ModelDownloadService.cs:184-208` (no
hash check), `:250,260` (`expected is null` ⇒ accepted unconditionally), `:350-369` (these files
author the manifest's dims/window/pooling/normalization);
`ModelDownloadPlanner.cs:9,469` (`PinnedFile.LfsSha256` null for git blobs).
**The actual gap (corrected in review):** the download path *already* hashes real downloaded
bytes into the manifest for tokenizer and onnx files (`ModelDownloadService.cs:366,369`,
`Sha256Of` over the bytes on disk; `ModelDownloadPlanner.cs:8` documents null-LfsSha256 as
TOFU-by-design). What is missing is that **`plan.ProvenanceFiles` (`config.json`,
`tokenizer_config.json`) is never written into the manifest's file lists at all**, so D1's
loader (`EmbeddingManifestLoader.cs:58` walks `Tokenizer.Files ++ Onnx.Files`) never sees them.
`modules.json` and `1_Pooling/config.json` are **not in scope**: they are fetched as strings,
consumed in-memory by `PoolingDecision` (`ModelDownloadPlanner.cs:304-310`), and never written to
the model directory — a file that does not exist on disk cannot be swapped after download, and
its *effect* (the pooling choice) is already frozen into the manifest at download time.
**Change:** write the two on-disk provenance files into the manifest with the sha256 of their
downloaded bytes (same TOFU mechanism, D1's helper). The pin is **trust-on-first-download** and
the manifest comment must say exactly that rather than imply upstream verification. With D1
landed, a later in-place edit of `config.json` is then detected.
**Acceptance:** a fresh `model download` writes a manifest whose file lists include
`config.json` and `tokenizer_config.json`, each with a sha256; editing either afterwards fails
D1's activation check.
**Gate (RED first):** `ModelDownloadPinsProvenanceFilesTests` (new file, Integration/Embedding,
against the existing `FakeHfServer`) — `TheProvenanceFiles_AppearInTheManifestWithSha256` (RED
today: `ProvenanceFiles` never reaches `WriteManifestAsync`, so the manifest omits them — assert
presence-in-manifest, NOT "git blobs get null", which is already false today), and an end-to-end
`EditingConfigJsonAfterDownload_FailsActivation` that only passes with D1 in place.
**Sequencing:** free of #405 and both restart PRs. After D1 in the same lane (shared helper).

### Lane B — maintenance loop

#### D6 — `HasWorkAsync` and the ledger read join `RunAsync` inside the per-job guard
**Resolves:** M4 = data F2 (lane graded LOW; integrated record elevated to MEDIUM).
**Where:** `src/AiRaccoon.Infrastructure/Maintenance/MaintenanceJobRunner.cs:38-46` (lastRun SELECT
+ `HasWorkAsync`) vs the `try` at `:56`; unguarded reader at `ModelMigrationJob.cs:25-29`.
**Change:** move the lastRun SELECT and the `HasWorkAsync` call inside the existing per-job
try/catch so one job's throw skips that job, not the pass. Log the skip through the runner's
existing `Log` class (`high-performance-logging` — a new `[LoggerMessage]` with its own `EventId`,
and `docs/reference/logging-event-ids.md` updated; note #405 also edits that file, so expect a
one-line merge).
**Acceptance:** a job whose `HasWorkAsync` throws is skipped and logged; every job registered after
it still runs in the same pass.
**Gate (RED first):** `MaintenanceJobRunnerTests.AJobWhoseHasWorkAsyncThrows_DoesNotStopLaterJobs`
(Unit) — RED today because the throw escapes before the try. Second test pins the log line.
**Sequencing:** free of #405 and both restart PRs. Start now.

### Lane C — tool gate

#### S1 — `promotion_list` without `projectId` requires an explicit read-all mode
**Resolves:** M8 = H8 residue = security F8.
**Where:** `src/AiRaccoon/Tools/PromotionTools.cs:36-40` — the gate runs only when `projectId` is
given; null yields an unscoped cross-project listing.
**Correction from review — the ruling's "global read-all mode" does not exist today.**
`AccessMode` is `{ Ro, Rw, Full }` (`AccessMode.cs:4-9`), `AccessModePolicy.cs:20` grants every
`Read` unconditionally, and `MemoryAccessGuard.cs:11` *requires* a projectId to resolve a mode at
all — with null there is nothing to resolve from. Building a genuinely global mode means a new
setting plus `Parse`/`Serialize`/`Resolve` changes in Core and a config surface, and it is
entangled with the unruled H-C/O2 (mode resolves from the caller-named project).
**Change (simpler shape, `ask-if-simpler` — owner flag O6):** refuse unscoped `promotion_list`
outright unless the caller passes an explicit `allProjects=true` argument; the refusal text names
that argument. The explicit flag is the consent the ruling wanted, without inventing an access
mode ahead of the H-C design. Keep the decision beside the existing gate call — the tool stays
thin (`mcp-thin`).
**Acceptance:** `promotion_list` with no `projectId` and no `allProjects` refuses with an
actionable message; with `allProjects=true` it lists as before; with `projectId` it is unchanged.
**Gate (RED first):** `PromotionToolsScopeTests.PromotionList_WithoutProjectId_RefusesWithoutAllProjects`
(Unit) — RED today because the null branch skips the gate (`PromotionTools.cs:37-42`, verified:
`queue.ListAsync(null, …)` is reached ungated). Plus a green-path test with the flag.
**Sequencing:** `PromotionTools.cs` is free of #405. **Soft collision:** if the refusal string
lands in `ToolRefusals.cs`, that file *is* in #405 — put the string with the tool, or accept a
one-line merge. Start now.

### Lane D — CI and docs-adjacent

#### Q2 — Nightly-tagged quality gates get a PR-runnable leg
**Resolves:** M6 = QA F1.
**Where:** `.github/workflows/build.yml` (three legs, filters `Speed=Fast`, `Category=bdd`,
`Speed=Slow` — nothing runs `Speed=Nightly`); `tests/AiRaccoon.Tests/TestCategories.cs`
("Nightly is excluded from every push-gate filter").
**Correction to the ruling's wording:** `nightly.yml` **already has `workflow_dispatch`**
(`nightly.yml:35`). The gap is not the trigger — it is that dispatching nightly.yml against a PR
head runs the *full unfiltered* 45-minute suite **and files a red-nightly issue** on failure, which
is wrong for a PR's own red. See owner flag O1.
**Change:** add a fourth job to `build.yml` — `build-nightly-gates` — that runs
`dotnet test --filter "Speed=Nightly"`, triggered on a `run-nightly-gates` label (or
`workflow_dispatch`), reading the same `changes.code` output as its siblings, and **not** invoking
`scripts/nightly-triage.py`. Actions pinned to full commit SHAs (`pin-actions-to-sha`).
**Acceptance:** applying the label to a PR runs the Nightly-tagged tests against the PR head, red
fails the check, and no red-nightly issue is filed.
**Gate:** the workflow itself is the gate; **prove it fails** by dispatching it once against a
branch carrying a deliberately-broken held-out floor and watching the job go red, then reverting.
Record the run URL in the PR.
**Sequencing:** workflows are free of #405 and both restart PRs. Start now.

#### C3 — `doctor` distinguishes "no bank" from HEALTHY
**Resolves:** surface F4 (LOW) — no M-number.
**Where:** `src/AiRaccoon/Setup/Cli/Commands/DoctorCommands.cs:18-22` — `!File.Exists(bankPath)`
at `:18` prints `no bank to check` and returns `ExitCode.Success` at `:21`, so a wrong
`--data-root` reads as healthy.
**Change:** a distinct non-zero exit code for "no bank at the resolved path", with the path in the
message. Extend the exit-code table in the docs.
**Acceptance:** `doctor --data-root <empty dir>` exits with the new code and names the path;
healthy still 0; 19/20 unchanged.
**Gate (RED first):** `DoctorCommandsTests.NoBankAtTheResolvedPath_ExitsNonZeroAndNamesThePath`
(Unit, asserting on argv not just the handler — CLI option names keep their `--` prefix). RED today
because it exits 0.
**Sequencing:** `DoctorCommands.cs` is free of #405. **Note:** `SchemaDoctorTests.cs` *is* in #405 —
put the new tests in the commands test file, not there. Start now.

### S3a — file the jsaa-memory.db issue NOW — **DONE**
**Resolves:** the immediate half of M11 = security F11 (owner note: "create issue now, wait for
the calm machine").
**Where:** `tests/AiRaccoon.Tests/Resources/jsaa-memory.db` — 19,173,376 bytes (`git cat-file -s`),
2,518 rows; 220 rows contain an `'@'` in their value (any email-like content), of which the 0814
campaign counted 94 carrying the owner's own address — two different measures, both real.
**Gate satisfied:** issue filed 2026-08-21 —
[#414](https://github.com/Arasz/ai-raccoon/issues/414). It records that HEAD removal alone is
insufficient, the rewrite needs `git filter-repo` plus a force-push, and that it must run on a
calm machine with no branch in flight.
**The rewrite itself (S3b) is NOT in this plan's waves** — it waits for the calm machine,
tracked by #414. HEAD removal + synthetic-fixture replacement can ride any PR that touches the
test resources.

---

## Wave 2 — coordinate with PR #415 (gate dissolved — see correction above)

Originally gated on "restart PR 1 merges" because the restart *plan* claimed
`ResiliencePipelineFactory.cs` and `NodeRunner.cs`. PR #415's actual scope touches neither, so
both items below are file-unblocked today; they stay a separate wave only to keep Wave 1's lanes
clean and because C1's messaging should be written with #415's new probe verdicts in view.

#### D3 — Dimension reconcile also runs at open when engine dim ≠ vec dim
**Resolves:** M2 = data F3. Owner note: *"What about performance hit for dimension check? Will this
be server only? (HARD INVARIANT)"* — answered below.
**Where:** `EntryEmbedder.cs:152` (`ReconcileAsync` runs only inside `DrainMigrationAsync`),
`:183-193` (`ReconcileVecDimensionsAsync`, private), `EntryEmbedder.cs:108`
(`MarkAllEmbeddedPending` empties vec tables); `VecDimensionReconciler.cs:29-59`;
`NodeRunner.cs:113` (`EnsureEmbeddingAvailabilityAsync`) vs `:115` (`serverHost.StartAsync`).

**Answer 1 — server-only? Yes, by construction, and that is the point.**
A reconcile is a bank *write* — `DROP TABLE` + `CREATE VIRTUAL TABLE … vec0`
(`VecDimensionReconciler.cs:79-90`). `cli-asks-the-server-acts` forbids the CLI writing the bank.
So the check must **not** go in `MemorySchema.EnsureAsync`, which runs on every
`InitializeAsync` (`SqliteConnectionFactory.cs:245-260`) from *every* process, CLI verbs included
(`EncryptionCommands.cs` opens the bank). The correct site is `NodeRunner`, which only runs under
`serve`: hook the reconcile beside `EnsureEmbeddingAvailabilityAsync` at `NodeRunner.cs:113`,
**before** `serverHost.StartAsync` at `:115`, so vec0 matches the engine before the first tool call.
Simplest shape (`ask-if-simpler`): promote the existing private `ReconcileVecDimensionsAsync` onto
`IEntryEmbedder` and call it once — no new component, no second copy of the provider-empty guard.

**Answer 2 — analytic cost of one check.** Two halves.
- *Bank side* (`VecDimensionReconciler.NeedsRecreateAsync`, `:61-77`): one
  `SELECT sql FROM sqlite_master WHERE type='table' AND name=@table` per vec table (2 tables) plus a
  `[GeneratedRegex]` match over a ~100-char CREATE statement. `sqlite_master` is already in page
  cache after open. **O(1) in bank size** — the 248 MB live bank costs the same as an empty one.
  Sub-millisecond.
- *Engine side* (`EmbeddingService.ResolveDimensions`, `:188-196`): non-local providers read a
  settings field — free. `local` goes through `ManifestDescriptorFor` (`:200-209`) →
  `EmbeddingManifestLoader.Load` (`:27-92`): `Directory.Exists`, `File.Exists(manifest.json)`,
  `File.ReadAllText`, JSON deserialize, validate, then one `File.Exists` per pinned file (typically
  2-5). **This is the expensive half, and it is not cached** (hypothesis — verify at
  implementation start; a cache would strengthen the argument). Plus one settings SELECT at
  `EntryEmbedder.cs:186`. Estimated 1-5 ms warm (analytic, not measured); worse cold or on a
  network FS.

**Answer 3 — why start-scoped, not per-tool-call.** The vec dimension can only change when the
engine config changes, and that only happens through `model set`. Server-mediated `model set` is
already covered by the drain; the *serverless* `model set` — M2's unbounded window — is exactly what
a start-time check catches. A per-call check therefore buys nothing and would put the manifest
`Load` in front of every MCP call, where `HasOpenModelMigrationAsync`
(`SqliteMemoryStore.ModelMigration.cs:15-22`) already opens a connection.
**And with D1 landed it gets worse:** activation verification hashes ~90 MB of ONNX weights, so a
per-call check becomes indefensible. **D1 and D3 interact; D3 is once per server start.**

**Measurement — designed, TO RUN AT IMPLEMENTATION START (not run in this planning pass, per
owner instruction: no code runs tonight).**
- *Instrument:* a `Speed=Slow`-tagged xUnit timing test, `Stopwatch`, 20 iterations, median and p95.
  BenchmarkDotNet does not pay here (`measure-when-it-pays`) — the decision only needs an order of
  magnitude against a budget three orders larger.
- *Subject:* `ReconcileVecDimensionsAsync` on its **no-change** path (the 99.99% case) against a
  bank restored from a ≥200 MB copy, with a manifest-configured local engine so the uncached
  `Load` is included.
- *Pass threshold:* **p95 ≤ 25 ms**, and ≤ 1% of measured server-start wall clock.
  Justification for 25 ms: `EnsureEmbeddingAvailabilityAsync` already creates an ONNX session at the
  same point in startup, which is seconds — 25 ms is invisible there and is a real ceiling, not a
  number chosen to pass. Re-derive the threshold once the measurement runs.
- *If it exceeds 25 ms:* the fix is not to drop the check but to cache the manifest descriptor in
  `EmbeddingService` keyed on (path, mtime, length). Fold that in only if measured.
- *Prove the measurement can fail:* run the same test once against a stub reconciler that sleeps
  60 ms and watch the assert go RED before wiring the real one.

**Acceptance:** after a serverless `model set` that changes dimensions, the next `serve` reconciles
vec0 before accepting a tool call; a server start with matching dimensions performs no DDL and stays
inside the 25 ms budget; **no CLI verb ever reconciles**.
**Gate (RED first):**
`VecDimensionReconcileAtStartTests.AServerlessModelSetChangingDimensions_IsReconciledBeforeTheFirstToolCall`
(Integration) — RED today because reconcile only runs in the drain;
`AMatchingDimension_PerformsNoDdl`; `ACliVerbOpeningTheBank_DoesNotReconcile` (the hard-invariant
guard); plus the Slow timing test above.
**Sequencing:** `VecDimensionReconciler.cs` and `EntryEmbedder.cs` are free of #405, and
`NodeRunner.cs` is in **neither #405 nor PR #415** (corrected — the restart plan proposed touching
it; the implementation did not). **D3 is file-unblocked and may start now.** If D3 needs a DI registration,
`AppRegistrations.cs` is in #405 — prefer promoting the method on the existing port over a new
registration, which avoids that file entirely.

#### C1 — Auto-start fails with instructions instead of "serve exit 1"
**Resolves:** H-A (HIGH, live-probed) = surface F2.
**Where:** `src/AiRaccoon/Hosting/Common/BackendLaunchArguments.cs:14` — `Executable()` returns
`Environment.ProcessPath`, the dotnet driver when unpackaged, so the child is
`dotnet --data-root … serve`, which exits 1 instantly; `src/AiRaccoon/Hosting/Proxy/BackendLauncher.cs:151-152`
(note: `Hosting/Proxy/`, not `Common/`) fire-and-forgets `DrainAsync(backend.StandardError)`, so
the operator sees only a 30 s timeout and `EXIT=18`.
**Pre-step (cheap, do first):** resolve the review's own still-open question — *does the global-tool
apphost path ever hit this?* Run the installed tool's auto-start once. If it never hits, H-A
downgrades to dev-invocation-only and the fix stays a diagnostic, not a launcher rewrite. Record the
answer in the PR either way.
**Change:** detect unpackaged invocation in `Executable()` and fail immediately with a message
naming the invocation shape and the manual `serve` command, instead of spawning a child that cannot
work. Surface the child's stderr on failure rather than draining it silently.
**Acceptance:** under `dotnet run`, a server-mediated verb fails in under a second with an
actionable message naming `serve`; under the packaged apphost, auto-start is unchanged;
`Executable()` gets its first test.
**Gate (RED first):** `BackendLaunchArgumentsTests.AnUnpackagedInvocation_IsDetected` (Unit, RED —
no test covers `Executable()` today) plus
`BackendLauncherTests.AFailedChild_SurfacesItsStderr` (RED — currently drained).
**Sequencing:** neither file is in #405, and neither is in PR #415's set — C1 is
*technically* unblocked today. It sits in Wave 2 on a design argument: #415 and C1 both rewrite
what a failed server start tells the operator, and designing that message in two places at once
produces two vocabularies. If wall-clock matters more, C1 can move to Wave 1 safely (owner flag O4).

---

## Wave 3 — after PR #405 merges

Five items; no two share a file with each other. Each names its #405 collision.

| # | Resolves | Where | Change · Acceptance · Gate (RED first) | Collision |
|---|---|---|---|---|
| **D5** | M3 = data F1 | `MemorySchema.cs:456-459` (Ddl → `StampSchemaDigestAsync`) vs `:551-558` (ladder → `StampAsync(CurrentVersion)`); cheap path `:832-840`, called per tool call via `SqliteMemoryStore.ModelMigration.cs:18` | Move `StampSchemaDigestAsync` after the ladder's `StampAsync`, so a crash in the window leaves the digest stale and the next open runs the full `EnsureAsync` instead of the cheap path trusting a stale-schema bank. **Acceptance:** a bank interrupted between DDL and ladder is fully migrated on next open, not cheap-pathed. **Gate:** `MemorySchemaStampOrderTests.ACrashBetweenDdlAndLadder_StillMigratesOnNextOpen` (Integration) — RED today. | `MemorySchema.cs` is in #405 |
| **D4** | retrieval F8(b) — no M-number | `EntryEmbedder.cs:26-53` (`ConfigureAsync`: no outbox, no reconcile, throws mid-re-embed on dim change); the port members on `IMemoryStore` | Delete `ConfigureAsync`/`ConfigureEmbeddingAsync` from `IMemoryStore` and its implementation; production already routes through the ADR-0076 outbox. Shrinks the 27-member god port by two (H21 direction, not a resolution). **Acceptance:** the members are gone, nothing in `src/` references them, the suite is green. **Gate:** `LayeringRulesTests` / `McpToolContractTests` — an ArchUnitNET or contract assertion that `IMemoryStore` exposes no `Configure*` member, written RED against today's port. | `SqliteMemoryStore.cs` + `MemoryTools.cs` in #405; `SqliteMemoryStore.cs` also in **restart PR 2** — serialize D4 against PR 2 |
| **S2** | H-B = H9/H10 = security F5 | `SyncService.cs:63-70` (whole-bank `VACUUM INTO` under a caller-named key), `:200-218` (`PRAGMA quick_check` = integrity, not authenticity), `:221-222,229,236` (remote blob `ATTACH`ed into the live bank) | Owner approved the ruling as written: **either** a keyed hash / signature over the pushed blob, verified before ATTACH, **or** the risk explicitly accepted and documented. Given `no-hand-rolled-crypto`, do not invent a scheme — use a platform primitive (HMAC over the blob with a key from the existing key resolver) or take the documented-acceptance branch. **Branch decision owed by the owner before code is written (owner flag O3)**; if acceptance, the deliverable is an ADR plus a README SECURITY line, not code. **Acceptance (code branch):** a tampered remote blob is refused before ATTACH. **Gate:** `SyncServiceRemoteBlobTests.ATamperedRemoteBlob_IsRefusedBeforeAttach` — RED today. | `SyncService.cs` is in #405 |
| **Q1** | M7 = QA F2 | `WatchIntegrationTests.cs:771-800` — budget checks at `:781-782` run only *between* iterations; condition/tick awaits carry only the never-firing test token; xUnit has no per-test timeout | Wrap each iteration's awaits in a token linked to the wall-clock budget so one blocked call fails the test instead of hanging the testhost. Note the observed red at `:343-348` is an arrange-phase seed timeout (QA F3) — Q1 makes it *diagnosable*, it does not fix the seed slowdown. **Acceptance:** a deliberately blocked await inside `StepUntilAsync` fails within the budget with a message naming the blocked step. **Gate:** `StepUntilAsyncTests.ABlockedAwait_FailsWithinTheWallClockBudget` — RED today (it hangs; run it with an external timeout to observe the RED). | `WatchIntegrationTests.cs` **is in #405** — Q1 is behind #405, not free |
| **C2** | M9 = surface F3 | `ServerSettingsStore.cs:199` → `ConfigCommands.cs:148` — a 500 prints `Response status code does not indicate success: 500` and exits **15 (InvalidArgument)**, i.e. "you mistyped" | A distinct exit code for server-side 5xx on the settings channel, with a message distinguishing "the server is broken" from "you mistyped". Extend the exit-code table in the docs (the contract is live-verified per surface F10 — keep it that way). C3 (Wave 1) will already have added a constant to `ExitCode.cs`, a case to `ExitCodeTests.cs` and a docs-table row — **C2 rebases onto C3's additions**; different waves, so serialized by construction. **Acceptance:** a stubbed 500 exits with the new code; a genuine bad argument still exits 15. **Gate:** `SettingsChannelExitCodeTests.AServerSide500_ExitsWithItsOwnCode` — RED today (returns 15). | **both** `ServerSettingsStore.cs` **and** `ConfigCommands.cs` are in #405 |

---

## Carried, not planned

| # | Why not planned |
|---|---|
| **M5** — thin out-of-sample control (retrieval F3/F4): 3 pinned queries, eval-100 is one ADR family | A corpus problem, not a defect. Belongs to the next tuning round, with the second family or internal split decided then. No owner ruling was sought. |
| **M10** — README "What's new" missing 1.29.0 (surface F1) | #405 rewrites `README.md`. Folding it in here guarantees a conflict for a one-line docs edit. **Hand it to whoever merges #405**, or to the next release checklist. |
| **H-C / H7** — access mode resolves from the caller-named project (`MemoryAccessGuard.cs:9-17`, MEASURED, HIGH) | Carried unchanged from 0814; **no owner ruling in this campaign's 14**. Needs a server-side project-identity design before it can be scoped — an ADR, not a fix item. Highest-severity unplanned finding — owner flag O2. |
| **H18** — `ResiliencePipelineFactory.cs:62` string-matches `"EmptyDownloadException"` (security F6, MEASURED) | **Handoff to the restart PR was proposed and DECLINED** (2026-08-21, with reasons that check out): #415 does not touch that factory — deliberately, to keep the probe fix out of a component shared by `ServerProbe`/`ServerRestart`/`ObservabilityRunner`; H18 sits in `CreateAssetDownloaderPipeline`, a different builder than the probe's; and the fix is an architecture decision (where should the exception type live, should a name-match predicate exist at all), not a line edit. **H18 stays open, unassigned — route to whoever next owns the download path** (Lane A's D1/D2 PR is the natural neighbour if the owner agrees) — owner flag O5. |
| **H20** — `WorkspaceService`/`IWorkspaceService` in Infrastructure; **arch F1** — `IPromotionQueuePruneStore`/`IWatchRegisteredStore` in Infrastructure; **arch F2** — RRF/affinity outside Core | Port-placement drift. Worth one generic ArchUnitNET rule instead of three moves, but no owner ruling exists and #405 adds new ports. Re-raise after #405 as a single "port placement rule" item. |
| **H21** — `IMemoryStore` at 27 members; arch F3 (partial split is file-level), arch F7 (4 duplicate settings members) | D4 removes two members as a side effect. Real decomposition is a multi-PR carve-out that #405 would collide with wholesale. Defer. |
| **H16** — CI still ubuntu-only, no matrix | Carried from 0814, no ruling. Cost/benefit belongs with the release checklist owner. |
| **H1** — `ranking` still rank-derived (`MemoryTools.cs:117`); `SourceAffinityRanker` λ=0.1 | Carried; **owner question 7 from 0814 is still unanswered**. Cannot be planned before it is answered. |
| Still-open: mechanism of the full-suite seed-embed slowdown (QA F3) | Diagnostic work, not a fix. Q1 makes the next occurrence diagnosable; the mechanism hunt is a separate investigation. |
| Still-open: has any Nightly run executed recently | **Answered 2026-08-21 (metadata read):** scheduled run 32442237251 on main completed **success** in 25m32s at 03:07Z today; the 2026-08-20 scheduled run failed and was fixed the same day via `workflow_dispatch` runs on `task/nightly-2026-08-20-fixes`. The schedule fires and the dispatch leg works. |
| Still-open: leave-one-family-out on RRF parameters | Moot for eval-100 per retrieval F4 (single family); returns with M5. |
| Still-open (data lane): does vec0 actually raise dimension-mismatch on insert against an empty old-dim table; `storedVersion >= CurrentVersion` early-return at `MemorySchema.cs:485-488` | Both are questions D5 and D3's implementers will stand in front of anyway. Answer them in those PRs; do not open separate items. |
| **1.29.0 checklist `server-lifecycle` failure** | **Unexplained.** The warming-server explanation was retracted and measurements refute it. Not fixed by PR #415 (confirmed by that session directly), not fixed by this plan. Stays open. |

## Owner flags — decisions or notices that survive this plan

- **O1 — Q2's approved wording is stale.** The ruling said "workflow_dispatch PR-gate leg", but
  `nightly.yml:35` already has `workflow_dispatch`. The plan implements what the ruling *meant* —
  a PR-runnable `Speed=Nightly` leg in `build.yml` that does not file red-nightly issues. If that
  reading is wrong, say so before Lane D starts.
- **O2 — H-C/H7 (HIGH, measured) has no ruling and is not planned.** It is the highest-severity
  finding with no owner decision. It needs a project-identity ADR, not a fix item.
- **O3 — S2 branch decision owed:** keyed-hash verification vs documented acceptance. The ruling
  as approved offers both; the plan cannot pick for you.
- **O4 — C1 sits in Wave 2 by design choice** (write its failure messaging with #415's new probe
  verdicts in view), not by file collision. Safe to pull into Wave 1 if speed matters more. D3's
  original file gate dissolved with #415's final scope — it is likewise startable now.
- **O5 — H18 needs an owner.** The restart session declined the handoff (its PR #415 deliberately
  avoids `ResiliencePipelineFactory.cs`, and H18 is in the *downloader* pipeline builder, not the
  probe's). H18 is also architecture-shaped: decide where `EmptyDownloadException` should live —
  or whether a name-match predicate should exist at all — before anyone edits the line. Natural
  home: the Lane A (D1/D2) PR, subject to your ruling.
- **O6 — S1 deviates from the ruling's literal wording.** The ruling said "global read-all mode";
  no such mode exists (`AccessMode` is `{Ro, Rw, Full}`), and building one is entangled with the
  unruled H-C. The plan substitutes an explicit `allProjects=true` argument as the consent
  mechanism. If you want the real mode, S1 grows into a Core access-model change and should be
  scoped together with O2.
- **Severity elevations inherited:** the plan uses the integrated record's grades where lanes
  differed (data F2 LOW→M4 MEDIUM; security F3/F4 MEDIUM→B1' blocker via two-lane convergence).
- **Source-doc nit:** `docs/work/2026-08-21-delta-review-owner-rulings.md:1` titles itself
  "12 owner rulings" while carrying 14 answered headings (its own form comment says 14/14). The
  plan counts 14; the source's H1 is stale — noted so the plan does not read as over-counting.

---

*Reviewed 2026-08-21 by a high-reasoning code-review lane before landing: 22 anchors checked
(18 exact, 4 corrected above), all 17 in-#405 collision claims and all 10 Wave-1 "start now"
free-of-#405 claims verified against `git diff --name-only main...origin/task/code-mem-implementation`
(167 files), 3 of the RED-today gate claims re-derived against source, D2 and S1 rewritten where
the review falsified the original shape.*
