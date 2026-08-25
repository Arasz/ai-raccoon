# Implementation plan — migrate to Microsoft Testing Platform + xunit v3 4.0.0, add retry for slow/E2E/integration tests

Task lane: `migrate-tests-to-mcp` — branch `task/migrate-tests-to-mcp-xunit4`, base `c2e29ae2` (main).
Date: 2026-08-25. Guide followed: `job-search-ai-assistant/docs/how-to/migrate-to-microsoft-testing-platform.md` (v2, 2026-08-19, jsaa PR #911).
Research record: `docs/work/2026-08-25-mtp-xunit4-migration-research.md` (same worktree).

This is a **plan only**. Nothing in this document authorizes editing files outside `docs/work/` until an
implementation lane is dispatched. Every WP below carries files owned, steps (exact commands), acceptance
criteria (checkable by someone who was not here), a quality gate (the exact command that proves it), and its
parallelism/serialization constraints.

---

## 0. Decisions (with rationale and rejected alternatives)

### A. Retry mechanism — **RECOMMENDED: xRetry.v3 1.0.0 (stable), per-method `[RetryFact]`/`[RetryTheory]`**

Rationale (one paragraph): xunit 4.0.0 has no built-in retry; the choice is between per-method attributes, a
custom class-level attribute, and a CI lane wrapper. xRetry.v3 **1.0.0 stable is confirmed on NuGet** and
declares a hard dependency `xunit.v3.extensibility.core [4.0.0, 5.0.0)` — i.e. it was rebuilt against the exact
4.0.0 extensibility surface that broke (its `RetryTestCaseRunner` subclasses
`XunitTestCaseRunnerBase<XunitTestCaseRunnerContext, ...>` and takes `ParallelMode`, `ExecutionScheduler`,
`FixtureMappingManager` — the changed 4.0.0 APIs). It retries **only the failed test** (the slow lane is the
14-min CI bottleneck at a 30-min timeout; class-level retry would re-run every method of a class — up to 3× the
lane — and blow the budget), it emits a `DiagnosticMessage` per attempt (`"Running test X attempt (1/3)"`,
`"...failed but is set to retry (1/3)..."`, `"...failed and been retried the maximum number of times (3)"`),
which makes retries **visible** under `--xunit-diagnostics on` (the xunit v3 4.0.0 MTP option —
`--diagnostics` is the vstest-era flag and is NOT registered by 4.0.0's MTP CommandLineOptionsProvider;
review round 1, M2), which makes retries visible (this repo's culture rejects silent flake-masking), and it
is maintained upstream rather than being our own code on the just-changed 4.0.0 API surface.

Rejected alternatives, named:
1. **Custom class-level `[Retry(...)]` attribute** — rejected. There is no attribute-driven per-class runner swap
   in xunit v3; a class-level retry would mean a custom `XunitTestClassRunner` wired through a custom
   test-framework entry (the exact "custom TestFramework + runner hooks" surface the repo's own
   `docs/work/2026-08-20-nightly-moe-plan.md` flagged as the risky one for Reqnroll), it re-runs every method in
   a class on one flake (slow-lane budget), and it would be ~6 files of new extensibility code on APIs that just
   changed — over-engineered when a maintained, 4.0.0-pinned library exists (violates ask-if-simpler).
2. **In-house per-method `RetryFact`/`RetryTheory`** — same ~1,100-line attribute-swap diff as xRetry PLUS our own
   discoverer/test-case/runner code on the changed surface PLUS no Reqnroll option. Strictly worse than xRetry.
3. **CI lane-level rerun wrapper** — rejected as the primary mechanism. It does not retry the failed test, it
   re-runs the whole ~14-min slow lane on any flake (worst case 3 × lane), gives developers nothing locally, and
   cannot express "retry only the flaky test". (It remains a valid future backstop, not this task.)
4. **MTP's own `--retry-failed-tests` extension** — rejected. Run-level (re-runs all failures at the end of the
   run, no per-attempt output), and this repo's `docs/work/2026-08-20-nightly-moe-plan.md` already ruled it out
   ("MASKS... wrong order of operations"). The user's ask is test-level retry with visible attempts; xRetry
   delivers that, and a test that fails all 3 attempts still goes RED in the lane — nothing is masked.

**Application strategy (exactly which classes get retry).** A test class is in the retry set iff:

```
(has [Trait(TestCategories.Speed, TestCategories.Slow)])
 OR (has [Trait(TestCategories.Speed, TestCategories.Nightly)])
 OR (file lives under tests/AiRaccoon.Tests/E2E/)
 OR (file lives under tests/AiRaccoon.Tests/Integration/)
AND NOT (has [Trait(TestCategories.Performance, TestCategories.Benchmark)])
```

- E2E/ folder: all 12 test classes carry `Speed=Nightly` (e.g. `McpServerE2ETests.cs:24`), NOT
  `Speed=Slow` — E2E runs in the label-triggered build-nightly-gates job, not build-slow. The
  retry set captures them via both the Nightly trait and the folder rule, so the surface is
  unaffected; the claim in the earlier draft that they were Slow was wrong (review round 1, S3).
- Integration/ folder: **252** files; ~90 of them carry NO `Slow`/`Nightly` trait (they run in
  the fast lane) — the folder rule pulls them in because the user asked for *integration* tests,
  not only slow ones.
- Speed=Slow: 154 files. Speed=Nightly: 35 files (disjoint; 189 total).
- Union size: **281 files / 1,634 attribute lines** (derived by script, WP4 step 7; 266 of them
  actually carry `[Fact]`/`[Theory]`). Correction note: the "minus 3 Performance=Benchmark
  files" is really a minus ONE — only `Integration/ParityGateTests.cs` is in the union; the two
  QueryGuard files (`Unit/Memory/QueryGuard/QueryGuardPolicyTests.cs`,
  `Unit/Memory/QueryGuard/Structural/StructuralNoisePerformanceTests.cs`) are `Speed=Fast` and
  never were in it. A retried benchmark lies about its budget; Reqnroll BDD scenarios
  (Category=bdd, no Speed trait — out of the user's ask; `xRetry.v3.Reqnroll` 1.0.0 exists on
  NuGet if BDD flakiness ever warrants it, it is NOT added now); all `Speed=Fast` classes outside
  the two folders (a unit test that needs retry is a bug).
- Parameters: 2 attempts for the probe tests (`[RetryFact(2)]`/`[RetryTheory(2)]` — see WP4,
  the `(1/2)` grep needs them); **3 attempts default** for the production surface (`[RetryFact]`,
  0 ms delay). The E2E `Skip`-carrying fact swaps to `[RetryFact(Skip = "...")]` (Skip is
  inherited from `FactAttribute`; the discoverer honors it — verified in xRetry source).

### B. coverlet.collector — **DROP** (guide step 1)

Verified: the only references are the `PackageVersion` in `tests/Directory.Packages.props` and the
`PackageReference` in `tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj`. No `--collect`/`--coverage`/coverage gate
anywhere in `.github/workflows/` or `scripts/` (searched). The guide says drop it; there is no consumer.

### C. Reqnroll.xunit.v3 3.3.4 and TngTech.ArchUnitNET.xUnitV3 0.13.4 — **keep pinned, prove by build**

- `TngTech.ArchUnitNET.xUnitV3` 0.13.4 **is the newest version on NuGet** (0.13.4 is the tip). jsaa kept 0.13.3
  pinned through the same migration and it worked. Keep pinned; the WP1 build gate proves it.
- `Reqnroll.xunit.v3` 3.3.4 is also the **newest on NuGet** (checked the flat container index: 3.3.4 is the tip).
  It is the single biggest migration risk: it registers a custom `TestFramework` + runner hooks on the 4.0.0
  surface that changed. **WP1 is sequenced first precisely as the mandatory Reqnroll spike** (the MoE plan called
  this "a mandatory Reqnroll spike" before adopting MTP). The BDD lane gate is `Category=bdd` run, not just build.
- **Fallback (named, not implemented):** if WP1's build or BDD run breaks on Reqnroll under xunit 4.0.0, there is
  NO newer Reqnroll.xunit.v3 to try (3.3.4 is the tip). The migration is then **BLOCKED**: apply §7 Rollback
  (revert the four WP1 files — the retry work has not started, so nothing else to unwind), file an upstream issue
  at https://github.com/reqnroll/Reqnroll describing the 4.0.0 incompatibility, and re-run WP1 when a compatible
  Reqnroll ships. Do NOT attempt a hand-written Reqnroll adapter.

### D. CI workflow changes (guide §4–5)

All four `dotnet test` invocations in `.github/workflows/build.yml` move to the `--project tests/AiRaccoon.Tests`
form (the documented MTP-mode form; the positional form still works on SDK 10 but the guide mandates `--project`).
Each of the four jobs' `env:` blocks gains `TESTINGPLATFORM_TELEMETRY_OPTOUT: true` (xunit v3 ≥ 3.2.0 pulls
`Microsoft.Testing.Extensions.Telemetry`; the guide's measured opt-out). WP4 additionally adds `--diagnostics` to
the build-slow and build-nightly-gates run steps (surfaces xRetry attempt lines). Nothing else changes: filters,
`--no-build --nologo -v m`, `DOTNET_CLI_TELEMETRY_OPTOUT`, crash-dump env, the `changes` job and its CODE_REGEX
(already matches `build.yml` and the props files), `setup-dotnet` `global-json-file` pinning. `.ai-badger/config.json`
`commands.test` stays `"dotnet test"` (guide §5: unchanged). Zero-matched-tests exit code 8 does not apply —
every lane's filter matches (baseline proves it). No `testconfig.json`/runsettings work exists here (no `.runsettings`,
no `testconfig.json`, no infra opt-in env var in this repo — guide step 3 does not apply; state that in the PR).

### E. Directory.Build.props + global.json (guide step 2)

- `global.json`: add a `test` section next to the existing `sdk` section (no new SDK pin):
  `{"test": {"runner": "Microsoft.Testing.Platform"}}`.
- Root `Directory.Build.props`: add
  `<PropertyGroup Condition="$(MSBuildProjectName.EndsWith('.Tests'))"><UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup>`.
  Condition on the name suffix, not `IsTestProject` (unavailable when Directory.Build.props loads — guide §2 pitfall).

### F. `[CollectionDefinition(DisableParallelization = true)]` — **check by build; expected GREEN; fallback named**

Verified against the xunit 4.0.0 release-notes text: the obsolescence covers the **assembly-level**
`[assembly: CollectionBehavior]` (`DisableTestParallelization`/`MaxParallelThreads`/`ParallelAlgorithm` →
`[assembly: Parallelization]` `Mode`/`MaxThreads`/`Algorithm`) and runner-context members
(`XunitTestAssemblyRunnerBaseContext.DisableParallelization` → `ParallelMode`). **Collection-level
`DisableParallelization` on `[CollectionDefinition]` is NOT flagged obsolete.** There are **9** such definitions
in this repo (research record said 5 — see corrections): `E2E/E2ETestCollection.cs`,
`Integration/Setup/QuietLoggingCollection.cs`, `Integration/WatchIntegrationCollection.cs`,
`BDD/CodeCorpusCollection.cs`, `BDD/FileWatcherCollection.cs`,
`Unit/Observability/ObservabilityCollection.cs`, `Unit/Setup/CliOutputRoutingCollection.cs`,
`Unit/TestHelpers/ConsoleCaptureCollection.cs`, `TestHelpers/BwsAccessTokenCollection.cs`. WP2 checks the WP1
build log for CS0618; expected zero. **Fallback if CS0618 appears anyway:** scope-suppress with a
`#pragma warning disable CS0618` + one-line comment on each affected definition (E2E port-race tests and console/
env capture tests MUST stay serialized; there is no 4.0.0 replacement attribute for collection-level
serialization), file an upstream note, and record the deviation in WP5's doc. Do NOT switch to assembly-level
`Parallelization` — that would serialize the whole suite.

### G. Version bump — **NO bump. Do not touch `VERSION`.**

Verified: `tests/AiRaccoon.Tests/Unit/Setup/VersionContractTests.cs` is **derive-only** — it reads the root
`VERSION` file and asserts the built `src/AiRaccoon` assembly version, `PackageVersion`, and
`src/AiRaccoon/.mcp/server.json` derive from it. There is **no literal version pin** in the test, so a test-infra
migration cannot break it, and no product version changes. Precedent: the repo's own
`docs/work/2026-08-20-nightly-moe-plan.md` — "All tests-only (no version bump, #277 precedent)". `VERSION` is
1.35.0 (recent product bump) and stays 1.35.0. WP5's gate asserts `git diff -- VERSION` is empty on the PR branch.

---

## 1. Corrections to the research record (verified against the repo at c2e29ae2)

1. **`[CollectionDefinition(DisableParallelization = true)]` count is 9, not 5.** The record named 5
   (CliOutputRouting, ConsoleCapture, Observability, QuietLogging, WatchIntegration). The other four:
   `E2E/E2ETestCollection.cs`, `BDD/CodeCorpusCollection.cs`, `BDD/FileWatcherCollection.cs`,
   `TestHelpers/BwsAccessTokenCollection.cs`. WP2 must check all 9.
2. **`nightly.yml` does not exist at this base.** Commit `7e141c59` "chore: remove nightly workflow" deleted it.
   The record's "nightly.yml's unfiltered dotnet test" is stale. `Speed=Nightly` tests now run only via the
   label-triggered `build-nightly-gates` job in `build.yml` (requires the `run-nightly-gates` label or
   workflow_dispatch). `scripts/nightly-triage.py` still exists but is orphaned by the removal; **this task does
   not touch it** (its ledger/record-and-tolerate design is why xRetry was chosen over MTP's run-level retry —
   see decision A.4).
3. **The exact-FullName reflection gate exists in TWO files, not one.** Both
   `Unit/SpeedGateCoverageTests.cs` and `Unit/CategoryGateCoverageTests.cs` use the identical `HasFactOrTheory`
   (matches `AttributeType.FullName is "Xunit.FactAttribute" or "Xunit.TheoryAttribute"`). A `[RetryFact]`
   method's attribute type is `xRetry.v3.RetryFactAttribute` → both gates silently lose every retried class
   (the class drops out of `TestClasses()` — the gate passes *vacuously*). WP4 fixes both, TDD-style.
4. **`xRetry.v3` stable 1.0.0 IS published** (the record left it to verify). NuGet flat-container index:
   `[..., 1.0.0-rc3, 1.0.0]`, and its nuspec pins `xunit.v3.extensibility.core [4.0.0, 5.0.0)`. The 4.0.0
   support is package-level fact, not just repo-commit rumor.
5. **Collection-level `DisableParallelization` is not obsolete** in 4.0.0 (see decision F) — the record's risk
   item 3 is downgraded from "likely break" to "check and move on" (the check stays, TreatWarningsAsErrors makes
   any surprise CS0618 a hard failure, which is exactly what the check is for).
6. **Integration folder is ~120+ files, not ~60.** (100 files matched before truncation, plus ~90 of them carry
   no Slow/Nightly trait.) The retry surface is therefore ~270–280 files / ~1,100 attribute lines, not "~187
   files". Sizing note for the mechanical commit.
7. **Fast-lane baseline already captured** at `/tmp/baseline-fast-c2e29ae2.txt`: **3330 passed, 1 skipped
   (`Integration.Memory.PromotionScoringRealDataTests.ScoresCorrelateWithHandLabeledUsefulness`), 0 failed,
   Duration 29 s** — this is the acceptance number for the fast lane.
8. `Reqnroll.xunit.v3` has **no** version newer than 3.3.4 and `TngTech.ArchUnitNET.xUnitV3` none newer than
   0.13.4 (both verified on NuGet) — the fallbacks in decision C are the only honest ones.

---

## 2. Work packages

### WP0 — Baseline capture (pre-change, on `c2e29ae2`)

Files owned: none (writes only `/tmp/baseline-*.txt`).
Serialization: FIRST — nothing starts until the numbers exist.

Steps (each command run in the worktree at base, vstest mode — the pre-change form is exactly what CI runs today):
1. Fast lane: already captured — `/tmp/baseline-fast-c2e29ae2.txt` (3330 passed / 1 skipped / 0 failed). Verify the file exists; do not re-run.
2. BDD lane (cheap): already captured — `/tmp/baseline-bdd-c2e29ae2.txt` (172 passed / 5 skipped / 0 failed, 177 total). Verify; do not re-run.
3. Slow lane: already captured — `/tmp/baseline-slow-c2e29ae2.txt` (968 passed / 4 skipped / 0 failed, 972 total, 57 s local — the "~14 min" figure is the CI job's, build.yml:177). Verify; do not re-run.
4. Nightly-tagged lane: already captured — `/tmp/baseline-nightly-c2e29ae2.txt` (147 passed / 10 skipped / 0 failed, 157 total, 6 m 18 s). Verify; do not re-run.
5. Discovery parity row: `dotnet test --list-tests 2>&1 | tee /tmp/baseline-list-tests-c2e29ae2.txt` — record the total case count.

ACCEPTANCE CRITERIA:
- Each baseline file ends with the MTP-less vstest summary line `Passed! - Failed: 0, Passed: <N>, Skipped: <S>, Total: <N+S>` (bdd/slow/nightly) and `exit=0`.
- The `Skipped` numbers are non-zero only where the suite already skips (fast: 1 skip; slow: GoldenFileTests skips on osx-arm64 per the build.yml comment; bdd/nightly: whatever they are — the point is the AFTER run must reproduce them exactly).

QUALITY GATE: `grep -l 'Passed!' /tmp/baseline-{fast,bdd,slow,nightly}-c2e29ae2.txt` lists all four files, and the four numbers (pass/skip/total per lane) are written into §6's matrix before WP1 starts.

### WP1 — Package bump + MTP enablement + Reqnroll/ArchUnitNET spike (guide steps 1–2)

Files owned (exactly four):
- `tests/Directory.Packages.props`
- `Directory.Build.props` (root)
- `global.json`
- `tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj`

Steps:
1. In `tests/Directory.Packages.props`: `xunit.v3` 3.2.2 → **4.0.0**; `xunit.v3.extensibility.core` 3.2.2 → **4.0.0** (pin kept even though it is not directly referenced — guide bumps it); `xunit.runner.visualstudio` 3.1.5 → **4.0.0**; **delete** the `coverlet.collector` `PackageVersion` line (decision B). Leave untouched: `Microsoft.NET.Test.Sdk` 18.8.1, `Reqnroll.xunit.v3` 3.3.4, `Reqnroll.Tools.MsBuild.Generation` 3.3.4, `TngTech.ArchUnitNET.xUnitV3` 0.13.4, and all others.
2. In `tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj`: delete the `<PackageReference Include="coverlet.collector" PrivateAssets="all"/>` line. Nothing else.
3. In `global.json`: add the `test` section — final content:
   ```json
   {
     "sdk": { "version": "10.0.400", "rollForward": "latestPatch" },
     "test": { "runner": "Microsoft.Testing.Platform" }
   }
   ```
4. In root `Directory.Build.props`: add below the `ValidateVersionFile` target:
   ```xml
   <PropertyGroup Condition="$(MSBuildProjectName.EndsWith('.Tests'))">
     <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner>
   </PropertyGroup>
   ```
5. Spike build (this is the Reqnroll + ArchUnitNET + CollectionDefinition proof):
   `dotnet build --nologo > /tmp/wp1-build.log 2>&1; echo "exit=$?" >> /tmp/wp1-build.log`
   (no PIPESTATUS — that is bash-only and the worktree shell is zsh; review round 1, N1.)
6. MTP banner smoke test + **OutputType checkpoint** (review round 1, S4): the jsaa guide's
   precondition "xunit v3 test projects already have OutputType=Exe" does NOT hold here —
   `AiRaccoon.Tests.csproj` has only `IsTestProject=true`. Assume the SDK's MTP targets emit the
   test executable; PROVE it here: `dotnet test --project tests/AiRaccoon.Tests --list-tests 2>&1 | tee /tmp/wp1-list.log` —
   expect the MTP "Test run summary" banner, not `[xUnit.net ...]` vstest lines. If the MTP
   banner does not appear (or the run fails because no executable was produced), STOP and set
   `<OutputType>Exe</OutputType>` in the csproj, then re-run this step. Do not guess past this
   checkpoint.
7. Reqnroll spike (the decision-C gate): `dotnet test --project tests/AiRaccoon.Tests --filter "Category=bdd" --no-build --nologo -v m 2>&1 | tee /tmp/wp1-bdd.log`
8. Fast lane under MTP: `dotnet test --project tests/AiRaccoon.Tests --filter "Speed=Fast&Performance!=Benchmark" --no-build --nologo -v m 2>&1 | tee /tmp/wp1-fast.log`

ACCEPTANCE CRITERIA:
- `dotnet build` exits 0 with **zero warnings** (TreatWarningsAsErrors: any CS0618 on the 9 collection definitions or any Reqnroll/ArchUnitNET break surfaces HERE, not later).
- `--list-tests` prints the MTP banner (guide §2 smoke test) and the same case count as WP0 step 5.
- `Category=bdd` run passes with the same pass/skip counts as `/tmp/baseline-bdd-c2e29ae2.txt` (Reqnroll survived 4.0.0).
- Fast lane passes with **3330 passed / 1 skipped / 0 failed** — identical to WP0.
- If the build or BDD run breaks on Reqnroll/ArchUnitNET: STOP. Apply §7 Rollback (four files), file the upstream issue (decision C), do not start WP2–WP5.

QUALITY GATE: `grep -cE 'warning|error' /tmp/wp1-build.log` → 0; `grep -c 'Test run summary' /tmp/wp1-list.log` → ≥1; `grep 'Passed!' /tmp/wp1-bdd.log` and `/tmp/wp1-fast.log` match the baseline numbers.

Parallelism: **serial** — WP2, WP3, WP4 all depend on its outcome (build green, Reqnroll alive).

### WP2 — CollectionDefinition serialization check (decision F)

Files owned: none in the happy path; fallback touches the 9 files listed in decision F.
Serialization: after WP1 (needs `/tmp/wp1-build.log`); parallel with WP3 and WP4 (no shared files).

Steps:
1. `grep -n 'CS0618' /tmp/wp1-build.log` → expect **zero hits** (the 9 definitions compile clean under 4.0.0).
2. If hits exist: apply the fallback — `#pragma warning disable CS0618` + one-line comment on each of the 9 definitions, rebuild, and record the deviation in WP5.

ACCEPTANCE CRITERIA: no CS0618, or the fallback applied and the build is green; the E2E serialized collection still serializes (port-race tests unchanged behavior).
QUALITY GATE: `grep -c 'CS0618' /tmp/wp1-build.log` → 0 (happy path), else `dotnet build --nologo` → exit 0 after the pragma edits.

### WP3 — CI workflow migration (decision D)

Files owned: `.github/workflows/build.yml` (four lane run steps + four job `env:` blocks).
Serialization: after WP1; parallel with WP2; **serial BEFORE WP4** (WP4 owns a later edit to the same file).

Steps (one commit):
1. In `build-fast`, `build-bdd`, `build-slow`, `build-nightly-gates`: change `run: dotnet test --filter ...` to `run: dotnet test --project tests/AiRaccoon.Tests --filter ...` (all other args identical) **and DROP `--nologo`** (MTP finding, see below).
2. In each of the four jobs' `env:` blocks add `TESTINGPLATFORM_TELEMETRY_OPTOUT: 1` (review
   round 1, S5: the guide's measured opt-out is `=1`, not `true` — YAML `true` renders the string
   "true" which MTP telemetry may not honour; keep `DOTNET_CLI_TELEMETRY_OPTOUT` and the
   crash-dump env).
3. Do NOT touch: `changes` job, CODE_REGEX, checkout/setup-dotnet pins, timeout-minutes, dump steps, the `run-nightly-gates` label logic.
4. Validate the YAML parses: `python3 -c "import yaml,sys; yaml.safe_load(open('.github/workflows/build.yml'))"`.

ACCEPTANCE CRITERIA: the four `dotnet test` lines are the `--project` form; four `TESTINGPLATFORM_TELEMETRY_OPTOUT` entries; YAML parses; `git diff -- .github/workflows/build.yml` shows ONLY those changes.
QUALITY GATE: the YAML parse command above, plus CI on the PR (build-fast/build-bdd/build-slow green — the real gate).

### WP4 — Retry rollout (decision A) — TDD-first

Files owned:
- `tests/Directory.Packages.props` (add `xRetry.v3` 1.0.0)
- `tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj` (add `<PackageReference Include="xRetry.v3"/>`)
- New probe/behavior test classes (below)
- `tests/AiRaccoon.Tests/Unit/SpeedGateCoverageTests.cs` and `tests/AiRaccoon.Tests/Unit/CategoryGateCoverageTests.cs` (gate fix)
- The retry surface: `grep -rl 'Speed, TestCategories.Slow\|Speed, TestCategories.Nightly' tests/AiRaccoon.Tests --include='*.cs' | grep -v '/obj/'` ∪ `find tests/AiRaccoon.Tests/E2E tests/AiRaccoon.Tests/Integration -name '*.cs' | grep -v '/obj/'`, minus `Integration/ParityGateTests.cs` (the ONLY Performance=Benchmark file actually in the union — review round 1, S2). = 281 files / 1,634 attribute lines.
- `.github/workflows/build.yml` (add `--xunit-diagnostics on` to the build-slow and build-nightly-gates run steps — must land AFTER WP3's commit).

Serialization: after WP1 and after WP3 (shares `build.yml` with WP3; needs the 4.0.0 build). Parallel with WP2.

Steps (strict order — TDD RED first; steps 3–5 reordered per review round 1, M1: the RED must be
witnessable, and `--no-build` against a stale assembly would exit 8 with zero matched tests instead):
1. **Write the behavior tests + gate probes first (uncompilable — that is the first RED).** Create
   `tests/AiRaccoon.Tests/Unit/TestInfra/RetryFactRetriesOnTransientFailureTests.cs` — a class with
   `[Trait(Category, Unit)]`, `[Trait(Speed, Fast)]` and one method `[RetryFact(2)]` that fails while a dedicated
   `static int` counter is 1 (attempt 1 throws, attempt 2 passes) and asserts the counter reached 2 (proves the
   retry ran). One behavior test per class, each with its own counter field (no cross-test state).
   Also `.../RetryTheoryRetriesDataRowTests.cs` — same pattern with `[RetryTheory(2)]` + `[InlineData]`.
   These classes are the **gate probes**: they carry retry attributes and regular traits, so once the gates
   recognize retry attributes they are covered; before the fix they are invisible. `(2)` matters: the quality
   gate greps `attempt (1/2)` and the default is 3 attempts (review round 1, M3).
2. **Gate-busting tests.** In `Unit/SpeedGateCoverageTests.cs` add
   `[Fact] public void RetryAttributeClasses_RemainVisibleToTheGuard()` asserting the probe classes ARE in
   `TestClasses()`; the identical test in `Unit/CategoryGateCoverageTests.cs`. (Failure mode being caught: the
   exact-FullName `HasFactOrTheory` drops retried classes from gate coverage — a silently vacuous pass.)
3. **Witness RED (compile)**: `dotnet build --nologo` → the probes reference `xRetry.v3` attributes with no
   package → **CS0246 compile failure** (the whole project fails). Record it. Do NOT run dotnet test here —
   with `--no-build` it would run the stale WP1 assembly, match zero tests, and exit 8 (the vacuous signal the
   repo invariants forbid).
4. **GREEN the retry library**: add `<PackageVersion Include="xRetry.v3" Version="1.0.0"/>` to `tests/Directory.Packages.props` and `<PackageReference Include="xRetry.v3"/>` to the csproj. `dotnet build --nologo` → green.
5. **Witness RED (gate)**: `dotnet test --project tests/AiRaccoon.Tests --filter "FullyQualifiedName~RetryAttributeClasses_RemainVisibleToTheGuard" --no-build --nologo -v m` → the two new gate tests FAIL (probes invisible to the exact-FullName `HasFactOrTheory`). Record the output — this is the gate-busting witness.
6. **GREEN the gates**: change `HasFactOrTheory` in BOTH gate files to accept derived attributes:
   `a.AttributeType.FullName is "Xunit.FactAttribute" or "Xunit.TheoryAttribute" || typeof(FactAttribute).IsAssignableFrom(a.AttributeType) || typeof(TheoryAttribute).IsAssignableFrom(a.AttributeType)`.
   (The Theory arm is technically dead for xRetry — `RetryTheoryAttribute : RetryFactAttribute : FactAttribute` —
   but harmless; review round 1, N6.) Re-run step 5's filter → GREEN. Run the full gate trio
   (`--filter "FullyQualifiedName~SpeedGateCoverageTests|FullyQualifiedName~CategoryGateCoverageTests|FullyQualifiedName~BddGateCoverageTests"`) → GREEN.
7. **Prove-the-check-fails for exhaustion (temporary RED witness, then delete):** add a
   throwaway `[RetryFact(2)]` method that always throws; run it; witness it go RED (the surfaced
   exception carries the LAST attempt number — e.g. "attempt 2" — which is itself the retry
   proof); then DELETE the throwaway method (a permanent red test is not acceptable; the witness
   is recorded in the commit message/WP log).
   **AMENDED (plan review round 2, M2 — see §8):** the plan's original `--xunit-diagnostics on`
   attempt-line grep is UNWITNESSABLE under MTP — xRetry.v3 1.0.0 emits per-attempt messages via
   the xunit message BUS (`RetryTestCaseRunner.cs:103`), and the MTP in-process runner drops
   message-bus DiagnosticMessages (only diagnostic-SINK messages reach output — proven
   empirically with a TestContext.SendDiagnosticMessage probe, which fired, while xRetry's
   "attempt (1/2)" lines appeared nowhere under `--xunit-diagnostics on`, `--xunit-internal-diagnostics on`,
   `-v n`, or the MTP `--diagnostic` file). No upstream fix (1.0.0 is the tip). Retry visibility
   under MTP therefore = (a) the two permanent probe tests (counter==2 assertions run in every
   suite), (b) exhaustion still goes RED with the last attempt's exception message, (c) per-attempt
   lines remain visible on the Rider/vstest path. The attempt-line grep is replaced by: probes
   pass + the exhaustion RED witness recorded at implementation time.
8. **Mechanical swap** (one commit): derive the surface per decision A (command in "Files owned"; exclude
   `/obj/`), then for each file rewrite
   `[Fact` → `[RetryFact` and `[Theory` → `[RetryTheory` (regex `\[Fact\b` / `\[Theory\b` — the only
   paren-form in the surface, `E2E/ModelMigrationCrashRecoveryE2ETests.cs:294 [Fact(Skip = "...")]`, swaps via
   the prefix and compiles because Skip is inherited) and insert `using xRetry.v3;` after `using Xunit;` where
   missing. Script asserts zero non-attribute `[Fact` matches before rewriting (drift guard; review round 1,
   N3 — the current surface is verified clean: all 1,634 matches are real attribute lines). Files with no
   `[Fact]`/`[Theory]` are no-ops (helpers like `Integration/BaselineQueryCatalog.cs`).
   Script stays in `/tmp` (one-shot tooling is not repo debt; the exact command is recorded here).
   Print the file count and line count for the record: expect **281 files / 1,634 lines** (review round 1, S2).
9. **Post-swap gates**: rerun step 6's gate trio → GREEN (every retried class still carries its traits; the
   gates still see every class). `dotnet build --nologo` → 0 warnings.
10. **Visibility on the lanes — DROPPED (review round 2, M2):** the plan originally added
   `--xunit-diagnostics on` to build-slow/build-nightly-gates; the round-2 finding proved it does
   NOT surface xRetry's per-attempt lines under MTP (message-bus drop, see step 7). Do NOT add
   the flag — it would add output volume for zero retry visibility. (No build.yml edit in this
   step.)
11. **Fast + bdd local verification**: `dotnet test --project tests/AiRaccoon.Tests --filter "Speed=Fast&Performance!=Benchmark" --no-build -v m` → **3334/1/0 (3335) = baseline 3330/1/0 + the 4 new fast-lane tests** (2 probes + 2 gate-busting; no --nologo — VSTest-only, dotnet/sdk#55309); `--filter "Category=bdd"` → baseline numbers (172/5/0).
12. **Slow/nightly verification goes to CI** (PR → build-slow job, label `run-nightly-gates` for the nightly job). Local slow re-run ONLY if CI cannot be used (that is the second and final allowed local slow run; do not run it more than once).

ACCEPTANCE CRITERIA (checkable by a stranger):
- The two gate files contain the `IsAssignableFrom` fix; both new gate tests exist and pass.
- `grep -rl '\[RetryFact' tests/AiRaccoon.Tests --include='*.cs' | grep -v '/obj/' | wc -l` ≈ 268 (the `\[RetryFact`
  prefix form counts `[RetryFact(Skip=…)]` too — review round 1, S2), and no `Performance, TestCategories.Benchmark`
  file carries a Retry attribute.
- The throwaway exhaustion witness is GONE (its RED + the last-attempt exception, e.g. "attempt 2", are recorded in the WP log/commit message).
- NO `--xunit-diagnostics` flag added to build.yml (round 2 amendment — see step 10).
- No `[Fact]`/`[Theory]` remains in the surface files: `for f in $(surface); do grep -H '\[Fact\]\|\[Theory\]' $f; done` prints nothing.

QUALITY GATE:
- `dotnet test --project tests/AiRaccoon.Tests --filter "FullyQualifiedName~SpeedGateCoverageTests|FullyQualifiedName~CategoryGateCoverageTests|FullyQualifiedName~BddGateCoverageTests" --no-build -v m` → all pass.
- `dotnet test --project tests/AiRaccoon.Tests --filter "FullyQualifiedName~RetryFactRetriesOnTransientFailureTests|FullyQualifiedName~RetryTheoryRetriesDataRowTests" --no-build -v m` → both pass (the counter==2 assertions are the retry-proof; per-attempt lines are MTP-dropped by design — round 2 amendment).
- `dotnet build --nologo` → exit 0, 0 warnings.
- CI: build-fast/build-bdd/build-slow green on the PR; counts per §6 (fast = 3334/1/0 with the 4 new tests; bdd/slow/nightly identical to baseline).

### WP5 — Documentation + version statement (decision G)

Files owned: `docs/work/2026-08-25-mtp-xunit4-migration.md` (implementation record, new); corrections folded into the research record if the lane owns it. `VERSION` — deliberately NOT touched.
Serialization: last (needs WP1–WP4 outcomes); parallel with nothing.

Steps:
1. Write the implementation record: what was changed (packages, runner, CI forms, retry surface), the baseline→after numbers from §6, the decision record (A–G), the witnessed REDs, and any deviation (e.g., WP2 fallback).
2. `git diff -- VERSION` must be empty; `git status` shows no `VERSION` modification.

ACCEPTANCE CRITERIA: the record exists and cites the WP0 baseline numbers and the post-migration numbers; VERSION untouched.
QUALITY GATE: `git diff --name-only HEAD~1 -- VERSION` prints nothing; the record file is listed in the PR.

---

## 3. Test strategy (TDD RED-first, gate-busting)

New behavior introduced by this task = the retry rollout (WP4). Everything else is config/package migration whose
"check" is the build + unchanged counts.

- **Behavior tests (written before the package, RED by compilation):**
  `tests/AiRaccoon.Tests/Unit/TestInfra/RetryFactRetriesOnTransientFailureTests.cs`,
  `tests/AiRaccoon.Tests/Unit/TestInfra/RetryTheoryRetriesDataRowTests.cs` — deterministic attempt counters
  prove attempt-2 recovery; each class owns a dedicated static counter (parallel-run-safe by construction).
- **Gate-busting (prove-the-check-fails):** the new
  `RetryAttributeClasses_RemainVisibleToTheGuard` test in BOTH `SpeedGateCoverageTests.cs` and
  `CategoryGateCoverageTests.cs` is the check that the exact-FullName gate still covers retried classes. It is
  witnessed RED (probes invisible → vacuous gate) BEFORE the `IsAssignableFrom` fix, GREEN after. The probe
  classes carry `Speed=Fast, Category=Unit` so they are never the "ungated class" the guard complains about.
- **Exhaustion path (cannot live green in a suite):** the throwaway always-throws `[RetryFact(2)]` witness is
  run RED with `--diagnostics`, the "attempt (1/2)" + "failed and been retried the maximum number of times (2)"
  lines recorded, then deleted — the check has been seen fail.
- **No gate may change without a busting witness.** WP4 step 3 is that witness; if an implementation lane skips
  it, the WP is not done (repo invariant: "A check you have not seen fail is not a check").
- **Unchanged gates that must stay green:** `BddGateCoverageTests` (FeatureTitle-based — unaffected by retry
  attributes, but run it anyway), `TheGuardSeesTheTestClasses` counts (>100 — the suite is ~600 classes, still
  true), `VersionContractTests` (derive-only — untouched), and the `Performance=Benchmark` carve-out (their
  classes must NOT be retried; the acceptance grep proves it).

---

## 4. Verification matrix

Baseline commands run on `c2e29ae2` (WP0, vstest mode, positional form as CI does today). Post-migration
commands are the MTP `--project` form (WP1/WP3). Counts must be identical; retry changes no counts (a
pass-after-retry is one pass; a skip is a skip; a 3-attempt exhaustion is one fail).

| Lane | Filter | Command (AFTER form shown) | BEFORE (c2e29ae2) | AFTER (PR branch) |
|---|---|---|---|---|
| fast | `Speed=Fast&Performance!=Benchmark` | `dotnet test --project tests/AiRaccoon.Tests --filter "Speed=Fast&Performance!=Benchmark" --no-build -v m` | **Passed 3330 / Skipped 1 / Failed 0** (captured: `/tmp/baseline-fast-c2e29ae2.txt`) | **3334/1/0 (3335)** = baseline + the 4 new fast-lane tests (2 retry probes + 2 gate-busting) — local + CI build-fast |
| bdd | `Category=bdd` | `dotnet test --project tests/AiRaccoon.Tests --filter "Category=bdd" --no-build -v m` | **Passed 172 / Skipped 5 / Failed 0** (177; `/tmp/baseline-bdd-c2e29ae2.txt`) | identical — local + CI build-bdd |
| slow | `Speed=Slow&Performance!=Benchmark` | `dotnet test --project tests/AiRaccoon.Tests --filter "Speed=Slow&Performance!=Benchmark" --no-build -v m` | **Passed 968 / Skipped 4 / Failed 0** (972; `/tmp/baseline-slow-c2e29ae2.txt`, 57 s local) | identical — **CI build-slow job** (no second local run unless CI unavailable) |
| nightly-gates | `Speed=Nightly&Performance!=Benchmark` | `dotnet test --project tests/AiRaccoon.Tests --filter "Speed=Nightly&Performance!=Benchmark" --no-build -v m` | **Passed 147 / Skipped 10 / Failed 0** (157; `/tmp/baseline-nightly-c2e29ae2.txt`) | identical — CI `build-nightly-gates` (label `run-nightly-gates` on the PR) |
| discovery | (none) | `dotnet test --project tests/AiRaccoon.Tests --list-tests` | WP0 count (`/tmp/baseline-list-tests-c2e29ae2.txt`) | identical count (RetryFact/RetryTheory produce one case per method/row, like Fact/Theory) |

Rules: (1) a lane is verified only by the exact commands above; (2) any count drift is a STOP — triage before
proceeding (drift means the migration changed what runs); (3) the slow lane's AFTER numbers come from the CI job
log (`Passed! - Failed: 0, Passed: N, Skipped: S, Total: N+S`); (4) if CI is unreachable, the single permitted
second local slow run provides them — never a third. NOTE (MTP finding, 2026-08-25, verified during WP1): ALL
commands above drop `--nologo` — it is a VSTest-only flag; under MTP mode dotnet test forwards it and reports
"Zero tests ran" (upstream: dotnet/aspnetcore#55309-style; reproduced locally during WP1).

---

## 5. Parallelism summary

```
WP0 (baseline)            — serial, first
WP1 (packages + MTP)      — serial (spike gates everything)
WP2 (collections check)   — parallel with WP3, WP4        (owns no files in happy path)
WP3 (CI build.yml, part 1)— parallel with WP2; serial before WP4   (shares build.yml with WP4)
WP4 (retry)               — serial after WP1 and WP3      (shares build.yml with WP3; owns props/csproj/gates/surface)
WP5 (docs + VERSION)      — serial, last
```
Shared-file conflicts: only `build.yml` (WP3 → WP4, strictly ordered) and `tests/Directory.Packages.props` +
the csproj (WP1 → WP4, strictly ordered, distinct lines). WP2 and WP4 both *read* WP1's build log but own
disjoint files.

---

## 6. Rollback (guide §8 pattern)

- **WP1-only rollback** (Reqnroll/ArchUnitNET spike fails): `git revert` the WP1 commit — removes the `test`
  section from `global.json`, the `UseMicrosoftTestingPlatformRunner` property from `Directory.Build.props`,
  restores `xunit.v3`/`xunit.v3.extensibility.core`/`xunit.runner.visualstudio` to 3.2.2/3.2.2/3.1.5 and
  `coverlet.collector` 10.0.1 in both files. The adapter packages (`xunit.runner.visualstudio` +
  `Microsoft.NET.Test.Sdk`) were never removed, so `dotnet test` falls straight back to vstest.
- **WP3 rollback**: revert the `build.yml` commit (positional form + telemetry env restore).
- **WP4 rollback**: revert the package line (xRetry.v3), the gate-fix commit, the mechanical swap commit, and
  the `--diagnostics` edit — in that order; the gates return to exact-FullName matching and every surface class
  back to `[Fact]`/`[Theory]`.
- **Full rollback** = the three reverts above; no runsettings/testconfig.json exist here, so there is no
  config-file cleanup (guide §8's second bullet does not apply to this repo).
- Rollback is always `git revert` of the named commits — never new code. Verify after revert with the WP1 gate
  command (`dotnet build --nologo` + fast lane) before abandoning the branch.

---

## 7. Risks and mitigations (summary)

1. **Reqnroll.xunit.v3 3.3.4 vs 4.0.0** — the spike (WP1 step 7) runs before any other change; fallback named in decision C; no newer Reqnroll exists.
2. **ArchUnitNET 0.13.4 vs 4.0.0** — jsaa kept 0.13.3 pinned and it worked; WP1 build proves it; 0.13.4 is the newest available.
3. **CS0618 on the 9 `[CollectionDefinition]` files** — release notes say collection-level is not obsolete; the WP1 build is the check; WP2 fallback named.
4. **Retry surface diff size (~1,100 lines)** — mechanical, script-generated, one commit, reviewable as a unit; alternative mechanisms each fail a stated requirement (decision A).
5. **Retries masking real defects** — the repo's MoE position. Mitigation: per-method retry with visible per-attempt diagnostics on the two slow lanes; a test that exhausts 3 attempts still goes RED; nothing is auto-quarantined.
6. **MTP behavioral differences** — zero-tests exit 8 does not apply (all filters match, baseline proves it); runsettings→testconfig conversion does not apply (none exist).
7. **Local slow-lane budget** — exactly one pre-change local run (WP0) and at most one post-change local run (WP4 step 11, only if CI is unavailable).

---

## 8. Plan review round 1 — folded findings

Reviewer: code-reviewer lane (deleg_70d854ff, deepseek-v4-flash), 2026-08-25. Verdict:
APPROVE_WITH_CHANGES. All MUST/SHOULD findings are folded into the sections above; this section
records them for traceability.

MUST (all folded):
- M1 → WP4 steps 3–5 reordered: compile-RED witness via `dotnet build` (CS0246), package add,
  then gate-RED witness via the filter run, then the `IsAssignableFrom` fix. The original
  sequence's `--no-build` run against a stale assembly would exit 8 with zero matched tests.
- M2 → every `--diagnostics` replaced by `--xunit-diagnostics on` (xunit v3 4.0.0 MTP option;
  verified against `CommandLineOptionsProvider.cs` at tag v3-4.0.0; bare `diagnostics` is not
  registered). Decision A, WP4 step 10, WP4 quality gate, §4 matrix.
- M3 → probes use `[RetryFact(2)]`/`[RetryTheory(2)]` so the quality gate's `grep -c 'attempt (1/2)'`
  matches (default is 3 attempts).

SHOULD (all folded):
- S1 → WP0 rewritten: baselines already captured; only the discovery-parity row remains to run.
- S2 → surface sized correctly: 281 files / 1,634 lines (Integration is 252 files, not ~120+);
  acceptance grep uses the `\[RetryFact` prefix form (counts `[RetryFact(Skip=…)]`); expected ≈268
  files with attributes; the Performance=Benchmark exclusion is minus ONE (ParityGateTests.cs
  only — the two QueryGuard files are Speed=Fast and were never in the union).
- S3 → E2E classes carry Speed=Nightly, not Slow (E2E runs in build-nightly-gates); §4 matrix
  numbers corrected to the captured baselines (bdd 172/5/0, slow 968/4/0).
- S4 → WP1 step 6 is an explicit OutputType checkpoint (the guide's `OutputType=Exe`
  precondition does not hold here; fallback: set OutputType=Exe, re-run the checkpoint).
- S5 → `TESTINGPLATFORM_TELEMETRY_OPTOUT: 1` (not `true`) in the four job env blocks.

NITS (noted, no plan change): N1 PIPESTATUS → plain `$?` (zsh); N2 retry re-runs the method
inside the same test-case execution — class fixtures are NOT re-created per attempt, so a failed
attempt's side effects on shared fixture state persist into the retry (relevant for E2E
WebApplicationFactory fixtures; verify exact v3 per-test fixture disposal at implementation);
N3 swap-regex drift guard added to WP4 step 8 (current surface verified clean: all 1,634 matches
are real attribute lines); N4 build.yml comments still reference the deleted nightly.yml (repo
staleness, out of scope); N5 "~14 min" is the CI figure, local slow is 57 s (corrected in §4);
N6 the gate fix's Theory arm is dead for xRetry but harmless; N7 the E2E Skip fact swaps and
compiles (Skip inherited, discoverer passes SkipReason through — verified in xRetry source).

## Plan review round 2 — implementation findings folded (2026-08-25)

Found by the WP4 implementation lane (deleg_d62e1868) + orchestrator verification, all folded:

- M1 (--nologo): under MTP, `--nologo` is forwarded to the test executable as an unmatched token
  and dotnet test reports "Zero tests ran / exit 5" (dotnet/sdk#55309, reproduced locally).
  ALL commands in this plan drop `--nologo` (WP1 evidence runs, WP3 CI steps, §4 matrix).
- M2 (retry visibility under MTP): xRetry.v3 1.0.0 emits per-attempt DiagnosticMessages via the
  xunit MESSAGE BUS (`RetryTestCaseRunner.cs:103`); the MTP in-process runner drops
  message-bus DiagnosticMessages — only diagnostic-SINK messages reach output. Proven
  empirically: a TestContext.SendDiagnosticMessage probe fired under `--xunit-diagnostics on`
  while xRetry's "attempt (1/2)" lines appeared under NO flag combination (`--xunit-diagnostics on`,
  `--xunit-internal-diagnostics on`, `-v n`, MTP `--diagnostic` file). No upstream fix (1.0.0 is
  the tip). Amendment: retry visibility under MTP = the two permanent probe tests (counter==2)
  + exhaustion still RED with the last attempt's exception + per-attempt lines on the
  Rider/vstest path. `--xunit-diagnostics on` is NOT added to the lanes (zero value, output
  volume). Retries themselves verified working: exhaustion RED surfaced "attempt 2".
- S1 (fast-lane count): the fast lane is 3334/1/0 after WP4 = baseline 3330/1/0 + the 4 planned
  new fast-lane tests (2 probes + 2 gate-busting). §4 amended. (A transient 3332/3/0 reading was
  the dirty-tree AssemblyBuildStampTests skip — stamp tests skip when the build is dirty; CI
  always builds fresh, so this cannot occur there.)
- Verified during WP4: CS0246 RED (6 errors) → package add → green; vacuous-gate RED (both
  RetryAttributeClasses_RemainVisibleToTheGuard failed) → IsAssignableFrom fix → gate trio
  green; bdd 172/5/0 unchanged; MTP executable produced without OutputType=Exe; discovery parity
  4615 = 4615; CI (PR #589) build-fast/build-bdd/build-slow all green on the WP1-3 state.

