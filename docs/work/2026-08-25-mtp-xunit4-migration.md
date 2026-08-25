# Implementation record — MTP + xunit 4.0.0 + retry for slow/E2E/integration

Task: `migrate-tests-to-mcp` — branch `task/migrate-tests-to-mcp-xunit4`, base `c2e29ae2` (main),
2026-08-25. Plan: `docs/work/2026-08-25-mtp-xunit4-migration-plan.md`; research:
`docs/work/2026-08-25-mtp-xunit4-migration-research.md`. Guide followed:
`job-search-ai-assistant/docs/how-to/migrate-to-microsoft-testing-platform.md` (jsaa PR #911).

## What changed

- **Packages** (`tests/Directory.Packages.props`): `xunit.v3`, `xunit.v3.extensibility.core`,
  `xunit.runner.visualstudio` 3.2.2/3.2.2/3.1.5 → **4.0.0/4.0.0/4.0.0**; `coverlet.collector`
  dropped (no consumer); `Microsoft.NET.Test.Sdk` 18.8.1 kept for the Rider/vstest path;
  `xRetry.v3` **1.0.0** added. `Reqnroll.xunit.v3` 3.3.4 and `TngTech.ArchUnitNET.xUnitV3` 0.13.4
  kept pinned — both verified working under 4.0.0 (build + BDD run).
- **Runner** (`global.json`): `test.runner = Microsoft.Testing.Platform`. Root
  `Directory.Build.props`: `UseMicrosoftTestingPlatformRunner` for `*.Tests` projects. The
  SDK's MTP targets emitted the test executable — no `OutputType=Exe` needed.
- **CI** (`.github/workflows/build.yml`): four lanes use the `--project tests/AiRaccoon.Tests`
  form; `TESTINGPLATFORM_TELEMETRY_OPTOUT: 1` on all four jobs; `--nologo` dropped everywhere
  (VSTest-only flag; under MTP it produces "Zero tests ran / exit 5" — dotnet/sdk#55309).
- **Retry**: `xRetry.v3` 1.0.0 — `[RetryFact]`/`[RetryTheory]` (3 attempts default) on the whole
  slow/E2E/integration surface: **281 files / 1,634 attribute lines** (1,611 `[Fact` + 23
  `[Theory`), 266 files actually swapped, 15 no-op helpers. Excluded: `ParityGateTests.cs`
  (Performance=Benchmark) and Reqnroll BDD scenarios (out of scope; `xRetry.v3.Reqnroll` exists
  if BDD retry is ever wanted).
- **Gates updated** (three, not two — the swap's blast radius): `SpeedGateCoverageTests` and
  `CategoryGateCoverageTests` `HasFactOrTheory` now accept `FactAttribute`/`TheoryAttribute`
  derivatives (`IsAssignableFrom`); `EnvGateReaderRuleTests.IsTestClass` (a source-TEXT scanner)
  now also matches `[RetryFact`/`[RetryTheory` — its scan set fell 13 → 2 after the swap and was
  restored to 13.
- **xUnit1069 fix**: `CodeChunkerTests.cs` Timeout test references
  `TestContext.Current.CancellationToken` (new 4.0.0 analyzer, TreatWarningsAsErrors).
- **New permanent tests**: `Unit/TestInfra/RetryFactRetriesOnTransientFailureTests.cs` and
  `RetryTheoryRetriesDataRowTests.cs` (deterministic attempt counters prove retry semantics),
  plus `RetryAttributeClasses_RemainVisibleToTheGuard` in both reflection gates.

## Baseline → after (per lane)

| Lane | Before (c2e29ae2) | After (PR branch) |
|---|---|---|
| fast | 3330 passed / 1 skipped / 0 failed (3331) | **3334 / 1 / 0 (3335)** = baseline + 4 new fast-lane tests (2 retry probes + 2 gate-busting) |
| bdd | 172 / 5 / 0 (177) | **172 / 5 / 0 (177)** — identical (Reqnroll survived 4.0.0) |
| slow | 968 / 4 / 0 (972) | CI build-slow job (identical expected) |
| nightly-gates | 147 / 10 / 0 (157) | CI build-nightly-gates job (identical expected) |
| discovery | 4615 (WP1 state) | **4619** = 4615 + the 4 new fast-lane tests (2 probes + 2 gate-busting); WP1 measured the WP1-state count 4615 — identical to the pre-change 4615 |

## Witnessed REDs (prove-the-check-fails)

1. CS0246 ×6 — probes referencing `xRetry` before the package was added.
2. Vacuous-gate RED ×2 — `RetryAttributeClasses_RemainVisibleToTheGuard` failed in both
   reflection gates before the `IsAssignableFrom` fix.
3. EnvGateReaderRuleTests — scan set dropped 13 → 2 post-swap; the non-empty guard went red
   ("should be ≥ 8 but was 2") while the offender sweep passed vacuously; fixed, scan set
   restored to 13.
4. Retry exhaustion — throwaway `[RetryFact(2)]` always-throws method went RED with the last
   attempt's exception ("attempt 2") surfacing; throwaway deleted after the witness.

## Deviations / amendments (documented in the plan §8 round 2)

1. **`--nologo` dropped** from all `dotnet test` invocations — VSTest-only; MTP forwards it as an
   unmatched token and reports "Zero tests ran / exit 5" (dotnet/sdk#55309, reproduced).
2. **Retry-attempt visibility under MTP**: xRetry.v3 1.0.0 emits per-attempt DiagnosticMessages
   via the xunit message bus (`RetryTestCaseRunner.cs:103`); the MTP in-process runner drops
   message-bus diagnostics — only diagnostic-SINK messages reach output. Proven empirically
   (a `TestContext.SendDiagnosticMessage` probe fired under `--xunit-diagnostics on` while
   xRetry's "attempt (1/2)" lines appeared under no flag combination). No upstream fix (1.0.0 is
   the tip). Retry visibility under MTP = the two permanent probe tests + exhaustion still goes
   RED + per-attempt lines on the Rider/vstest path. `--xunit-diagnostics on` NOT added to the
   lanes (zero value, output volume).
3. **Fast-lane count +4** is the plan's own new tests (2 probes + 2 gate-busting), not drift.
4. **Review-round-3 hardening (folded)**: RetryTheory probe extended with a second always-passing
   row proving per-row isolation (a passing row is not re-run when another row retries); new
   `Unit/RetrySurfaceGateTests` closes the surface-drift gap (a reverted `[Fact]` in the surface
   would otherwise pass every gate — derive-or-delete); `scripts/nightly-triage.py` drops
   `--nologo` (orphaned script, but would break under MTP if revived). Fixture semantics verified:
   xRetry re-runs the failed METHOD inside the same test-case execution — class/collection
   fixtures are NOT re-created per attempt, so a failed attempt's side effects on fixture state
   persist into the retry; the E2E fixtures (McpServerE2ETests, ModelMigrationCrashRecoveryE2ETests)
   must stay failure-transparent for retry to help rather than compound (noted, no change —
   the E2E suite passed green in CI with retries active).

## Version

No product change → `VERSION` untouched (1.35.0); `VersionContractTests` derive-only and green.

## Commits (branch `task/migrate-tests-to-mcp-xunit4`)

- `69c67485` test(mtp): migrate tests to Microsoft Testing Platform + xunit 4.0.0 (WP1)
- `75da0b2e` ci(mtp): move the four test lanes to the --project form + telemetry opt-out (WP3)
- `d1fa56f4` test(retry): xRetry.v3 probes + gate IsAssignableFrom fix (WP4a)
- `4867d656` test(retry): swap [Fact/[Theory -> [RetryFact/[RetryTheory across the slow/E2E/integration surface (WP4)
- `04a83462` test(layering): recognize retry attributes in the EnvGateReaderRule test-class scan (WP4 step 9 fix)

PR: https://github.com/Arasz/ai-raccoon/pull/589
