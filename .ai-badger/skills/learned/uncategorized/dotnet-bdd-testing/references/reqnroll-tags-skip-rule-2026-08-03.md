# Reqnroll tag→skip/Category mechanics + 2026-08 ecosystem state

Research date: 2026-08-03. Method: NuGet APIs, GitHub REST, shallow sparse clone of
github.com/reqnroll/Reqnroll (main @ 33eeea2) and source grep. Grades: MEASURED = API/nuspec/
source read; INFERRED = combination not directly tested.

## Package/ecosystem state (MEASURED via api.nuget.org)
- Reqnroll 3.3.4 — published 2026-03-23; license **BSD-3-Clause** (not MIT). Cadence ~monthly:
  3.3.0 (2025-12-17), 3.3.1/2/3 (Jan-2026), 3.3.4 (2026-03-23). GitHub: 774 stars, 48 open issues,
  commits through 2026-07-26 (MEASURED api.github.com/repos/reqnroll/Reqnroll).
- Reqnroll.xunit.v3 3.3.4 — "Package to use Reqnroll with xUnit.v3 2.0 and later"; deps:
  xunit.v3.assert >= 2.0.0, xunit.v3.extensibility.core >= 2.0.0, Reqnroll 3.3.4,
  Reqnroll.Tools.MsBuild.Generation 3.3.4 (nuspec). First shipped at Reqnroll 3.1.0 (2025-09-26).
  xunit.v3 latest stable 3.2.2 (4.0.0-pre exists) — INFERRED compatible (floor 2.0.0, repo pins 3.2.2,
  transitive pinning off).
- Reqnroll core nuspec (3.3.4) deps: Gherkin 35.0.0, Cucumber.CucumberExpressions 17.1.0,
  Cucumber.Messages 30.1.0, System.Text.Json 8.0.5, etc. → modern Gherkin grammar incl. `Rule:`
  (Rule keyword exists since Gherkin 6).
- .NET 10: README "works on ... up to .NET 10.0"; v3.3.4 release notes: templates "support .NET 10,
  xUnit v3".
- SpecFlow: EOL **2024-12-31** (Tricentis announcement); GitHub repos deleted (SpecFlowOSS/SpecFlow →
  404); specflow.org redirects to Tricentis "ShiftSync" community page stating "While SpecFlow has
  been retired". Last stable 3.9.74; last 4.0 beta 2023-02-15. Sources: reqnroll.net/news/2025/01/
  specflow-end-of-life-has-been-announced/, seankilleen.com/2025/01/farewell-specflow.../ .
- Xunit.Gherkin.Quick 4.5.0 (2024-03-14, MIT) — csproj: netstandard1.5, gherkin **5.0.0**, xunit
  **2.3.1** → cannot parse Rule:, no xunit.v3. Repo (ttutisani/Xunit.Gherkin.Quick) 224 stars,
  32 open issues, commits 2026-06 ("AppVeyor .net 10 pending", version bump) but NO NuGet release.
- Ecosystem scan (azuresearch-usnc.nuget.org/query?q=gherkin xunit): Reqnroll.xunit.v3 (546k
  downloads) vs Xunit.Gherkin.Quick (1.17M legacy), GherkinSpec.XUnit 3.0.2, SecByte.Xunit.Gherkin
  1.1.0, Klinked.Gherkin 2.0.0, TickSpec.Xunit 2.0.5 — all tiny/legacy.

## Source-verified generation internals (MEASURED, Reqnroll main @ 2026-08-03)
- **@ignore → Skip at generation time**: `Reqnroll.Generator/UnitTestConverter/IgnoreDecorator.cs` —
  `IGNORE_TAG = "ignore"`, matched via `ITagFilterMatcher` (InvariantCultureIgnoreCase, "@" stripped)
  → `SetTestMethodIgnore` → generator emits `Skip="Ignored"` on Fact/Theory. Registered as
  ITestClassTagDecorator + ITestMethodTagDecorator; RemoveProcessedTags=true.
- **xunit.v3 provider**: `Plugins/Reqnroll.xUnit3.Generator.ReqnrollPlugin/XUnit3TestGeneratorProvider.cs`
  — FACT_ATTRIBUTE="Xunit.FactAttribute", SKIP_PROPERTY_NAME="Skip", IGNORED_REASON="Ignored",
  TRAIT_ATTRIBUTE="Xunit.TraitAttribute", CATEGORY_PROPERTY_NAME="Category". `SetTestMethodIgnore`
  adds Skip="Ignored" to the Fact/Theory attribute (no SkippableFact needed on v3).
- **All non-ignore tags → Category traits**: `UnitTestMethodGenerator.SetupTestMethod` →
  `ConcatTags(ruleTags, scenario.Tags, additionalTags)` → `DecorateTestMethod(..., out scenarioCategories)`
  → `SetTestMethodCategories` emits `[Trait("Category", tag)]`. Rule-level tags ARE merged in.
  Feature tags → class-level Category traits.
- **Runtime skip**: `XUnit3RuntimeProvider.TestIgnore` → `Xunit.Assert.Skip(message)`; SkipException →
  ScenarioExecutionStatus.Skipped.
- **MSBuild**: `Reqnroll.Tools.MsBuild.Generation/build/Reqnroll.Tools.MsBuild.Generation.props` —
  `ReqnrollUseIntermediateOutputPathForCodeBehind` (default false; comment: "will be changed to true
  in v4"), `ReqnrollEmbedFeatureFiles`, `ReqnrollWarnForObsoleteCodeBehindFiles`. Task:
  `GenerateFeatureFileCodeBehindTask` (metadata `CodeBehindFile`).
- **Step-less scenarios**: a `Scenario:` with zero steps parses fine; if you don't want a trivially
  passing/odd test, tag it @ignore rather than leaving it bare.

## Technique: sparse shallow clone to grep library internals
```bash
git clone --depth 1 --filter=blob:none --sparse https://github.com/<org>/<repo>.git /tmp/src
cd /tmp/src && git sparse-checkout set <dirs-to-grep>
grep -rn "<symbol>" <dirs>/
```
Verified generator internals faster and more reliably than docs. Pitfalls:
- Keep the command short — one long one-liner chaining several `sparse-checkout add` calls can trip
  the terminal's hardline parser block; recovery is `curl` raw.githubusercontent.com files instead.
- unauthenticated GitHub API: 60 req/hr — batch calls, check `rate_limit` before blaming 404s.

## ai-raccoon fit (READ repo / INFERRED wiring)
- Repo: net10.0, xunit.v3 3.2.2, Shouldly, CPM (tests/Directory.Packages.props), TreatWarningsAsErrors
  ON, 162 hand-written [Fact]/[Theory]; docs/features/*/*.feature (44 + 29 = 73 scenarios) use
  `Rule:` + `Background:` + `@FR-*`/`@AC-*` + `@deferred` (3 step-less scenarios); spec_holes.py
  already treats @deferred as the elicitation marker; .feature is the contract, spec.json the task
  manifest.
- Wiring: pin Reqnroll.xunit.v3 3.3.4 in tests/Directory.Packages.props; include the .feature files;
  `ReqnrollUseIntermediateOutputPathForCodeBehind=true`; one [Binding] class per feature domain
  driving IMemoryStore/test doubles; tag the 3 step-less scenarios `@deferred @ignore` → Skipped;
  Category filters from @FR-*/@AC-* tags work out of the box.
- Verdict recorded: adopt Reqnroll.xunit.v3 as an ADDITIVE acceptance layer; keep hand-written tests
  authoritative for units (avoid double-maintenance).
