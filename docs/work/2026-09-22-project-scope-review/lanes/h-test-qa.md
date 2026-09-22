# Lane H — Test-suite QA

Campaign: project-scope review, 2026-09-22, base `5bca19000d127547ddbc56e2a464113c844334cc`
Worktree: `.ai-badger/worktrees/psr-h-test-qa` (all mutations applied inside it, all reverted; final
`git status --porcelain` empty, HEAD = base SHA). Persona: qa (stack override `qa-backend`).
`commands.test` in `.ai-badger/config.json` is `dotnet test` — the full suite; per campaign rule I ran
**targeted filters only** (the full suite was executing elsewhere) and every count below is from my own
`--list-tests`/run invocations, not copied from Phase 0.

Scope, stated before the findings: I audit the **gate surface** — would each load-bearing gate redden
when the thing it guards breaks — plus skip honesty, fake fidelity, partition honesty and flake
architecture. Out of scope: retrieval/ranking correctness (Lane B), SQLite schema and migration
semantics (Lane D), production security (Lane J), and architecture/layering (Lane A). I do not grade
coverage percentages and I do not propose plans.

Mutations run (≥3 gates broken and watched red, as briefed; every one reverted and re-verified green):

| # | gate under test | mutation (applied, then reverted) | command | observed |
|---|---|---|---|---|
| M1 | "TTL necessary but not sufficient" sweep rule | `DegradationPolicy.ShouldDegrade`: dropped the `ttlDays.HasValue` clause → `rating < threshold && ageDays > (ttlDays ?? 0)` | `dotnet test tests/AiRaccoon.Tests --no-build --filter "FullyQualifiedName~Sweep"` | **RED**: 102 total, 2 failed — `SweepServiceTests.SweepAsync_EntryWithoutPerEntryTtl_IsNeverSwept`, `PromotionQueueServiceTests.Sweep_DropsQueueRowsForTheEntriesItDeleted_AndLeavesTheRest` |
| M2 | cross-project / shared-tier delete refusal | `SqliteMemoryStore.DeleteContextAsync`: removed the `ContextScope.RequireWithinProject(context, projectId)` call | `--filter "FullyQualifiedName~MemoryStoreContextScopeTests"` | **RED**: 6 total, 2 failed — `DeleteContextAsync_NamingAnotherProject_DeletesNothing` (:84), `DeleteContextAsync_NamingTheSharedTier_DeletesNothing` (:98) |
| M3 | trait-coverage guard fires on an ungated class | planted `ZZProbeNoTraitTests` (no `[Trait]`) | `--filter "FullyQualifiedName~SpeedGateCoverageTests"` | **RED**: 3 total, 1 failed — offender named `AiRaccoon.Tests.Unit.ZZProbeNoTraitTests` |
| M4 | sweep's scoped delete (H2) — fake vs real store | `SweepService`: delete scope `"project"` → `"workspace"` | `--filter "FullyQualifiedName~SweepServiceTests"` then `--filter "FullyQualifiedName~SweepScopeSiblingTests"` | **unit 7/7 GREEN; real store 3/3 RED** (all three `SweepScopeSiblingTests`) |

---

### F1 — The trait-coverage gates check the trait's NAME but not its VALUE, so a class tagged `Speed=Medium` is blessed green while no CI lane selects it [MEASURED]
**Severity:** MEDIUM
**Evidence:** planted `tests/AiRaccoon.Tests/Unit/ZZProbeBadSpeedValueTests.cs` (`[Trait(Category,Unit)] [Trait(Speed,"Medium")]`) alongside a no-trait probe, then M3: `dotnet test … --filter "FullyQualifiedName~SpeedGateCoverageTests"` → `total: 3, failed: 1`, offender list `["AiRaccoon.Tests.Unit.ZZProbeNoTraitTests"]` **only**. `--list-tests --filter "Speed=Medium"` → `Discovered 1` (`ZZProbeBadSpeedValueTests.Probe_SpeedTraitValueIsNotALane`); the same probe under `Speed=Fast&Performance!=Benchmark`, `Speed=Slow&Performance!=Benchmark`, `Speed=Nightly&Performance!=Benchmark`, `Category=bdd` → **0 matches each**. Both probes deleted; `SpeedGateCoverageTests` back to 3/3 green.
`SpeedGateCoverageTests.cs:22-29` and `CategoryGateCoverageTests.cs:22-29` filter on `trait.Name != TestCategories.Speed|Category`; nothing compares the value to the closed set in `TestCategories.cs:24-30`. The guard's stated purpose (its own docstring `:12-15`) is that "a test class with no Speed trait … compiles, runs locally, and never gates a merge" — a typo'd or unknown *value* (`"Medium"`, `"fast "`, a bare literal) reproduces exactly that outcome while both guards and `TheGuardSeesTheTestClasses` (`:35-39`) stay green. The Category half is the same expression (read, not separately planted). No live offender exists today; the hole is latent.

### F2 — The CI lane partition is not exact: two test cases carry both `Speed=Fast` and `Speed=Slow` and run in two lanes [MEASURED]
**Severity:** LOW
**Evidence:** my own per-lane discovery in this worktree: Fast **3788** + bdd **184** + Slow **1073** + Nightly **164** + Benchmark **3** = **5212** against the assembly total, and `comm -12` of the sorted Fast and Slow name lists returns exactly `ThreadResolutionTests.Generator_ReportsTheIntraOpThreadsItWasBuiltWith(threads: 0)` and `(threads: 4)`.
`tests/AiRaccoon.Tests/Unit/Embedding/ThreadResolutionTests.cs:8-10` carries class-level `Category=Unit` + `Speed=Fast`; `:62-68` adds method-level `Category=Integration` + `Speed=Slow` on that one theory ("Constructs a real ONNX session, so this one leg is Integration/Slow").
Consequence: `build-fast` and `build-slow` both execute it (duplicated CI work for a real-ONNX test inside the fast gate), and `TestCategories.cs:11-13`'s definition of Unit ("pure logic / fakes") is false for it. The 2026-08-14 test-QA lane measured this partition as exact with 0 pairwise intersections; this is new drift, not a coverage hole.

### F3 — Documented gate commands are inert: `dotnet test … --no-build --nologo` discovers 0 tests and exits 5 [MEASURED]
**Severity:** LOW
**Evidence:** `dotnet test tests/AiRaccoon.Tests --no-build --list-tests --nologo` → `Zero tests ran` / `Discovered 0 tests` / exit 5, twice (with and without a `--filter`); the byte-identical command **without** `--nologo` → `Discovered 5212 tests` (5210 at base; two temporary probe classes were present during the measurement).
`docs/plans/2026-08-08-embedding-perf.md:312-314,495-497` and `docs/plans/2026-08-16-bank-open-cost-implementation.md:452` hand agents gates as `dotnet test --filter … --no-build --nologo -v m`.
On .NET 10's Microsoft.Testing.Platform path the unrecognised switch produces a run over zero subjects reported with a non-success exit code and the words "Zero tests ran" — a reader scanning for red finds none, and the plan's acceptance gate never executed. CI itself is unaffected (`-v m`, no `--nologo`); the hazard is agent- and plan-directed commands. Fix: drop `--nologo` from test commands (it is a build switch), or wrap documented gates in a non-zero-subject assertion (T1-PRF-03).

### F4 — `FakeMemoryStore.DeleteInScopeAsync` drops the `scope` argument, so the unit lane is blind to the sweep's scoped-delete invariant; only the real-store lane holds it [MEASURED]
**Severity:** LOW
**Evidence:** M4 — with `SweepService` deleting scope `"workspace"` instead of `"project"`: `--filter "FullyQualifiedName~SweepServiceTests"` → `total: 7, failed: 0, succeeded: 7` (all green), while `--filter "FullyQualifiedName~SweepScopeSiblingTests"` → `total: 3, failed: 3` including the negative control `Sweep_ProjectRowWithNoSibling_IsStillDeleted` ("the scoped delete must not become inert for the ordinary case"). Reverted; both green.
`tests/AiRaccoon.Tests/TestHelpers/FakeMemoryStore.cs:34-36` forwards to `DeleteAsync(projectId, hash)` and discards `scope`; `tests/AiRaccoon.Tests/Unit/TestHelpers/FakeMemoryStoreTests.cs:33-39` pins that forwarding as a contract. The production invariant (`SweepService.cs:52-57` plus `SqliteMemoryStore.DeleteInScopeAsync`'s H2 doc, `:285-299`) is therefore unobservable through the fake: any future unit test asserting "the sweep deletes only this scope" passes vacuously, and the *real* guarantee lives entirely in `Integration/Storage/SweepScopeSiblingTests.cs`. Not a current hole — the integration lane covers it — but the fake is an unverified claim about production on exactly the parameter the invariant depends on (T0-05/T1-DBL-02).

### F5 — The retry surface is the whole expensive half of the suite, a bare retry attribute buys 3 attempts, and a retry-recovered failure leaves no trace in the run output [MEASURED]
**Severity:** MEDIUM
**Evidence:** temporary probe `[RetryFact] public void Probe_CountsAttempts()` appending a line per attempt and failing: `dotnet test … --filter "FullyQualifiedName~ZZProbeRetryBudget"` → run failed; `docs/work/2026-09-22-project-scope-review/h-test-qa/retry-attempts.txt` = **3 lines** (3 attempts, 1 reported failure). The existing `RetryFactRetriesOnTransientFailureTests.RetriesUntilItPasses` (throws on attempt 1, passes on 2): run output is `passed … total: 1, failed: 0, succeeded: 1` with **0** occurrences of `retry|transient|attempt` in the log. `tests/AiRaccoon.Tests/Unit/RetrySurfaceGateTests.cs:20-38` *mandates* retry attributes for every file under `E2E/` and `Integration/` and every Slow/Nightly file (1835 grep hits of `RetryFact`/`RetryTheory` in the tree), with `SurfaceFiles().Count ≥ 200` as its non-vacuity guard.
Consequence for the lane's question: for the ~1000-test integration/E2E surface the run can no longer distinguish "passed" from "failed twice, passed on the third try", so a genuinely intermittent defect (a race, a port collision, a shared-fixture leak — the archetypes the backend QA persona hunts) can keep a PR green indefinitely, and the quarantine ledger that exists for exactly that purpose (`known-flakes.json`, 1 entry, consumed by `scripts/nightly-triage.py:41` from *failed* tests) is never fed by the PR lanes. Retries were a deliberate project decision; the missing half is the reporting, not the retry.

### F6 — The golden-vector tripwire's bit-identity assertion is arch-scoped and inert on the x64 platform where merges are gated [MEASURED]
**Severity:** LOW
**Evidence:** my run in this worktree: `dotnet test … --filter "FullyQualifiedName~MiniLmGoldenVectorTests"` → `total: 2, failed: 1, succeeded: 0, skipped: 1`; the failure message is `mismatchedBits … entry E001: 384/384 float32 values differ bit-for-bit from the golden capture — the WP3 refactor changed engine output` (exit 2). `CaptureGoldenVectors` correctly reports **skipped** (env-gated), not passed.
`tests/AiRaccoon.Tests/Integration/Embedding/MiniLmGoldenVectorTests.cs:90-91` computes `sameArchitecture` from the capture's recorded provenance; `:110-122` runs the bit-identity + `L2 ≤ 1e-6` assertions only when true; off-arch the pass condition degrades to `cosine ≥ 0.95` (`:146`) plus token-id equality (`:163-165`), itself documented as a "COARSE BREAKAGE FLOOR". The committed capture is Arm64, so on the x64 CI runners the strongest engine-output check never executes — which is consistent with the ORT 1.29→1.30 bump (Ground-Truth) merging green while this arm64 machine reddens. The trade and its fix ("per-architecture golden capture") are written down in the file; the finding is that the gate's headline assertion is local-only.

### F7 — The `@ignore`d tool-parity scenario now passes if un-ignored, but its name and Gherkin still claim 17 tools while its step binding checks 16 and the product exposes 29 [MEASURED]
**Severity:** LOW
**Evidence:** removed only the `@ignore` line above the scenario, rebuilt: `dotnet test … --filter "FullyQualifiedName~All17ToolsAreStillListed"` → `total: 1, failed: 0, succeeded: 1, skipped: 0` (it **passes**). Restored: same filter → `total: 1, succeeded: 0, skipped: 1`. Surface counts measured: `grep -rc "McpServerTool(" src/AiRaccoon/Tools/*.cs` = **29**, and `docs/reference/agent-memory-server.md`'s `## Tools (29)` heading + 29 table rows match it.
`docs/work/features-native-memory/native-memory.feature:196-206` still carries the comment "stale against the real **22-tool** surface" and the title "All **17** tools are still listed"; `tests/AiRaccoon.Tests/BDD/NativeMemorySteps.cs:909-927` binds that `Then` to a **16-name** list plus a comment that `memory_configure` is excluded. So the drift the ignore documents was already absorbed inside the binding: keeping it ignored means the project's named tool-parity scenario never gates a PR (the replacement, `Unit/Mcp/ToolInventoryTests.cs`, deliberately has no count assertion and guards count only against the doc heading), and un-ignoring it as-is ships a green test whose title and step text are false. `TestHelpers/RegisteredTools.cs:11-13` cites the repo invariant `.ai-badger/invariants/derive-or-delete-the-list.md` ("Never write this number into a test") which this scenario violates twice.

---

## Verified sound (checked, not findings)

- **Gates that redden on cue**: M1, M2, M3 above — the sweep TTL rule, the cross-project/shared-tier delete refusal, and the missing-trait guard all fail on a real edit, with the failing test named, and all return green after revert (7/7, 6/6, 3/3).
- **Skip honesty holds** for the environment-gated suites: `GraphPooledOutputParityTests` → `total: 2, succeeded: 0, skipped: 2`; `CodeEngineRealModelEmbedTests` → `total: 4, succeeded: 0, skipped: 4`; `CaptureGoldenVectors` → skipped. This reproduces the 0814 lane's claim on the two suites it checked (no early `return` masquerading as a pass).
- **The 0814 lane's F1 (hand-pinned tool counts) is fixed**: `tests/AiRaccoon.Tests/E2E/McpServerToolSurfaceE2ETests.cs:21-52` derives from `RegisteredTools.Names()` at test time and its doc comment says so; no `Count.ShouldBe(26)`-style pin remains in the three files that carried them.
- **The H2 scoped-delete and code-corpus non-interference invariants are genuinely tested against real SQLite** (`Integration/Storage/SweepScopeSiblingTests.cs:48-114`, `Integration/Sweep/SweepCodeCorpusNonInterferenceTests.cs:71-85`), and the latter documents why no synthetic red exists for "sweep touches code rows" — an honest comment, not a gap I could falsify.
- **Discovery arithmetic is stable**: 5210 tests at base, 5212 with my two probe classes, exactly +2.

## Still open
- **Seed-embed full-suite slowdown (carried item)**: not reproduced. I did not run the full suite (campaign rule) and did not isolate `WatchIntegrationTests`' seed budget under load; the delta lane's F3 (arrange-phase seed timeout under CPU contention, first red tolerated) stands unverified by me.
- **TRX representation of retried attempts**: I measured the console/MTP summary only. Whether a `*.trx` records per-attempt failures (which `scripts/nightly-triage.py` reads) is unverified; no workflow command I read generates a TRX.
- **`--nologo` mechanism**: I measured the correlation (0 tests vs full discovery, twice) but did not trace whether it is MTP argument parsing or the xunit v3 adapter swallowing it.
- **The 4 other `@ignore`d BDD scenarios** (MRTR, cloud RLS, metrics/tracing, `memory_inspect`) — the delta lane examined them; I only un-ignored the tool-inventory one.
- **The 2 double-counted lane cases**: whether `build-fast` or `build-slow` is the intended owner is an ownership call I cannot make from the tests alone.

## Grade mix
7 MEASURED · 0 READ · 0 INFERRED · 0 UNVERIFIED — severities: 0 BLOCKER · 0 HIGH · 2 MEDIUM (F1, F5) · 5 LOW (F2, F3, F4, F6, F7) · 0 NIT.

## Owner questions
- Should the trait guards validate values against the closed set (`Fast|Slow|Nightly`, `Unit|Integration|E2E|Retrieval|bdd`) instead of name-presence (F1)?
- Should the retry surface emit a retry counter/diagnostic so a retry-recovered failure is visible and ledgerable, rather than only its final verdict (F5)?
- Which lane owns `ThreadResolutionTests`' split-leg theory — split it into a Fast/Unit case and a Slow/Integration case, or accept the duplicate (F2)?
- Should `RetrySurfaceGateTests`' retry mandate be narrowed to tests that hold a real external resource, rather than every Integration/ file (F5)?
- Delete the `@ignore`d 17-tool scenario or rewrite it to 29/derived, now that un-ignoring passes (F7)?
