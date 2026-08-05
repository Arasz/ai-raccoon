# Test Coverage Plan — Watch List Format Refactor (labeled output, descriptions, `watch registered`, `watch remove`)

> **Based on:** `docs/plans/watch-list-format-refactor.md` (the architect's implementation plan, WP1–WP5,
> Spectre.Console rejected, §6). This is the test-engineer (QA) companion plan: the complete test inventory
> after the refactor, the per-work-package hot-point gap analysis, the RED→GREEN sequence, and the acceptance
> gates. It does not restate the architect's production design except where the test surface constrains it.
> **Date:** 2026-08-05 · **Branch:** `task/watch-list-format` · **Scope:** plan only — no production or test
> code is changed by this document.
> **Workflow:** TDD mandatory; new types use the repo's compile-break-as-RED convention. xunit.v3 + Shouldly,
> `[Trait(TestCategories.Category, TestCategories.Unit)]` + `[Trait(TestCategories.Speed, TestCategories.Fast)]`
> (BDD/Integration where applicable). Byte pins follow the repo convention `stdout.Trim().ShouldBe(...)`
> (see `WatchScopeList_PrintsOnePathPerLine`) — the raw string `ShouldBe` the architect sketches would fail
> on the trailing `\n` that `WriteLineAsync` emits (§2 WP2, correction 1).

---

## 0. Current worktree state (verified at plan time)

| Item | State (verified by read/grep in this worktree) | Notes |
|------|-------|-------|
| `ConfigCommandsWatchTests.cs` | **18 tests** (15 non-`WatchList_*` + 3 `WatchList_*`) | Old one-line format pinned in **exactly 5 assertions inside the 3 `WatchList_*` tests** (L249–250, L269–270, L279). Grep `enabled=true concurrency|scope=\[` across `tests/` hits **only this file** — no BDD/E2E/other-unit pin exists. |
| `CliArgsTests.cs` | **56 tests** | Watch parse tests L318–384 incl. `Parse_WatchList_ParsesCommandPath` (L384). Grep of all five changed description strings across `tests/`: **0 exact matches** (the 2 case-insensitive hits are BDD step bindings, not pins). Description edits are test-neutral — architect §2.8 **confirmed**. |
| `CliOutputRoutingTests.cs` | **7 tests** | Pins routing contract + `"Usage"` + SCL parse-error template; no description pins. Stays unmodified. |
| `WatchConfigTests.cs` | **16 test methods** | **Drift note: the architect's "14 unit tests" (§2.2) is stale** — main moved past the checkout with PRs #19/#20/#21; the file gained `Keys_ExactStrings`, `SerializeScope_*`, `ParseScope_*` tests. No action: all 16 stay unmodified. |
| `ToolInventoryTests` (4), `WatchToolsInventoryTests` (3), `MemoryPromptsTests` (5) | Name-only pins | `WatchToolsInventoryTests` L33 pins `memory_watch_add` **name**; `MemoryPromptsTests` L48 pins prompt fragments (`memory_watch_add`, `memory_watch_status` — prompt unchanged, §2.9). `memory_watch_add` **description** (WatchTools.cs L35–36) unpinned. |
| Warning string (reviewer finding #1, PR #19) | `ConfigCommands.cs` **L496**: `'ai-raccoon watch scope add * <path>'` — teaches the **unquoted** form | Pinned only by substrings: unit `WatchEnableStar_WithEmptyScopeAllowlist_PrintsAddScopeMessage` (`err.ShouldContain("scope")`, weak) and BDD `ThenCommandReturnsAddScopeMessage` (`"add at least one scope"`). **Both survive a quote fix** — the fix is test-neutral, but the unit pin is strengthened (§2 WP3, new item 3). |
| `RunAsync` trailing-param change (R7) | Signature L23–25 already has trailing optional `bank`/`bws`/`env` | All call sites verified: `ConfigCommandsWatchTests` L28 (7 args), `ConfigCommandsEncryptionTests` L57 (6), `ConfigCommandsAccessModelTests` L25 (6), `ConfigCommandsRetrievalSweepSyncTests` L24 (6), `FileWatcherSteps` L88 (6), `EncryptionBitwardenFeatureContext` L190 (9, positional incl. bank/bws/env), `McpServerE2ETests` L261 (7), `Program.cs` L27 (named `bank:`, `bws:`). **Adding trailing `IWatchStore? watchStore = null` compiles every call site unchanged — R7 verified.** |
| BDD surface | `file-watcher.feature` CLI-rule scenarios L44–76 (6 scenarios) | They assert settings effects + the add-a-scope warning, **never** the list format — none break. `RunCliAsync` (FileWatcherSteps L75–96) passes 6 positional args; a named `watchStore:` arg is **additive** (R8 verified). `Ctx.WatchStore` is a **real `WatchStore` over the real SQLite bank** (FileWatcherFeatureContext L186) — the WP3 scenario exercises the real store. `Given a watch for … on path …` helper exists (L403) and registers via the real `WatchService.AddAsync`, which writes `lastChangeTs: 0` (WatchService.cs L32–34) → the scenario can assert `lastChange: never`. |
| E2E | `McpServerE2ETests` / `McpServerLaunchArgsE2ETests` | Grep `watch` in `tests/.../E2E/`: **0 matches** — no E2E runs watch verbs; nothing to break. |
| `FakeWatchStore` | `WatchTestFakes.cs` L73–107, internal, same assembly, `Watches` dict seedable | `ListWatchesAsync` enumerates the dict — insertion order in practice, unspecified. **Sort tests must seed deliberately unsorted** (§2 WP3, correction 1). |
| Domain types | `WatchConfig(bool Enabled, IReadOnlyList<string> Scope, int Concurrency)` record (WatchConfig.cs L10); `WatchPath.PathComparer` **OS-dependent** (Ordinal on Unix / OrdinalIgnoreCase on Windows, L16–17); `WatchRegistration(ProjectId, Path, CreatedAt, LastChangeTs)` (WatchStore.cs L7) | Formatter tests construct `WatchConfig` directly. Sort-test paths chosen lowercase (`/a`, `/b`, `/z`) so ordering is identical under both comparers. `1_700_000_000` → `2023-11-14T22:13:20Z`, `2_000_000_000` → `2033-05-18T03:33:20Z` (verified). |
| Docs | Old format appears **only in the architect's plan itself** (grep of `docs/`) | README / `agent-memory-server.md` pin no list format; WP5 writes fresh text — no doc drift to repair. |
| Deliverable | `docs/plans/watch-list-format-refactor-test-plan.md` does not exist | Created by this plan. |

---

## 1. Test inventory after the refactor

| File | Before | After | Action |
|------|--------|-------|--------|
| `tests/AiRaccoon.Tests/Unit/Watch/WatchListFormatTests.cs` | — | **3** | **NEW (WP1)** — byte-for-byte `ShouldBe` pins. |
| `tests/AiRaccoon.Tests/Unit/Setup/ConfigCommandsWatchTests.cs` | 18 | **27** | 15 stay unmodified; 3 `WatchList_*` **updated** (WP2); **+2** `WatchList_*` (WP2); **+4** `WatchRegistered_*` (WP3); **+3** `WatchRemove_*` (WP4); 1 assertion **strengthened** (WP3, warning quote). `Run` helper gains optional `FakeWatchStore? watchStore = null` (WP3 only). |
| `tests/AiRaccoon.Tests/Unit/Setup/CliArgsTests.cs` | 56 | **59** | 56 stay unmodified; **+2** parse tests (WP3); **+1** (WP4). |
| `docs/features/file-watcher/file-watcher.feature` | — | **+1 scenario** (WP3) | In the "Watch configuration is CLI-only and user-facing" rule. |
| `tests/AiRaccoon.Tests/BDD/FileWatcherSteps.cs` | — | **+2 step bindings** + `RunCliAsync` wiring (WP3) | `[When("^the user runs watch registered$")]`, `[Then("^the CLI output lists the registered watch for \"([^\"]*)\" at \"([^\"]*)\"$")]`. |
| `CliOutputRoutingTests` (7), `WatchConfigTests` (16), `ToolInventoryTests` (4), `WatchToolsInventoryTests` (3), `MemoryPromptsTests` (5), `Unit/Watch/*`, `Unit/Setup/*` (non-watch), `Unit/Mcp/*`, BDD/E2E/Integration | — | **unchanged** | Verified unpinned against every changed string (§0). |

New tests: **16** (3 WP1 + 2 WP2 + 6 WP3 unit + 1 WP3 BDD scenario + 4 WP4). Updated: **3** (+1 strengthened assertion). Everything else byte-stable.

---

## 2. Gap analysis vs the architect's hot points (§4) and risks (§10)

### WP1 — formatter (hot point b)

| Branch | Detector | Verdict |
|--------|----------|---------|
| Empty scope → `(none)` inline | `Render_EmptyScope_RendersNoneInline` | Covered — also pins `enabled: false` and default `concurrency: 4`. |
| Multi-path indent (two-space, one per line, stored order) | `Render_ScopePaths_OneIndentedLinePerPath` | Covered — also pins `enabled: true`, non-default `concurrency: 8`. |
| Canonical `true`/`false`/int rendering | `Render_EnabledAndConcurrency_RenderCanonicalValues` | Covered. |
| Target ordering | **Not the formatter's job** — single-target signature; ordering pinned at CLI level | Correctly placed in WP2 (`WatchList_Ordering_IsOrdinalByTargetName`). |
| Weird target names (`CLAUDE.md`) | CLI ordering test seeds `CLAUDE.md` | Covered at CLI level. |

**WP1 verdict: sufficient as designed. No missing tests.**

### WP2 — wire `WatchListAsync` (hot points a, c, d; risks R2, R4)

| Hot point | Detector | Verdict |
|-----------|----------|---------|
| (a) `WatchConfig.Resolve` consumed by the CLI | All 5 `WatchList_*` tests now render *through* `Resolve`: project-beats-global (`WatchList_ProjectRow_WinsOverGlobal`), global fallback (`WatchList_ShowsResolvedValues_PerTarget`), defaults (`WatchList_NoRows_PrintsOnlyGlobalDefaults`), global-scope fallback + pollution visibility (`WatchList_OnlyEnabledRow_ShowsResolvedGlobalScope`), seeded-ordinal ordering (`WatchList_Ordering_IsOrdinalByTargetName`) | Sufficient. R2's canonicalization deltas are visible in the pins (`enabled: true`, not stored case). |
| (c) ordering pinned | `WatchList_Ordering_IsOrdinalByTargetName` (acme, CLAUDE.md, global → CLAUDE.md first) | Sufficient (R4). |
| (d) stdout discipline | Existing routing tests + the full-output pins assert stdout only | Sufficient. |
| **Correction 1 (QA):** full-output byte pins must use `stdout.Trim().ShouldBe(expected)` | The architect's raw `ShouldBe` on `stdout.ToString()` fails on the trailing `\n` from `WriteLineAsync` — GREEN would never go green | All three updated + two new full-output pins use `Trim()` (repo convention). |
| **Correction 2 (QA):** `WatchList_ShowsResolvedValues_PerTarget` as full multi-target pin | Expected: `"target: acme  enabled: true  concurrency: 2  scope:\n  /a\ntarget: global  enabled: true  concurrency: 4  scope: (none)"` | Also pins the no-blank-line-between-targets rule. |

**WP2 verdict: architect's 5 tests suffice; both corrections are mechanical (pin shape), no new test names.**

### WP3 — descriptions + `watch registered` (hot points d, e, f, g; risks R3, R6, R7, R8)

| Hot point | Detector | Verdict |
|-----------|----------|---------|
| (e) persisted-only fields, no fabricated live state | `WatchRegistered_ListsAllRegistrations_SortedByProjectThenPath` (full-output pin: `project:`/`path:`/`registered:`/`lastChange:` only), `WatchRegistered_ReadsOnlyTheWatchesTable` (settings rows present, no registrations → `no registered watches` — proves no settings fallback) | Sufficient (R6). |
| Sorting (projectId ordinal, path comparer) | Same full-output test — **Correction 1 (QA): seed the fake deliberately unsorted** (zeta first, then acme `/b`, then acme `/a`). The architect's seed order (acme/a, acme/b, zeta/z) is already sorted, so a handler that skipped the sort would still pass via dict enumeration order. Lowercase paths keep the pin OS-independent (PathComparer is Ordinal on Unix, OrdinalIgnoreCase on Windows). | Must-fix before writing. |
| `0` → `never` mapping | acme `/b` row (`lastChangeTs: 0` → `never`) + acme `/a` (`2_000_000_000` → `2033-05-18T03:33:20Z`) inside the same full-output pin; ISO format pinned byte-exact | Covered. |
| Optional filter with matches | `WatchRegistered_ProjectFilter_LimitsToProject` (`["watch","registered","acme"]` → only the two acme lines) | Covered. |
| Optional filter with **no matches** | **Correction 2 (QA): fold a second run into `WatchRegistered_ProjectFilter_LimitsToProject`** — filter `"nope"` → `no registered watches`, exit 0. Same empty-output path as no-rows; does not earn a separate test name. | Must-add (architect missed this branch). |
| Empty store | `WatchRegistered_NoRows_PrintsNoRegisteredWatches` | Covered. |
| Stdout discipline + exit codes (hot point d) | **Correction 3 (QA):** each of the 4 handler tests asserts `exit.ShouldBe(0)` and `err.ShouldBeEmpty()` — the architect's sketch asserts output only. The `Run` helper already asserts parse-clean; exit/stderr are per-test. | Must-add (cheap, per-test). |
| BDD scenario exercises the **real** WatchStore? | **Verified yes:** `Ctx.WatchStore` is a real `WatchStore` over the real SQLite bank (FileWatcherFeatureContext L186); `Given a watch for "proj-a" on path "/repo"` registers via real `WatchService.AddAsync` (lastChangeTs 0 → `lastChange: never`); the new `Then` asserts `_lastCliMessage` contains `project: proj-a  path: {Map("/repo")}  registered: ` and ` lastChange: never`. Exact ISO timestamp stays unit-pinned; BDD keeps contains-assertions. | Feasible as designed; bindings must Map the path (the store holds the normalized absolute path, not `/repo`). |
| (g) injection seam | Trailing optional param — **R7 verified** (§0, all 8 call sites compile unchanged). `Guard.IsNotNull(watchStore)` is **deliberately not unit-tested**: with no guard an NRE would produce the same exit-1/stderr shape through the `RunAsync` catch, so a test would pin an exception message, not behavior. The BDD real-store test + `Program.cs` wiring are the real guards. | No test. |
| (f) descriptions | Test-neutral — verified unpinned (§0). Parse tests pin command paths only: `Parse_WatchRegistered_ParsesCommandPath`, `Parse_WatchRegistered_AcceptsOptionalProjectFilter` | Sufficient (R3: per-verb parse tests don't snapshot the full tree — verified). |
| **New item 3 (QA):** reviewer finding #1 (PR #19) — L496 warning teaches unquoted `watch scope add * <path>` | Fix in WP3: `'ai-raccoon watch scope add '*' <path>'`. Drift detector: **strengthen** `WatchEnableStar_WithEmptyScopeAllowlist_PrintsAddScopeMessage` — `err.ShouldContain("scope")` → `err.ShouldContain("watch scope add '*'")`. The BDD `ThenCommandReturnsAddScopeMessage` pins `"add at least one scope"` — survives the fix unmodified. This is the one "stays unmodified" test the architect's inventory misses; it is the only test anywhere that touches the warning. | Must-do. |

**WP3 verdict: architect's 4 + 2 + 1 suffice with 3 corrections (unsorted seed, filter-no-match fold-in, exit/stderr asserts) + 1 new item (warning quote fix + strengthened pin).**

### WP4 — `watch remove` (risk R5)

| Branch | Detector | Verdict |
|--------|----------|---------|
| Per-target delete, other targets untouched | `WatchRemove_DeletesEnabledScopeAndConcurrencyRows_ForTarget` | Covered (asserts both the acme rows gone *and* the second target's rows untouched). |
| Star → global rows | `WatchRemove_Star_DeletesGlobalRows` | Covered. |
| No rows → no-op, exit 0, stdout message | `WatchRemove_NoRows_IsExitZeroNoOp` (exit 0, stdout contains `removed`, store unchanged) | Covered. |
| Parse | `Parse_WatchRemove_ParsesTarget` | Covered. |
| Partial-family rows (only one of the three keys present) | **Deliberately not tested** — `DeleteSettingAsync` on a missing key is store behavior already pinned elsewhere (`WatchScopeRemove_LastPath_DeletesTheRow`); a per-family test would re-pin the store. | No test. |

**WP4 verdict: architect's 3 + 1 suffice; add `exit.ShouldBe(0)` inside each of the three handler tests (the architect's sketch asserts it only in the no-rows test).**

---

## 3. Per-WP RED→GREEN sequence

Repo convention: gates are `dotnet build` (0 errors, 0 warnings) + `dotnet test` from the worktree root; targeted `--filter` per WP. Build green at every step.

### WP1 — pure formatter
- **RED (compile-break-as-RED):** new `tests/AiRaccoon.Tests/Unit/Watch/WatchListFormatTests.cs` — 3 tests reference the not-yet-existing `WatchListFormat` → file does not compile.
- **GREEN:** new `src/AiRaccoon.Core/Watch/WatchListFormat.cs` — `public static string Render(string target, WatchConfig config)`; empty scope → `(none)` inline, else header + `string.Join('\n', scope.Select(p => "  " + p))`.
- **Gate:** `dotnet test --filter "FullyQualifiedName~WatchListFormatTests"`.

### WP2 — wire `WatchListAsync`
- **RED (all 5 fail against current code):** the 3 updated `WatchList_*` tests fail on the old `target: enabled=… scope=[…]` format (2 `ShouldContain` → full `Trim().ShouldBe` pins, 1 `Trim().ShouldBe` new text); `WatchList_Ordering_IsOrdinalByTargetName` and `WatchList_OnlyEnabledRow_ShowsResolvedGlobalScope` fail on format (the old code already sorts ordinally — the *format* is what makes them red).
- **GREEN:** rewrite `WatchListAsync` (ConfigCommands.cs L567–597) — keep prefix fetch + seeded `SortedSet`; replace the three inline `??` chains with `WatchConfig.Resolve(target, key => rows.GetValueOrDefault(key))` + `WatchListFormat.Render`; the one-line `Resolve`-identity comment. Nothing else changes.
- **Gate:** `dotnet test --filter "FullyQualifiedName~ConfigCommandsWatchTests|FullyQualifiedName~WatchListFormatTests"`.

### WP3 — descriptions + `watch registered` (+ warning quote fix)
- **RED (6 unit + 1 BDD):**
  - `Parse_WatchRegistered_ParsesCommandPath`, `Parse_WatchRegistered_AcceptsOptionalProjectFilter` → parse errors (no `registered` command).
  - `WatchRegistered_ListsAllRegistrations_SortedByProjectThenPath` (**unsorted seed**), `WatchRegistered_ProjectFilter_LimitsToProject` (**+ second no-match run**), `WatchRegistered_NoRows_PrintsNoRegisteredWatches`, `WatchRegistered_ReadsOnlyTheWatchesTable` → `Run` helper's `parsed.Errors.ShouldBeEmpty()` throws first (no command), then dispatch throws.
  - BDD scenario in the CLI-only rule → Reqnroll fails on missing step bindings.
  - Strengthened `WatchEnableStar_WithEmptyScopeAllowlist_PrintsAddScopeMessage` (`err.ShouldContain("watch scope add '*'")`) → **also RED**: the current L496 string is `…watch scope add * <path>…` (no quotes), so the new assertion fails until the quote fix lands. All REDs in one commit.
- **GREEN:** `CliArgs.cs` — §3.3 description texts + `registered` command (`Argument<string?>` `"project-id"`, `ZeroOrOne`); `ConfigCommands.cs` — dispatch row + `WatchRegisteredAsync` (filter → `store.ListWatchesAsync()` → `OrderBy(ProjectId, StringComparer.Ordinal).ThenBy(Path, WatchPath.PathComparer)` → §3.2 line, `0`→`never`, `no registered watches`, all to stdout, exit 0) + trailing `IWatchStore? watchStore = null`; `Program.cs` — `watchStore: new WatchStore(bank)` + using; `WatchTools.cs` — `memory_watch_add` description; **ConfigCommands.cs L496 warning → quoted `'*'`**; `FileWatcherSteps.cs` — `RunCliAsync` gains `watchStore: Ctx.WatchStore`, +2 bindings.
- **Gate:** `dotnet test --filter "FullyQualifiedName~ConfigCommandsWatchTests|FullyQualifiedName~CliArgsTests|FullyQualifiedName~FileWatcherSteps"` + the BDD feature filter (`FullyQualifiedName~FileWatcherSteps` covers the new scenario via Reqnroll).

### WP4 — `watch remove`
- **RED (4):** `WatchRemove_DeletesEnabledScopeAndConcurrencyRows_ForTarget`, `WatchRemove_Star_DeletesGlobalRows`, `WatchRemove_NoRows_IsExitZeroNoOp` (dispatch throws → exit 1 via catch), `Parse_WatchRemove_ParsesTarget` (parse error).
- **GREEN:** `CliArgs.cs` += `remove` command (`HelpName = "project-id|*"`); `ConfigCommands.cs` += dispatch row + `WatchRemoveAsync` (target-specific or the 3 global keys via `DeleteSettingAsync`, `removed watch config for {target}` to stdout, exit 0). No `Program.cs` change.
- **Gate:** `dotnet test --filter "FullyQualifiedName~ConfigCommandsWatchTests|FullyQualifiedName~CliArgsTests"`.

### WP5 — docs
- No tests; doc read-through at review (block format, `watch registered`, `watch remove`, quoting pitfall).

---

## 4. Coverage the current suite is missing that a regression could silently break

| Question | Finding |
|----------|---------|
| Does anything pin the old one-line `watch list` format outside `ConfigCommandsWatchTests`? | **No** — grep `enabled=true concurrency|scope=\[` across `tests/` hits only the 3 `WatchList_*` tests (5 assertions). No BDD step, no E2E, no other unit file. |
| Do the BDD feature's CLI-rule scenarios break under the new format/command? | **No** — the 6 scenarios (feature L44–76) assert settings effects + the add-scope warning, never list output. `RunCliAsync`'s `watchStore:` arg is additive (R8). The only BDD touch is the warning-quote-fix survival check (`ThenCommandReturnsAddScopeMessage` pins `"add at least one scope"` — unaffected). |
| Does any test pin the description strings or `memory_watch_add`'s description? | **No** (verified, §0) — every WP3 description edit is test-neutral. |
| Does E2E exercise watch verbs? | **No** — 0 `watch` matches in `tests/.../E2E/`. |
| Trailing-newline trap in byte pins | The architect's raw `ShouldBe` sketches would never go green; all full-output pins use `stdout.Trim().ShouldBe(...)` (WP2 correction 1). |
| Fake-enumeration-order trap | `FakeWatchStore.ListWatchesAsync` enumerates the dict; sort tests seed unsorted (WP3 correction 1) so a no-sort handler fails. |
| Docs pin the old format? | **No** — only the plan itself (WP5 writes fresh text). |

---

## 5. Acceptance criteria for coverage after the refactor

1. **Per-file counts (final):**
   - `Unit/Watch/WatchListFormatTests.cs` — **3** (new).
   - `Unit/Setup/ConfigCommandsWatchTests.cs` — **27** (15 unmodified, 3 updated, 2+4+3 new, 1 strengthened assertion).
   - `Unit/Setup/CliArgsTests.cs` — **59** (56 unmodified, 3 new).
   - `BDD/FileWatcherSteps.cs` — +2 bindings + `RunCliAsync` wiring; `file-watcher.feature` — +1 scenario.
   - **Unmodified:** `CliOutputRoutingTests` (7), `WatchConfigTests` (16 — note: architect's "14" stale, verified 16), `ToolInventoryTests` (4), `WatchToolsInventoryTests` (3), `MemoryPromptsTests` (5), all remaining `Unit/*`, BDD/E2E/Integration.
2. **Build gate:** `dotnet build` from the worktree root → 0 errors, 0 warnings (TreatWarningsAsErrors).
3. **Full-suite gate:** `dotnet test` from the worktree root → all green (unit + BDD + E2E + integration; 16 new tests, 3 updated, 1 strengthened).
4. **Untouched files:** `WatchConfig.cs`, `WatchConfigKeys.cs`, `WatchScopeList.cs`, `WatchStore.cs`, `WatchService.cs`, `IWatchStore`, `IWatchService`, `WatchPipeline`, `MemoryPrompts.cs`, `CliArgs.Render`, `CliOutputRoutingTests`, `WatchConfigTests`, server path in `Program.cs` appear in no refactor commit.
5. **Drift detectors named and present:** old format → 5 updated/new full-output pins; ordering → `WatchList_Ordering_IsOrdinalByTargetName` + `WatchRegistered_ListsAllRegistrations_SortedByProjectThenPath` (unsorted seed); `never`/ISO mapping → the same registered full-output pin; empty/filter branches → `WatchRegistered_NoRows…` + `…ReadsOnlyTheWatchesTable` + filter no-match fold-in; warning quote fix → strengthened `WatchEnableStar_WithEmptyScopeAllowlist_PrintsAddScopeMessage` (`watch scope add '*'`).
6. **Stdout discipline:** every `WatchList_*` / `WatchRegistered_*` handler test asserts output on the stdout writer, exit 0, and (for `WatchRegistered_*`) empty stderr; existing routing tests stay green.

---

## 6. Out of scope

- Any production or test change beyond the inventory above (no `Guard.IsNotNull` message pin, no SCL-arity tests for `watch registered`, no partial-family `watch remove` test, no OS-case sort tests, no Spectre-related tests — zero new dependencies).
- Any change to the architect's production design (format text, command shape, ordering semantics, WP ordering).
- MCP watch tool behavior, `MemoryPrompts`, `watch scope list` output, storage/settings keys.
