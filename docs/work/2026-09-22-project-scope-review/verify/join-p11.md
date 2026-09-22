# Join review — `task/psr-w1-p11` (F6, F1/F50; plan P1.1 + addendum R16)

**Reviewer posture:** read-only in `/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/w1-p11`.
No tracked file modified (`git status --porcelain` empty at start and end; only gitignored `obj/`+`bin/` written by the build). No `~/.ai-raccoon` access; no `mcp_ai-raccoon_*` call. Every claim below is checked against the ruling, the finding's mechanism, and a run of the gate — not the lane's summary.

**Scope:** branch `task/psr-w1-p11`, 3 commits on `5bca1900`:

```
51ba4804 docs: add the memory-engine activation step to the first-run surfaces
533a2d18 feat(search): warn when the memory embedding engine is unconfigured
b058d3c0 fix(docs): remove committed merge marker from the agent contract
```

`git diff --stat 5bca1900..HEAD` → 27 files, +429/−83. Build of the touched surface:

```
$ dotnet build tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj -v:q
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

---

## 1. Claimed change vs ruling and mechanism

| # | Lane's claim | Verdict | Evidence |
|---|---|---|---|
| 1 | Memory-engine warning naming `ai-raccoon model embedding set local` | **CONFIRMED** | `SearchWarnings.cs:19-24` (`EngineNotConfiguredPrefix = "memory engine not configured"`, remedy = `EmbeddingEngineSetup.DefaultModelCommand`); `EmbeddingEngineSetup.cs:11` = `"ai-raccoon model embedding set local"`; emitted by `MemoryTools.cs:237-238` + `:282-291`. |
| 2 | Code warning reworded to "the code section is FTS5-only" | **CONFIRMED** | `CodeSearchWarnings.cs:15-17`. Old pins of the old string survive only in `docs/work/checklist/*.json` historical evidence records and one prose assertion message (`MemorySearchKindToolTests.cs:447`), not in a shipped surface. |
| 3 | First-run docs (README/tutorial/how-to/McpServerInstructions) incl. tutorial `sessionId` | **CONFIRMED** | `README.md:57-64` (inside `## Quick Start`, 49–79); `docs/tutorials/get-started-with-ai-raccoon.md:36-43` + `:116`; `docs/how-to/configure-embedding-engines.md:5-7`; `McpServerInstructions.cs:16-27`. Gate `FirstRunMemoryEngineTests` 6/6 green. |
| 4 | Merge marker deleted | **CONFIRMED** | `git grep -n -I -E "^(<<<<<<<\|=======$\|>>>>>>>)"` → exit 1, no matches; `docs/reference/agent-memory-server.md:212-214` now reads "…on its own on-demand cadence…" with the marker gone. |
| 5 | 31 `MemoryTools` constructions updated | **CORRECTED** | Actual = **32 sites**: 26 × `new MemoryTools(` + 6 target-typed `new(...)` helper factories (`SearchMetricsIsolationTests.cs:66`, `CanonicalProjectIdReachesStorageTests.cs:80,87`, `ProjectIdsConvergenceTests.cs:349`, `MemorySearchFusionSignalMetricsTests.cs:32`, `MemoryToolsInstrumentationTests.cs:196`). The plan's "26" was the literal-site count; the extra 6 are helper constructions. Immaterial — see §4. |
| 6 | 267 touched-surface tests green | **CORRECTED** | Not reproducible as an exact set. My union of the touched surface: **276 passed / 3 skipped (pre-existing `@ignore`) / 0 failed** across three filtered runs (§2.5). The substance (nothing in the touched surface is red) holds. |

### F6 mechanism, verified end to end

- The warning's predicate is `string.IsNullOrWhiteSpace(provider)` on `embedding.provider` (`SearchWarnings.cs:45-46`).
- That is the **same row and the same predicate the search itself uses to decide the vector leg is off**: `EntryEmbedder.EmbedQueryAsync` returns `QueryVector.Empty` exactly when `string.IsNullOrWhiteSpace(settings.Provider)` (`EntryEmbedder.cs:312-315`), which makes `parameters.IsVectorQueried(queryVector)` false (`SqliteMemoryStore.cs:189,706`).
- It is also the same predicate `doctor` uses for the memory "not configured" arm (`DoctorCommands.cs:165-167`; `CorpusEngineReport.cs:30-37`). The doc comment's claim "the same settings row doctor reads" is accurate.
- The remedy string is the one the finding named (`ai-raccoon model embedding set local`), not the retired `model set local` (F54's defect).

### F1 / R16 mechanism

- Real line deleted; repo-wide scan clean (command above).
- Gate implemented as `git grep -n -I -E "^(<<<<<<<\|=======$\|>>>>>>>)"` over tracked files (`MergeConflictMarkerTests.cs:22-41`), with the required planted-marker fixture asserting **all three arms** and that a clean file is absent (`:44-73`). `git grep` (working-tree, tracked-only) is an equivalent, slightly stronger form of the ruling's `git ls-files | grep`: it searches content, so a marker cannot hide behind a filename. The `>100 files` sanity count prevents an empty-repo false pass.

### F50 mechanism

- `docs/tutorials/get-started-with-ai-raccoon.md:116` now reads `memory_search` with `sessionId="<your agent's session id>"`.
- Gate `TutorialStep4_NamesTheRequiredSessionId` parses the `## Step 4` section (heading exists at `:111`) and asserts `sessionId` (`FirstRunMemoryEngineTests.cs:43-50`).

---

## 2. Gates run (my own runs, filters only, no `--nologo`)

All against `tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests.dll`, built from this working tree.

### 2.1 Memory-warning test (F6 red→green slot)

```
$ dotnet test --project tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --no-build \
  --filter "FullyQualifiedName~MemorySearchKindToolTests.Search_KindMemory_WithNoEngineConfigured_WarnsAndNamesTheRemedy"
Test run summary: Passed!
  total: 1  failed: 0  succeeded: 1  skipped: 0
```

Watched-red evidence for this slot (independently recovered from base): at `5bca1900` the same call was pinned to the opposite outcome —

```
$ git show 5bca1900:tests/AiRaccoon.Tests/Unit/Mcp/MemorySearchKindToolTests.cs | awk 'NR>=271 && NR<=280'
271:     [Fact]
272:     public async Task Search_KindMemory_HasNoEngineNotConfiguredWarning()
...
279:         envelope.Data!.Warning.ShouldBeNull();
```

The lane rewrote the pin to the inverse assertion instead of adding a test beside it — exactly what P1.1 required.

### 2.2 Positive control (bank WITH an engine → no memory warning)

```
$ dotnet test --project tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --no-build \
  --filter "FullyQualifiedName~MemorySearchKindToolTests.Search_KindMemory_WithAnEngineConfigured_HasNoMemoryEngineWarning"
Test run summary: Passed!
  total: 1  failed: 0  succeeded: 1  skipped: 0
```

The test seeds `embedding.provider=local` and asserts `Warning.ShouldBeNull()` (`MemorySearchKindToolTests.cs:298-305`). A "warn always" implementation fails it; the pair cannot both pass unless the warning is conditional. A second positive control with the **real** `SqliteSettingsStore` exists as the golden test (provider `openai` seeded, no warning in the golden envelope) and passed in §2.5.

### 2.3 Code-warning regression

```
$ dotnet test ... --filter "FullyQualifiedName~MemorySearchKindToolTests.Search_KindBoth_WithNoCodeEngineConfigured_DegradesToFtsOnlyWithWarning
|FullyQualifiedName~MemorySearchKindToolTests.Search_KindCode_ForwardsTheCodeService_OwnWarning
|FullyQualifiedName~MemorySearchKindToolTests.Search_KindBoth_WithNeitherEngineConfigured_CarriesBothWarnings"
Test run summary: Passed!
  total: 3  failed: 0  succeeded: 3  skipped: 0
```

The kind=both/neither test asserts **both** section-scoped prefixes appear (`:314-323`), so one note cannot silently replace the other. The real-service code warning is also covered by `MemorySearchCodeIntegrationTests.Search_KindCode_ReturnsRealFtsHits_WithTheEngineNotConfiguredWarning` (green in §2.5).

### 2.4 Merge-marker gate (incl. planted-marker fixture)

```
$ dotnet test ... --filter "FullyQualifiedName~MergeConflictMarkerTests"
Test run summary: Passed!
  total: 2  failed: 0  succeeded: 2  skipped: 0
```

Fixture asserts exit 0 and exactly the three planted marker lines, with `clean.md` absent; the repo-wide arm asserts exit 1 (no match) after a `git ls-files` sanity count >100. This is the "prove the check fails" control the ruling required.

### 2.5 Touched-surface union

```
$ dotnet test ... --filter "FullyQualifiedName~MemorySearchKindToolTests|...~SearchWarningsTests|...~FirstRunMemoryEngineTests
|...~MergeConflictMarkerTests|...~MemoryToolsTests|...~MemoryToolsAccessModeTests|...~MemorySearchEvidenceEnvelopeTests
|...~MemorySearchFusionSignalMetricsTests|...~MemoryToolsInstrumentationTests|...~GoldenMemorySearchResponseTests
|...~CodeReindexJobTests|...~MemorySearchCodeIntegrationTests|...~SearchMetricsIsolationTests
|...~CanonicalProjectIdReachesStorageTests|...~OrphanVerbatimRefusalTests|...~ProjectIdsConvergenceTests
|...~SingleProjectIdE2E|...~SearchSignalPreservationStageOneTests|...~DefaultCodeModelCommandTests"
Test run summary: Passed!  total: 210  failed: 0  succeeded: 210  skipped: 0

$ dotnet test ... --filter "FullyQualifiedName~SqliteCodeSearchServiceTests|FullyQualifiedName~SearchDispatcherTests"
Test run summary: Passed!  total: 26  failed: 0  succeeded: 26  skipped: 0

$ dotnet test ... --filter "FullyQualifiedName~NativeMemoryStoreAi_RaccoonMCPServerFeature"   # BDD, uses CodeCorpusFeatureContext
Test run summary: Passed!  total: 43  failed: 0  succeeded: 40  skipped: 3 (pre-existing @ignore)
```

Union: **276 passed, 3 pre-existing skips, 0 failed.** No red anywhere in the touched surface.

---

## 3. Attacks

### (a) Does the warning leak into `kind=code`, or into a bank that HAS an engine?

**No leak, by construction — and the `kind=code` half has no regression test.** `MemoryEngineWarningAsync` early-returns `null` for `SearchKind.Code` (`MemoryTools.cs:284-287`) and is the **only** producer of the memory note (`git grep -n "MemoryEngineWarning" src` → `SearchWarnings.cs` + `MemoryTools.cs` only). With an engine configured the predicate returns null (`SearchWarnings.cs:45-46`); the positive control proves it.

**Test-coverage gap (not a behaviour defect):** no test asserts *absence* of the memory prefix on `kind=code`. Mutation that would go undetected: delete the `if (kind == SearchKind.Code) return null;` guard — `Search_KindCode_ForwardsTheCodeService_OwnWarning` (`:259-268`) asserts only `ShouldContain(code prefix)`, and `Search_KindCode_ReturnsCodeSectionWithEmptyResults_AndNoMemoryLeak` never inspects `Warning`. Both still pass with the leak present. This is the only place the "positive control" discipline of the plan is not mirrored (there is a control for the memory leg, none for the code leg's isolation).

### (b) Can the merge-marker test actually see a planted marker?

**Yes — measured.** The fixture (`MergeConflictMarkerTests.cs:44-73`) creates a temp git repo, plants all three arms plus a clean file, `git add -A`, runs the *same* command string as the repo-wide test, and asserts exit 0, exactly 3 matching lines, each arm present, `clean.md` absent. The gate passed 2/2 (§2.4), so the fixture is live and the scan is not a constant-exit-1 command. The real repo arm independently returned exit 1 on my own `git grep` before the test ran.

### (c) Did the construction updates change a test's meaning?

**No — spot-checked the guard/length-warning neighbours.** The 32 sites are argument-list insertions only; the build is green and no assertion text changed except where intended:

- `MemoryToolsTests` ctor now seeds `_store.Settings[EmbeddingSettingsKeys.Provider] = "local"` (`:46`). The guard/length tests (`Search_WithAnOrdinaryQuery_HasNoWarning` `:571-576`, `Search_WithAQueryAtExactlyTheLengthThreshold_HasNoWarning` `:605-612`, shadow/disabled variants `:580-588`, `:616-625`, `:632-643`, `:730-743`) keep their exact assertions; they now isolate guard/length from the engine note. The fresh-bank "ordinary query" case is no longer asserted here — but it is now asserted *in the correct direction* by the new `kind=memory` warning test, so coverage moved, it did not disappear.
- `MemorySearchKindToolTests.Search_KindBoth_WithNoEngineConfigured_DegradesToFtsOnlyWithWarning` was renamed to `..._WithNoCodeEngineConfigured_...` and seeds `provider=local` to isolate the code leg (`:437-452`). Meaning preserved; the doc comment says why.
- `MemorySearchEvidenceEnvelopeTests` ctor seeds `provider=local` with an explicit comment that the suite asserts additive evidence fields, not the fresh-bank warning (`:36-38`). Meaning preserved.
- `SearchSignalPreservationStageOneTests`/`OrphanVerbatimRefusalTests`/`SingleProjectIdE2E` introduce one shared `settings` instance passed to both `QueryGuardService` and `MemoryTools` — a routing cleanup, not a semantic change.
- `MemoryToolsInstrumentationTests`, `SearchMetricsIsolationTests`, `MemorySearchFusionSignalMetricsTests` pass fresh `InMemorySettings()` (no provider) — their subjects (metrics/telemetry) contain no `Warning` assertions (checked by grep), so the extra warning string cannot weaken them.

### (d) Does the warning still fire when the settings read fails (rather than throwing)?

**It throws; there is no catch** (`MemoryTools.cs:289` is an unguarded `await settings.GetSettingAsync(...)`). Two mitigating facts, both verified:

1. The settings store was **already read earlier on the same call path**: `QueryGuardService.EvaluateAsync` does `GetSettingsByPrefixAsync("queryGuard.")` before dispatch (`QueryGuardService.cs:37-38`), and the memory search itself reads `embedding.provider` on the search connection (`EntryEmbedder.cs:312-315`). A settings-store failure was already fatal to `memory_search` before this change; the new read adds one more bank open (and one more `SqliteException`→`bank-busy` window) after the work is done, not a new failure *class*.
2. `doctor`'s "unreadable ≠ not configured" contract (`DoctorCommands.cs:153-172`) is deliberately not mirrored: the warning path has no unreadable arm, so a settings failure refuses rather than emits a false remedy — the safe direction.

This is a residual, not a refutation: the fix does not make the search resilient to a mid-call settings failure, and the plan did not ask it to.

---

## 4. The lane's two deviations

**Deviation 1 — no `MemoryWarning` field on `SearchResults`/`SearchDispatchResult`; composed in `MemoryTools`.**
**Acceptable.** The plan's file list named the field, but the requirement is the observable: a memory-leg warning on the response. `MemoryTools` is the only consumer of `SearchDispatcher` that emits a warning (`git grep -ln SearchDispatcher src` → `ISearchDispatcher`, `SearchDispatcher`, `SearchDispatchResult`, `CodeSearchResults`, `AppRegistrations`, `MemoryTools`), and there is no CLI `search` verb. Composing at the tool boundary keeps the Core result types settings-free and matches the existing guard/length composition that already lived there. The plan's field would have been one more way to thread the same string; it is not a partially-fixed finding — the warning fires on the only surface that can show it, for both `kind=memory` and `kind=both`.

**Deviation 2 — 31 (actual 32) constructions vs the plan's 26.**
**Acceptable.** The plan's 26 was the count of `new MemoryTools(` literal sites; 6 helper factories construct the same type target-typed (`=> new(...)`) and necessarily gained the settings argument too. Nothing was skipped — the build proves every site was updated (the parameter is not optional). The lane's count is off by one against mine; the number is bookkeeping, not a gap.

**Verdict: F6, F1 and F50 are fixed, not partially fixed.** The only genuine hole is test-side, not behaviour-side: the `kind=code` isolation guard is untested (attack (a)).

---

## Residual risk

1. **`kind=code` isolation is guarded but unasserted.** A future removal of `MemoryTools.cs:284-287` leaks the memory note into code-only responses with no red test. Cheapest close: one `Warning.ShouldNotContain(SearchWarnings.EngineNotConfiguredPrefix)` on the existing `Search_KindCode_ForwardsTheCodeService_OwnWarning`.
2. **One extra settings read per `kind=memory`/`kind=both` search.** `SqliteSettingsStore.GetSettingAsync` opens the bank per call (`SqliteSettingsStore.cs:9-16`); the warning read happens after the dispatch work, so a transient SQLITE_BUSY there converts a successful search into a `bank-busy` refusal instead of returning results. Small window, not new class of failure (the guard already reads the same store first), but it is hot-path cost the plan did not weigh.
3. **Warning is derived from a second read of `embedding.provider`, not from the leg that actually degraded.** Today the predicates are identical (`EntryEmbedder.cs:312-315`), so it is correct; if the vector leg ever degrades for a reason other than "provider unset", the warning will not track it. The warning's contract is "unconfigured", which is exactly F6's scope.
4. **F50's gate is substring-weak.** `TutorialStep4_NamesTheRequiredSessionId` asserts the section contains `sessionId`; a mutation moving the token into prose would pass. It satisfies the plan's literal gate wording, but it does not prove the call parses (unlike, say, executing the example).
5. **Historical checklist JSONs still contain the old code-warning string** (`docs/work/checklist/2026-08-22-*.json` etc.). Correct for historical evidence records; they are not current surfaces. Flagging only so a future doc sweep does not mistake them for live drift.

## Still open

- **F47's label drift remains** in the how-to despite the new banner: `docs/how-to/configure-embedding-engines.md:17` (`Local ONNX Engine (Default)`) and `:35` (`**Local (Default)**`) still call the unactivated engine "the Default". F47 was not in this package's scope and the lane did not claim it, but the F6 family's first-run misread is only half-corrected until those two labels change.
- **F6's live-install re-measurement is not in this review.** The gates here are unit/integration against stubs and scratch banks; I did not drive a fresh installed tool (no bank access, no MCP calls per the brief). The `ai-raccoon-manual-checklist` pass remains the place to confirm the warning on a real first-run bank.
- **The 267 figure** the lane reported is not reproducible as a set; my union (276 pass / 3 pre-existing skip / 0 fail) is the number I can stand behind. If the lane's set is needed for a record, it should name its filter.
