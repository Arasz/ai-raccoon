# Research record — migrate-tests-to-mcp (MTP + xunit 4.0.0 + retry)

Task: update xunit packages to 4.0.0, follow the jsaa migration guide, add retry support for
all slow / e2e / integration tests. Date: 2026-08-25. Base: c2e29ae2 (main).

## The guide (jsaa repo) — found first, as instructed

- `docs/how-to/migrate-to-microsoft-testing-platform.md` (v2, 2026-08-19) in
  /Users/arasz/RiderProjects/job-search-ai-assistant — the complete, repeatable procedure for
  vstest -> MTP + xunit v3 4.0.0. Written while performing jsaa PR #911 (merge commit f6a1e225,
  "test: migrate .NET tests to Microsoft Testing Platform + xunit v3 4.0.0").
- Supporting: `docs/adr/0099-tests-run-on-microsoft-testing-platform.md`,
  `docs/work/plans/2026-08-19-mtp-xunit4-migration.md`.
- Guide essentials:
  1. Packages: xunit.v3 / xunit.v3.extensibility.core / xunit.runner.visualstudio -> 4.0.0;
     coverlet.collector -> remove (vstest-only, no consumer); Microsoft.NET.Test.Sdk kept for
     the Rider/vstest path. Keep framework adapters pinned until the build proves 4.0.0 compat
     (4.0.0 breaking changes are mostly extensibility APIs).
  2. global.json (new `test` section, no sdk pin): `{"test": {"runner": "Microsoft.Testing.Platform"}}`.
  3. Directory.Build.props: `<PropertyGroup Condition="$(MSBuildProjectName.EndsWith('.Tests'))">
     <UseMicrosoftTestingPlatformRunner>true</UseMicrosoftTestingPlatformRunner></PropertyGroup>`.
     IsTestProject is unavailable in Directory.Build.props (set later by the SDK).
  4. runsettings are silently IGNORED by MTP for xunit; env vars via testconfig.json
     `environmentVariables` (MTP >= 2.3.0), auto-renamed to <appname>.testconfig.json and
     OVERWRITTEN on every build (keep source in repo).
  5. `dotnet test --project tests/X.Tests` is the documented MTP-mode form (positional still
     works but --project is official). Filter syntax unchanged for xunit 4.0.0 MTP. Zero
     matched tests -> exit code 8 (needs --ignore-exit-code 8 where a filter can match nothing).
  6. Telemetry: opt out with TESTINGPLATFORM_TELEMETRY_OPTOUT=1 (xunit v3 >= 3.2.0 pulls
     Microsoft.Testing.Extensions.Telemetry).
  7. Rider still uses the vstest adapter path — that's why xunit.runner.visualstudio +
     Microsoft.NET.Test.Sdk stay referenced.

## Current state (ai-raccoon)

- Single test project `tests/AiRaccoon.Tests` (VSTest mode: IsTestProject=true, no OutputType=Exe).
- `tests/Directory.Packages.props` (central versions): xunit.v3 3.2.2, xunit.v3.extensibility.core
  3.2.2, xunit.runner.visualstudio 3.1.5, Microsoft.NET.Test.Sdk 18.8.1, coverlet.collector 10.0.1,
  Reqnroll.xunit.v3 3.3.4, Reqnroll.Tools.MsBuild.Generation 3.3.4, TngTech.ArchUnitNET.xUnitV3
  0.13.4, Shouldly 4.3.0, NSubstitute 6.2.0, Microsoft.AspNetCore.Mvc.Testing 10.0.11, etc.
  (xunit.v3.extensibility.core is NOT directly referenced in the csproj — likely transitive via
  ArchUnitNET/Reqnroll; the pin is inert with CentralPackageTransitivePinningEnabled=false but
  the guide bumps it anyway.)
- Root `Directory.Build.props`: Version from VERSION file, TreatWarningsAsErrors=true,
  LangVersion latest, Nullable enable, ImplicitUsings enable, EnableNETAnalyzers + AnalysisLevel
  latest, EnforceCodeStyleInBuild. -> ANY obsolete-API usage from the 4.0.0 bump breaks the build.
- `global.json`: `{"sdk": {"version": "10.0.400", "rollForward": "latestPatch"}}` — add the
  `test` section alongside; no new SDK pin.
- CI `.github/workflows/build.yml`: build-fast (`--filter "Speed=Fast&Performance!=Benchmark"`),
  build-bdd (`Category=bdd`), build-slow (`Speed=Slow&Performance!=Benchmark`), label-triggered
  build-nightly-gates (`Speed=Nightly&Performance!=Benchmark`). All use positional `dotnet test
  --filter ... --no-build --nologo -v m`. CODE_REGEX already covers .csproj / Directory props /
  build.yml -> this PR triggers all lanes.
- Traits (tests/AiRaccoon.Tests/TestCategories.cs): Category=Unit|Integration|E2E|Retrieval,
  Speed=Fast|Slow|Nightly, Performance=Benchmark (excluded from PR lanes).
- Trait gates: SpeedGateCoverageTests (every handwritten test class must carry a Speed trait;
  reflection matches attributes by FullName == "Xunit.FactAttribute"/"Xunit.TheoryAttribute" —
  a RetryFact-derived attribute would NOT match and must be handled), BddGateCoverageTests,
  CategoryGateCoverageTests (all three under Unit/).
- ~187 test files carry Slow/Nightly; E2E folder ~14 files (incl. E2ETestCollection,
  McpServerFactory, AiRaccoonProcess); Integration ~60 files; Nightly ~20 files.
- 5 collections use `[CollectionDefinition(Name, DisableParallelization = true)]`
  (CliOutputRouting, ConsoleCapture, Observability, QuietLogging, WatchIntegration). 4.0.0 marks
  assembly-level CollectionBehavior DisableTestParallelization obsolete — collection-level
  DisableParallelization status must be proven by the build (TreatWarningsAsErrors).
- No runsettings file, no testconfig.json, no infra opt-in env var (env usage in tests is
  EnvScope/EnvEncryptionKeyProvider test-helper scope) -> the jsaa testconfig.json use-case
  does not apply here.
- Reqnroll BDD: 5 feature files wired via ReqnrollFeatureFiles in the csproj; reqnroll.json.
- coverlet.collector: no consumer found (no --collect in CI; only a stale comment in build.yml).

## xunit 4.0.0 facts (source: xunit.net/releases/v3/4.0.0, released 2026-08-14)

- Drops Microsoft Testing Platform v1 support; MTP v2 packages updated to 2.3.3. Co-released
  xunit.runner.visualstudio 4.0.0 keeps vstest mode working (Rider path).
- Adds full test parallelization as OPT-IN ParallelMode.All; default remains collections
  (source: xunit.net/docs/running-tests-in-parallel — "in v2 and v3 prior to 4.0, only two
  parallel modes (none and collections); v3 4.0 adds 'all'"; default CollectionBehavior
  CollectionPerClass unchanged). -> E2E port-race tests unaffected (E2ETestCollection already
  serializes them).
- Breaking changes (mostly extensibility): XunitTest/XunitTestCaseRunner/XunitTestRunner ctors
  now require parallelMode, scheduler, methodFixtureMappings; XunitTest ctor requires label +
  can-run-in-parallel flag; test class/method orderers added (breaks existing ITestCaseOrderer
  impls); [assembly: CollectionBehavior] DisableTestParallelization/MaxParallelThreads/
  ParallelAlgorithm obsolete -> [assembly: Parallelization] Mode/MaxThreads/Algorithm; report
  switches renamed (--report-trx etc.); XunitFilters.ToXunit3Arguments needs a Version param.
- Sets IsTestingPlatformApplication=true (anticipating MTP v3).
- NO built-in retry (release notes contain no retry feature). Verified against the release
  notes page text directly.

## Retry — the ask and the options (xunit has NO built-in retry)

User: "add retry support for all slow / e2e / integration tests".

- xRetry.v3 — community package; repo (github.com/JoshKeegan/xRetry) HEAD shows commits
  "Bump versions for xRetry & Reqnroll to 1.0.0" and "Bump xunit.v3.extensibility.core and
  xunit.runner.visualstudio to 4.0.0" -> stable 1.0.0 with explicit xunit 4.0.0 support, plus
  xRetry.v3.Reqnroll (Reqnroll v3, @retry tag on scenarios) and xRetry.Reqnroll (v2).
  API: [RetryFact]/[RetryTheory] REPLACE [Fact]/[Theory] per test method; default 3 attempts;
  optional (maxAttempts, delayMs); SkipExceptions support. NuGet listing shows 1.0.0-rc3 — the
  stable 1.0.0 must be verified at implementation time (repo commits confirm it exists).
  Per-method attributes -> touching hundreds of test methods in ~150 files (big noisy diff).
- Custom class-level attribute (e.g. [Retry(maxAttempts)] on the class) — small diff (~1
  attribute type + 1 line per class), but must be implemented against 4.0.0's CHANGED
  extensibility APIs (v2 pattern was XunitTestCase.RunAsync override + DelayedMessageBus; v3
  equivalent touches XunitTestCaseRunner/XunitTestRunner contexts whose ctors changed in 4.0.0).
  TDD-able; keeps [Fact] on methods so SpeedGateCoverageTests stays green untouched.
- CI-level rerun (action wrapper around the lane) — zero test-code changes but reruns the whole
  slow lane (~14+ min) on any flake; coarse.
- jsaa precedent: NO test-level retry (their 6 "Retry" hits are product LLM-client retry tests);
  their only migration retry was fixture-level EmulatorStartupRetry for a Cosmos 503; their MoE
  explicitly rejected retry as a fix for nightly flakes ("masks, does not fix"). The user's ask
  here is a deliberate feature, so the design should make retries VISIBLE (attempts surfaced in
  output/logs) rather than silent.
- Gate impact: if per-method RetryFact/RetryTheory attributes are used, SpeedGateCoverageTests'
  HasFactOrTheory (exact FullName match on Xunit.FactAttribute/Xunit.TheoryAttribute) drops
  those classes from coverage — the gate itself must be updated to recognize the retry
  attributes. Class-level attributes avoid this.

## Compatibility risks to prove empirically (build + run)

1. Reqnroll.xunit.v3 3.3.4 vs xunit 4.0.0 — UNPROVEN (jsaa explicitly flagged this as a risk
   and did NOT migrate Reqnroll; ai-raccoon HAS Reqnroll BDD). Gate: build + Category=bdd run.
2. TngTech.ArchUnitNET.xUnitV3 0.13.4 vs xunit 4.0.0 — jsaa kept 0.13.3 and it proved
   compatible; likely fine; gate: build.
3. [CollectionDefinition(DisableParallelization = true)] obsolescence under 4.0.0 — build
   (TreatWarningsAsErrors) decides.
4. MTP + dotnet test with the existing filter syntax — guide says unchanged for 4.0.0 (verified
   in jsaa). Zero-tests exit 8 does not apply (filters always match).
5. Rider/vstest path still needs xunit.runner.visualstudio + Microsoft.NET.Test.Sdk.

## Addendum (2026-08-25, after the record was written — decisive checks)

- BASELINES CAPTURED on base c2e29ae2 (all green, no build): Fast 3330 passed / 1 skipped /
  0 failed (3331, 29s); BDD 172 passed / 5 skipped / 0 failed (177, 13s); Slow 968 passed /
  4 skipped / 0 failed (972, 57s); Nightly 147 passed / 10 skipped / 0 failed (157, 6m18s).
  These are the acceptance numbers for the verification matrix.
- xRetry.v3 1.0.0 STABLE + xRetry.v3.Reqnroll 1.0.0 both exist on NuGet (flatcontainer index).
- xRetry.v3 1.0.0 depends on xunit.v3.extensibility.core `[4.0.0, 5.0.0)` — built FOR 4.0.0.
- xRetry.v3.Reqnroll 1.0.0 depends on Reqnroll.xunit.v3 `[3.0.0, 4.0.0)` + xRetry.v3 `[1.0.0, 2.0.0)`
  -> fits this repo's Reqnroll.xunit.v3 3.3.4.
- Reqnroll.xunit.v3 3.3.4 depends on xunit.v3.extensibility.core `>= 2.0.0` (no upper bound) —
  NuGet will resolve 4.0.0; runtime compat is the empirical Category=bdd gate.
- TngTech.ArchUnitNET.xUnitV3 0.13.4 depends on xunit.v3.assert >= 1.1.0 (no extensibility.core
  dep listed); jsaa kept 0.13.3 against 4.0.0 and it worked.
- fsi probe of xunit 4.0.0 (xunit.v3.core): `Xunit.CollectionDefinitionAttribute.DisableParallelization`
  is NOT obsolete; only assembly-level `Xunit.CollectionBehaviorAttribute`
  DisableTestParallelization/MaxParallelThreads/ParallelAlgorithm are obsolete (all `*`).
  The 5 `[CollectionDefinition(Name, DisableParallelization = true)]` usages in this repo are
  SAFE under 4.0.0 + TreatWarningsAsErrors. `Xunit.v3.ParallelizationAttribute`
  (Mode/MaxThreads/Algorithm) exists, not obsolete.
- VersionContractTests (tests/AiRaccoon.Tests/Unit/Setup/VersionContractTests.cs) only checks
  the PRODUCT assembly version against VERSION -> a test-infra-only change needs NO version bump.
- v3 retry seam (xunit.net migration-extensibility doc): custom FactAttribute +
  IXunitTestCaseDiscoverer + IXunitTestCase with CreateTests() and PreInvoke/PostInvoke hooks;
  runner extensibility docs "still forthcoming" -> hand-rolling retry on 4.0.0 is real custom
  work; xRetry.v3 1.0.0 already did it (RetryFactAttribute -> discoverer -> RetryTestCase with
  BlockingMessageBus, per-attempt DiagnosticMessages for visibility).

## Baseline (acceptance numbers)

- Fast lane (Speed=Fast&Performance!=Benchmark): captured on base c2e29ae2
  (docs/work baseline file: /tmp/baseline-fast-c2e29ae2.txt).
- BDD lane (Category=bdd) and slow lane (Speed=Slow&Performance!=Benchmark): to be captured on
  base c2e29ae2 before the package bump (slow lane ~14 min -> CI or one local run as sole
  session). Acceptance: post-migration counts identical (pass/skip/fail per lane).

## Sources

- jsaa guide: /Users/arasz/RiderProjects/job-search-ai-assistant/docs/how-to/migrate-to-microsoft-testing-platform.md
- jsaa PR #911: f6a1e225 (stat + commit message)
- xunit 4.0.0 release notes: https://xunit.net/releases/v3/4.0.0 (fetched 2026-08-25)
- Parallel modes doc: https://xunit.net/docs/running-tests-in-parallel (fetched 2026-08-25)
- xRetry README: https://github.com/JoshKeegan/xRetry (fetched 2026-08-25)
- ai-raccoon: tests/Directory.Packages.props, Directory.Build.props, global.json,
  .github/workflows/build.yml, tests/AiRaccoon.Tests/TestCategories.cs,
  tests/AiRaccoon.Tests/Unit/SpeedGateCoverageTests.cs
