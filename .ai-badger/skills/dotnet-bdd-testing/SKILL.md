---
name: dotnet-bdd-testing
description: "Use when adding Gherkin .feature files / a BDD runner to a .NET project: Reqnroll is the only live option (SpecFlow is EOL — never recommend it), xunit.v3 + CPM integration, tags/@ignore/Skip and Rule: blocks, or un-ignoring dormant scenarios. Includes the build-time code-behind recipe, feature-file overlap policy, and verified package facts."
version: 1.0.0
author: hermes-curator
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [dotnet, bdd, gherkin, reqnroll, specflow, testing]
    related_skills: [dotnet-domain-modeling, dotnet-test-migrations]
---

# BDD / Gherkin testing in .NET

## Runner landscape (checked 2026-08)
- **SpecFlow is dead**: end-of-life 2024-12-31, GitHub repos deleted, Tricentis discontinued it.
  NuGet line stuck on stale betas; xUnit v2 only. Never recommend it for a new integration.
- **Reqnroll is the successor** (BSD-3-Clause, actively released). xUnit v3 support shipped in
  Reqnroll 3.1 (2025-09). Latest line (2026-08): Reqnroll / Reqnroll.xunit.v3 3.3.x.

## Verify before you integrate — always spike first
Never recommend a runner from docs alone. Build a throwaway spike in /tmp that mirrors the repo's
exact constraints, run it, and report the measured result:

1. Query the NuGet flat-container API for the latest versions:
   `curl -s https://api.nuget.org/v3-flatcontainer/<pkg>/index.json`
2. Read the nuspec for dependency floors and target frameworks:
   `curl -s https://api.nuget.org/v3-flatcontainer/<pkg>/<ver>/<pkg>.nuspec`
   (netstandard2.0 targets are consumable from net10.0; check the xunit.v3 assert/extensibility
   floors against the repo's pinned xunit.v3; the runner adapter needs xunit.runner.visualstudio >= 3.0.2)
3. `dotnet new xunit -f net10.0` in /tmp, rewrite the csproj to the repo's stack: pinned xunit.v3 /
   runner / Microsoft.NET.Test.Sdk versions, TreatWarningsAsErrors, EnforceCodeStyleInBuild, and
   central package management (Directory.Packages.props + version-less refs) if the repo uses CPM.
4. Add Reqnroll + the runner adapter, one small .feature (2-3 simplified scenarios), trivial step defs.
5. `dotnet test` — green is the go signal. Report the exact commands, result line, and machine.

## Integration facts (verified 2026-08-02: net10.0 + xunit.v3 3.2.2 + CPM, green 3/3)
- Packages: `Reqnroll` + `Reqnroll.xunit.v3` (the xUnit v3 adapter; use `Reqnroll.xUnit` only for v2).
- Code-behind is generated **at build time** by `Reqnroll.Tools.MsBuild.Generation` (ships inside the
  adapter) — no committed .feature.cs files. `.feature.cs` defaults to being written NEXT TO the
  feature file; set `<ReqnrollUseIntermediateOutputPathForCodeBehind>true</...>` (v3.3+, default
  false, planned true in v4) to emit into obj/ instead — keeps the repo tree clean and generated
  code out of analyzer scope. ndjson/other artifacts land in obj/ either way.
- Config: `reqnroll.json` with `"$schema": "https://schemas.reqnroll.net/reqnroll-config-latest.json"`.
- Generated tests are async Task-based; xunit.v3's default conservative parallel mode is recommended.
- No `Reqnroll.Shouldly` package exists — use Shouldly directly in step definitions (adapter packages
  exist only for FluentAssertions / Verify).

## Tags, skips and Rule: blocks (source-verified 2026-08-03, Reqnroll 3.3.4)
- `@ignore` is special-cased **at generation time**: the `IgnoreDecorator` matches tag `ignore`
  case-insensitively at Feature/Rule/Scenario level and emits `Skip="Ignored"` on the generated
  `[Fact]`/`[Theory]` — the test is reported Skipped and never runs. Runtime dynamic skip is also
  available (`IUnitTestRuntimeProvider.TestIgnore` → xunit.v3 `Assert.Skip`; `SkipException` maps to
  `ScenarioExecutionStatus.Skipped`).
- Every OTHER tag (Feature + Rule + Scenario) is emitted as `[Trait("Category", tag)]` on the
  generated tests, so `dotnet test --filter "Category=..."` works with zero config. Consequence: a
  custom tag like `@deferred` becomes a Category trait, NOT a skip — to map it to Skipped, add
  `@ignore` alongside it (or write a custom tag-decorator plugin).
- `Rule:` blocks are fully supported — Reqnroll 3.3.4 bundles Gherkin 35.0.0 (Rule arrived in Gherkin
  6); Rule-level tags are merged into each contained scenario's tag decoration; scenarios inside a
  Rule generate normally.
- Lightweight-alternative rejection (verified): Xunit.Gherkin.Quick 4.5.0 is legacy — netstandard1.5,
  gherkin 5.0.0 (pre-Rule grammar), xunit 2.3.1; repo has 2026 commits but no release. Cannot parse
  modern .feature files; never recommend it.

## Adoption timing
A .feature whose Background/steps need infrastructure that doesn't exist yet (e.g. "the MCP server is
running") needs a harness before it can execute. Integrating before the domain exists forces fake
harnesses and throwaway step definitions — the "no abstraction before a caller" invariant says defer;
the spike de-risks the later integration, so deferring is cheap, not risky.

## Implementing @ignore'd scenarios: un-ignoring dormant features (verified 2026-08-06)

Removing `@ignore` is when the real work starts: bindings written while a scenario was
ignored were NEVER executed, and bound-but-dormant steps are frequently subtly broken.
Three real traps hit in one session (agent-memory.feature, 13 tags → 2):

- **`[When]` does not bind "And when ..." steps.** Gherkin parses `And when I search ...`
  after a `Then` as a THEN-kind step; only `[Then]`/`[StepDefinition]` bindings match it.
  Add a parallel `[StepDefinition(@"when I search for ...")]` binding — lowercase "when"
  prefix so its regex cannot also match the When-kind step (`I search for ...`), which
  would create an ambiguity. Keep the `[When]` binding for the When-kind occurrence.
- **Wrong positional args in dormant SearchQuery calls.** `SearchQuery`'s 4th positional
  parameter is `WorkspaceId`, NOT `ContextLabel` (that is the 10th). A dormant binding that
  passed a context string as the 4th arg silently searched the wrong bucket — only the
  un-ignore run exposed it. Prefer named args (`Scope:`, `ContextLabel:`) in step bindings.
- **Dapper `new { entry.Hash }` can fail to bind `@hash`** with "Must add values for the
  following parameters: @hash" — always write the parameter name explicitly:
  `new { hash = entry.Hash }` (the working bindings in the same file used the explicit form).
- **Placeholder hash labels in feature text are scenario keys.** `keep=["h1"]` in a
  consolidation scenario means "resolve ScenarioContext['h1']", not the literal string:
  the Given captures the real hashes under the h1/h2 keys, the When resolves them. The
  "h1" row no longer exists after consolidation (promotion writes a fresh project row via
  add_content), so assert the kept CONTENT is searchable, not the old hash.

## Feature-file overlap policy: duplicate vs implement vs keep

When two feature files describe overlapping behavior (e.g. an older requirements file vs a
newer implementation file, both live in the same suite):

- **Delete genuine duplicates** — same operation + same assertions, live in the other file —
  with a one-line comment pointing at the live scenario (`# Tool listing is live in
  native-memory FR-NM-9 ...`). A subset-assertion duplicate (e.g. 11 of 17 tools) is still
  a duplicate; a weaker re-statement adds no coverage.
- **Implement what is unique** — verify against CODE, not text. `grep` the production tree
  for the service/type the scenario names (hook pipeline → `MemoryExtensionHost` existed;
  sweep → `SweepService`; sync → `SyncService` + a production `FakeCloudStore`), then bind
  steps to the real service, not to raw-SQL approximations.
- **Keep `@ignore` only for genuinely unbuilt features** — step-less open-question
  scenarios whose fallback shipped are the honest keepers (consistent with the sibling
  file's treatment of its own unbuilt Part-2 scenarios).

## Gotchas
- **Filtering generated tests (verified 2026-08-05, Reqnroll.xunit.v3):** the
  generated test NAMES are the scenario display texts, so `--filter
  "FullyQualifiedName~<StepsClass>"` (or `Name~<scenario>`) matches NOTHING and
  silently runs zero tests. Discover real names with `dotnet test --list-tests`
  (the listing shows bare scenario text), then filter with
  `--filter "DisplayName~<scenario text>"`. When adding a new scenario to an
  existing feature, the full suite is the reliable gate — the scenario-name
  filter is the fast path once you know the display name.
- Static state in step-definition classes leaks across scenarios — reset it in a `[BeforeScenario]`
  hook, or a "duplicate content is written once" scenario fails mysteriously on leftover state.
- TreatWarningsAsErrors + EnforceCodeStyleInBuild do NOT fail Reqnroll-generated code (verified) —
  but your own step-definition code must be warning-clean.
- If the host task mandates its own deliverable format (plain-markdown findings with
  [measured]/[read]/[inferred] tags), follow the task: the evidence-first-research HTML renderer only
  accepts its own template schema and will refuse other shapes.

## Files
- `references/reqnroll-verification-spike-2026-08-02.md` — the full spike recipe (read when verifying a Reqnroll spike; commands, csproj,
  Directory.Packages.props, feature, step-definition sketch, results, machine) that proved
  Reqnroll 3.3.4 works on a modern .NET stack (net10.0, xunit.v3, CPM).
- `references/reqnroll-tags-skip-rule-2026-08-03.md` — source-verified mechanics of tag→skip/Category (read when tag→skip mapping is in question)
  mapping, MSBuild generation knobs, package/ecosystem state with dates+licenses, SpecFlow EOL
  evidence, Xunit.Gherkin.Quick rejection, and the sparse-clone grep technique used to verify
  generator internals.
- `references/unignoring-dormant-scenarios-2026-08-06.md` — the implement/delete/keep decision (read when un-ignoring dormant scenarios)
  table for a 13-tag un-ignore pass, plus the code-verified behavior facts (workspace context
  storage, search-bump rating formula, FTS OR-fallback trigger, SweepService candidate rule,
  SyncService workspace stripping) the bindings were built on.
