# Lane report — test-suite QA

Campaign: project-scope review, base `1d1889d517baf840df0b839f547091bd7f46808b`.
Model: sonnet · persona: test-engineer · read-only. Lane verified the base SHA and a clean tree.

This is the lane with the **shortest findings list and the longest `Healthy` section**, and that is the
result: the suite is in materially better shape than the campaign brief's hypotheses assumed. The
lane treated the prior round's review (base `b4581717`, 52 commits back) as leads only and
re-verified everything at this commit; a large fraction had already been fixed.

---

### F1 — Five independent hand-pinned copies of the 26-tool list violate "derive the list, or delete it"; two already show live drift [MEASURED]
**Severity:** MEDIUM
**Evidence:**
- `tests/AiRaccoon.Tests/Unit/Mcp/ToolInventoryTests.cs:33,39` — method `ToolsNamespace_ExposesAll24SpecTools` asserts `tools.Count.ShouldBe(26)` plus 26 individual `ShouldContain` lines (name says 24, asserts 26).
- `tests/AiRaccoon.Tests/E2E/McpServerToolSurfaceE2ETests.cs:21,32-60,82` — class doc comment says "all 25 tools", the `ExpectedToolNames` array has 26 entries, the method is named `ToolsList_SurfacesAllTwentyFourTools`. **Three numbers in one file, all different, none matching the actual 26.**
- `tests/AiRaccoon.Tests/Integration/Setup/McpServerSetupHostTests.cs:242,247-248` — `toolNames.Count.ShouldBe(26)` plus individual `ShouldContain`s.
- `tests/AiRaccoon.Tests/E2E/McpServerLaunchArgsE2ETests.cs:69` — `tools.Count.ShouldBe(26)`.
- Actual count: `grep -c "McpServerTool(" src/AiRaccoon/Tools/*.cs` → **26** (numeric assertions are all correct today).
- `ToolInventoryTests.cs:124-149` (`PackagedReadme_ToolsHeading_MatchesActualToolCount`, `PackagedReadme_ToolsTable_ListsExactlyTheRegisteredTools`) already do this **correctly** — they derive from `ToolMethods()` at test time, no pin.

**Why it matters:** five places encode "the tool set" independently; two have already drifted in their
names and doc comments. The next tool addition needs four synchronised manual edits with no single
check catching a missed one.

**Fix:** replace the four pinned sites' literal counts/arrays with a derived comparison against
`ToolMethods()` (Core project) or the live `tools/list` response (E2E project), exactly as the two
README tests in the same file already do. Rename the two mis-named methods regardless.

---

### F2 — `CreateGenerator_UsesSettingsModelPath` never proves the custom model path was used [MEASURED]
**Severity:** LOW
**Evidence:** `tests/AiRaccoon.Tests/Integration/Embedding/EmbeddingServiceConfiguredPathTests.cs:20-29`
— copies the bundled ONNX model to a custom path, calls
`service.CreateGenerator(new EmbeddingSettings("local", custom, null, null))`, and asserts only
`generator.ShouldNotBeNull()`.

**Why it matters:** this is the prior round's QA-F9 and it is still open. `CreateGenerator` would pass
even if it silently ignored `custom` and loaded the bundled default — the exact defect class this
test's name claims to guard against. **No value would turn it red.**

**Fix:** assert on something that distinguishes the custom path from the default — delete or rename the
bundled model first and confirm generation still succeeds, or assert on the resolved model file
path/fingerprint the service actually loaded.

---

### F3 — Two real-port/real-process Integration tests fail only when three `dotnet test` invocations run concurrently on one machine; both are clean in isolation [MEASURED, then disproven as a suite defect]
**Severity:** LOW
**Evidence:** running `Speed=Fast`, `Category=bdd` and `Speed=Slow` simultaneously produced two
failures: `ToolRefusalsTests.KnownRefusal_ReturnsRefusal_WithoutAnSdkErrorLog(…ro…)`
(`tests/…/Integration/Mcp/ToolRefusalsTests.cs:218-229`, binds a real loopback HTTP server) and
`BackendLauncherTests.Acquire_WhenAServerIsAlreadyListening_DoesNotSpawn`. Rerun in isolation:
`--filter "FullyQualifiedName~ToolRefusalsTests"` → **34/34 passed**;
`--filter "FullyQualifiedName~BackendLauncherTests"` → **8/8 passed**.

**Why it matters:** **not a CI risk** — `build.yml:39-135` runs the three filters as separate GitHub
Actions jobs on separate runners, never concurrently on one machine. But it confirms that the tests
binding real loopback ports (`tests/AiRaccoon.Tests/TestHelpers/LoopbackPort.cs:65-116`) retry only on
`SocketError.AddressAlreadyInUse` (`:119-123`) — not on the general CPU-starvation timing sensitivity a
real HTTP/process round trip has under heavy concurrent load. Worth knowing before someone runs two
`dotnet test` invocations locally and reports a flake.

**Fix:** none required for CI. If local double-invocation becomes common, widen the retry class or note
the constraint in the E2E collection's doc comment.

---

## Healthy — checked and found genuinely rigorous

- **The CI partition is exact.** `dotnet test --filter "(Speed!=Fast)&(Category!=bdd)&(Speed!=Slow)" --list-tests` → **0 tests** [MEASURED]. Pairwise intersections (`Fast&bdd`, `Fast&Slow`, `bdd&Slow`) are also **0** [MEASURED]. The full-suite run reproduced the ground truth exactly: **Failed 0, Passed 2861, Skipped 9, Total 2870**. Per-filter real execution: **Fast 2142 + bdd 143 + Slow 585 = 2870** [MEASURED]. `--list-tests` undercounts by exactly 20, all in the Fast filter (2122 vs 2142) — a discovery-vs-execution gap for a data-driven `[Theory]`, not a coverage gap, since the boolean escapee/overlap queries use the same trait-selection mechanism as real filtering and are dispositive on their own.
- **The .NET default-interface-member dispatch trap does not occur anywhere in this suite.** `IMemoryStore` has two DIM members (`GetAsync`, `DeleteInScopeAsync`); `FakeMemoryStore.cs:27-34` declares `GetAsync` **virtual** with a comment explicitly citing the shadow-vs-override mechanism, and deliberately leaves `DeleteInScopeAsync` undeclared with a comment explaining why (its default forwards to the virtual, overridable `DeleteAsync`) — backed by a dedicated test, `FakeMemoryStoreTests.cs:33-39`. All 16 files deriving from `FakeMemoryStore` were checked: only one (`MemoryToolsTests.cs:752`) overrides `GetAsync`, correctly with `override`; none declares `DeleteInScopeAsync`. `IPromotionQueueStore` also carries two DIM members; its 5 test fakes all implement the interface directly, so the trap's precondition never arises.
- **`FakeEmbeddingEndpoint.VectorFor`** (`tests/AiRaccoon.Tests/TestHelpers/FakeEmbeddingEndpoint.cs:35-46`) derives its vector from `SHA256.HashData` of the actual input text — distinct per input, stable across calls. Swapping inputs between two tests using this fake **would** change their outcomes. This directly disconfirms the canonical content-ignoring-fake pattern for the fake wired into E2E/Integration tests.
- **The previously-flagged content-ignoring noise-filter fake is gone.** `ZeroShotEmbeddingFilter*`, `NoiseClustering*`, `NoiseFeedback*`, `PromotionScorerTtlPolicy*` no longer exist in `src/` or `tests/`. `NoiseFilteringServiceTests.cs` now has 4 tests including a genuine short-circuit test with a counting fake as the secondary observable (`:58-76`).
- **`WritePerformanceBenchmarkTests.cs`** now asserts a real functional gate (`interceptedCount.ShouldBe(iterations)`, `:83`) instead of discarding return values, and only writes its report file behind `AIRACCOON_BENCH_REPORT=1` (`:108`) instead of dirtying tracked `docs/` on every run.
- **`DeleteReplaceRollbackTests.cs`** (new) exercises the previously-untested `catch { ROLLBACK; throw; }` branch in `DeleteSourcePathAsync`/`ReplaceFileAsync` with a real SQLite `BEFORE` trigger forcing a mid-transaction failure, and checks multiple secondary observables (entry count, raw `value LIKE '%revised%'` count, watch fingerprint, search-still-finds-original) rather than one return value.
- **`WaitByPolling.cs`** (renamed from `WaitByPooling`) plus `WaitByPollingTests.cs`: `FirstTick` dropped from 10 s to 25 ms, deadline now returns `false` rather than throwing, and the tests distinguish fast-path / deadline / polling-density / cancellation with a `FakeTimeProvider` — a genuine "prove the check fails first" build.
- **Env-gated local-only probes correctly report as Skipped, not Passed.** `PlatformNumericsProbe.cs:70-73`, `PromotionScoringRealDataTests.cs:60-65`, `JsaaCorpusRegenerationTool.cs:38-42`, `GateQueryVectorRegenerationTool.cs:34-37` all use xUnit v3's `Assert.Skip(…)` (which throws internally), **not** a bare early `return;` that would read as Passed. Confirmed against the measured skip counts (4 non-BDD + 5 BDD = 9, matching ground truth exactly).
- **Self-guarding derived gates.** `CategoryGateCoverageTests`, `SpeedGateCoverageTests`, `BddGateCoverageTests` reflect over every test class for trait coverage, and each **also asserts its own reflection query still finds classes** (`TheGuardSeesTheTestClasses`: `Count.ShouldBeGreaterThan(100)`) — guarding against the exact "vacuous the day reflection stops matching" failure mode.
- **Ratchets carry genuine raise histories.** `git log -p` on `McpServerSetupHostTests.cs`'s `toolNames.Count.ShouldBe(N)` shows **7 clean increments** (19→20→22→23→24→25→26), one per commit, never silently re-pinned. `RrfParameterSweepTests.cs:150-171` documents every re-pinned threshold inline with an ADR reference and a measured value/date, and explicitly records where it **declined** to re-pin ("Not re-pinned to a wider window; recorded as a gap") rather than loosening reflexively.
- **`LikePattern`** (previously zero test references anywhere) now has 4 dedicated test files: `LikePatternTests.cs`, `LikePatternSqliteBehaviourTests.cs`, `LikePatternCascadeDeleteTests.cs`, `MemoryStorePathCascadeEscapeTests.cs`.

## Disconfirmed

- **"The suite is flaky."** Two failures appeared only under an artificial condition the lane created itself (three concurrent `dotnet test` invocations on one machine); both are 100% reproducible passes in isolation. See F3.
- **"`--list-tests` misses 20 tests, so the CI partition has a gap."** The 20-test gap between `--list-tests` (2850) and the real run (2870) is confined entirely to the Fast filter's discovery-time enumeration of one data-driven `[Theory]`; the direct boolean escapee query (0 tests match none of the three filters) and the arithmetic reconciliation (2142+143+585 = 2870, exactly ground truth) both settle that **no test escapes CI**.
- **The 2.43:1 test-to-production ratio is bloat.** No new evidence of padding in what was sampled — `RrfParameterSweepTests.cs`, `DeleteReplaceRollbackTests.cs`, `WaitByPollingTests.cs` are all dense, distinct-scenario files. A full duplicate-detection sweep was not run (see Still open).
- **The DIM dispatch trap on the rebased fakes.** The brief flagged this as worth checking because the suite recently rebased many fakes onto `FakeMemoryStore`; all 16 derived classes plus the `IPromotionQueueStore` fakes were checked and **the codebase already guards against it explicitly, correctly, with its own test.**

## Still open

- **`McpToolContractTests.cs`** carries a full tool-signature contract string — a sixth site referencing tool names. Whether it duplicates F1's drift risk or is a legitimately distinct (stronger, signature-level) contract test was not determined.
- **`tests/AiRaccoon.Tests/BDD/NativeMemorySteps.cs`** is now the largest test file at 1,853 lines (previously `SyncServiceTests.cs` at 1,463, which the prior round verified earns its size). 84 step attributes, 114 unique step patterns; feature files sum to 815 lines. Proportional to the overall ratio, but its step bodies were not diffed for duplication.
- **The prior round's QA-F6/F12** (one access-mode guarantee pinned at both BDD and Unit layers; four watch happy-paths pinned at both BDD and Integration) — explicitly deprioritised then, not re-checked now.
- **Whether the port-binding retry (F3) should widen its catch class** — an owner judgment call, not something one reproduction settles.

## Grade mix

MEASURED 11 (F1, F2, F3, the CI partition, ratchet history ×2, the skip mechanism, the tool-count grep,
line counts, isolation reruns ×2) · READ 6 · INFERRED 1 (the `--list-tests` Theory-expansion
explanation — plausible and arithmetic-consistent but not traced to the specific `[Theory]`) ·
UNVERIFIED 0.

## Owner questions

1. Should the four pinned tool-count/list sites (F1) be converted to derive from `ToolMethods()` / the live `tools/list` response, matching the pattern `ToolInventoryTests.cs` already applies to its README tests?
2. Is the stale "24"/"25" language in test method names and doc comments (F1) worth a follow-up naming pass now that the numeric pins are confirmed correct?
3. Should `CreateGenerator_UsesSettingsModelPath` (F2) be strengthened, or is it low enough priority to leave?
4. Is F3's narrow port-bind retry (only `AddressAlreadyInUse`) worth widening, given it is provably a non-issue for CI but a rough edge for local concurrent runs?
