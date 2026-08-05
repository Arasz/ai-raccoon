# Test Coverage Plan — CLI Rendering Structure Refactor (extract Render out of CliArgs.cs)

> **Based on:** `docs/plans/watch-star-glob-cli-refactor.md` (the architect's implementation plan,
> commit `e394958`, branch `task/watch-star-glob`, draft PR #19). This is the test-engineer (QA)
> companion plan: the complete test inventory after the refactor, the hot-point gap analysis, the
> per-work-package RED→GREEN sequence, and the acceptance gates. It does not restate the
> architect's production design except where the test surface constrains it.
> **Date:** 2026-08-05 · **Branch:** `task/watch-star-glob` · **Scope:** plan only — no production
> or test code is changed by this document.
> **Workflow:** TDD mandatory; pure moves use the repo's compile-break-as-RED convention
> (see `docs/plans/cli-args-parsing.md` §7 WP5a — the moved test's new call site fails to compile
> until the new type exists). xunit.v3 + Shouldly, `[Trait(TestCategories.Category, TestCategories.Unit)]`
> + `[Trait(TestCategories.Speed, TestCategories.Fast)]` on every test class; namespaces mirror
> folders (`AiRaccoon.Tests.Unit.Setup.Cli`); `InternalsVisibleTo` already covers internal types
> (`src/AiRaccoon/AiRaccoon.csproj:60`).

---

## 0. Current worktree state (verified at plan time)

The worktree is **mid-WP1**: the two WP1 deliverables already exist, but the facade conversion has
not happened yet.

| Item | State | Notes |
|------|-------|-------|
| `src/AiRaccoon/Setup/Cli/CliCommandTree.cs` | **exists** (174 lines) | complete move target: `Verbs` (internal), `BuildFullRootCommand` (internal), `BuildLaunchRootCommand` (**internal**), `AddLaunchOptions` (private), the 7 verb builders. Content matches `CliArgs.cs` verbatim. |
| `tests/AiRaccoon.Tests/Unit/Setup/Cli/CliCommandTreeTests.cs` | **exists** (2 tests) | `Root_ExposesAllVerbFamilies`, `LaunchRoot_ExposesLaunchOptionsAndNoVerbs` — the architect's two tests, already written with correct traits/namespace. |
| `src/AiRaccoon/Setup/CliArgs.cs` | **unchanged** (392 lines) | still carries its private tree, `Render`, `GlobExpansionHint`, and both parsers. `CliCommandTree` and `CliArgs` are distinct classes, so the tree compiles today with both copies. |
| `src/AiRaccoon/Setup/Cli/CliRendering.cs` | **does not exist** | WP2 not started. |
| `tests/AiRaccoon.Tests/Unit/Setup/CliGlobExpansionHintTests.cs` | **unchanged** (9 tests) | still calls `CliArgs.GlobExpansionHint` / `CliArgs.Render`. |

> **Visibility correction to the architect plan (§2, §4 WP1):** the architect wrote that
> `BuildLaunchRootCommand` and `AddLaunchOptions` "stay private in the new class", but its own
> test `LaunchRoot_ExposesLaunchOptionsAndNoVerbs` calls `CliCommandTree.BuildLaunchRootCommand()`
> directly. The existing worktree file already declares it **internal** — that is the correct
> state and must be kept (the test is the caller). `AddLaunchOptions` stays private (it is
> exercised only through the two root builders).

---

## 1. Test inventory after the refactor

| File (after refactor) | Count | Action | Changes |
|-----------------------|-------|--------|---------|
| `tests/AiRaccoon.Tests/Unit/Setup/CliArgsTests.cs` | **56** (verified — the architect's "~40" is stale) | **stays, unmodified** | pins every verb family + launch flags end-to-end through `CliArgs.Parse`; includes `Render_Help_ListsEncryptionVerb`, which pins help text through the facade `CliArgs.Render`. |
| `tests/AiRaccoon.Tests/Unit/Setup/CliOutputRoutingTests.cs` | **7** | **stays, unmodified** | exercises the facade only (`CliArgs.Render`); after WP2 the facade forwards to `CliRendering.Render`, so these 7 are the stdout/stderr gate on the forwarder. |
| `tests/AiRaccoon.Tests/Unit/Setup/Cli/CliGlobExpansionHintTests.cs` | **10** (9 moved + 1 new, §2 hot point a) | **move** from `Unit/Setup/` | namespace → `AiRaccoon.Tests.Unit.Setup.Cli`; keep `using AiRaccoon.Setup;` (`CliArgs.Parse`), add `using AiRaccoon.Setup.Cli;`. **8 of the 9** change their hint call site from `CliArgs.GlobExpansionHint(parsed, CwdEntries)` to `CliRendering.GlobExpansionHint(parsed, CwdEntries)` (lines 27, 38, 49, 60, 68, 76, 84, 99 in the current file) — assertions unchanged. `Render_GlobExpansion_AppendsHintAfterParseErrors` (line 110) keeps calling `CliArgs.Render` — unchanged. Plus the new Int32-template test (§2 hot point a). |
| `tests/AiRaccoon.Tests/Unit/Setup/Cli/CliCommandTreeTests.cs` | **2** (already exist) | **strengthen, no new tests** | add one assertion to `Root_ExposesAllVerbFamilies` pinning `CliCommandTree.Verbs` to the same 7 names (§2 hot point e). |
| BDD (`FileWatcherSteps.cs`, `EncryptionBitwardenSteps.cs`) + E2E (`McpServerE2ETests.cs`, `McpServerLaunchArgsE2ETests.cs`) + `ConfigCommands*Tests.cs` (4 files) | — | **untouched** | verified by grep: they call `CliArgs.Parse` only, never `Render`/`GlobExpansionHint`/tree builders. |

In-scope CLI unit tests after the refactor: **56 + 7 + 10 + 2 = 75**.

---

## 2. Gap analysis against the architect's hot-point table (plan §3)

For each hot point: does the post-refactor suite detect drift, and what is missing?

| Hot point | Post-refactor detector | Verdict / missing tests |
|-----------|------------------------|-------------------------|
| **(b) command tree** | `Root_ExposesAllVerbFamilies` (exact ordered set of the 7 verb families on the full root), `LaunchRoot_ExposesLaunchOptionsAndNoVerbs` (the 3 launch options present, zero subcommands), plus all 56 `CliArgsTests` (every family parses end-to-end through `Parse` → full root) and `Render_Help_ListsEncryptionVerb` (help lists verbs). | **Sufficient.** The architect's two tests cover the structural contract; the 56 parse tests cover behavior. The launch-root no-verbs property (untested before) is now pinned, and the test **does** pin that the launch root still exposes `--transport`/`--data-root`/`--install-scope` (it asserts all three `Option.Name`s). No new tree tests needed. Deliberately not pinned: the hidden host flags (`--environment`/`--contentRoot`/`--applicationName`) — they are an E2E-host accommodation (architect §3 hot point 4 explicitly excludes them; a test would couple to `WebApplicationFactory` internals — over-testing). |
| **(a) SCL error-message templates** | The 9 moved hint tests parse **real args** through `CliRendering.GlobExpansionHint`; `Render_ParseError_WritesOnlyToErrorWriter` pins `"Unrecognized command or argument '--bogus'."`; `Render_GlobExpansion_AppendsHintAfterParseErrors` pins hint append after parse errors. | **One gap: the `System.Int32` template branch is currently unexercised.** The 9 hint tests only hit the `Unrecognized command or argument` and `System.Boolean` typed-argument branches (`watch enable … true`). `TryTypedArgumentToken`'s Int32 branch (`"Cannot parse argument '{t}' as expected type 'System.Int32'."` — one of the three pinned 2.0.10 strings) has no covering test: `Parse_WatchConcurrency_NonNumeric_ReturnsError` only asserts errors non-empty and never calls the hint. If SCL's Int32 message shape drifted, no test would fail. **Add one test** to the moved hint file: `GlobExpansionHint_WatchConcurrencyInt32Value_StillFiresViaTypedArgument` — `CliRendering.GlobExpansionHint(CliArgs.Parse(["watch", "concurrency", "CLAUDE.md", "README.md", "/tmp/x"]), CwdEntries)` must be non-null and contain `"ai-raccoon watch concurrency '*' /tmp/x"` (target=`CLAUDE.md`, value=`README.md` → Int32 error with a cwd hit, `/tmp/x` → unrecognized, `CommandPath=[watch,concurrency]`). **Direct template-level tests of the private parsers: agreed over-testing** — the parsers' only contract is "extract the token from SCL 2.0.10's real message shape"; feeding them hand-written strings would pin our own strings, not SCL's output, and would require exposing private members. Real-args coverage is both adequate and the only drift detector that works. |
| **(e) token extraction** | `ContainsVerb` reads `CliCommandTree.Verbs` (single source); `Root_ExposesAllVerbFamilies` pins the root children. | **Small drift hole: `Verbs` itself is not pinned.** `Root_ExposesAllVerbFamilies` asserts `root.Children` names but not `CliCommandTree.Verbs`. If a new family is added to the tree but forgotten in `Verbs`, `ContainsVerb` silently misses it (the verb-less fallback misfires only when the new verb appears in an erroring arg set — subtle, no current test would catch it). Fix: **one added assertion inside the existing test** — `CliCommandTree.Verbs.ShouldBe(["access", "model", "retrieval", "sweep", "sync", "watch", "encryption"]);` (same ordered set). The message parsers' token extraction is covered by the hint tests (incl. the new Int32 test). No new test file. |
| **(c) cwd-entries injection** | 8 hint tests inject the fixed `CwdEntries` set; `GlobExpansionHint_RealCurrentDirectory_DetectsExpansion` exercises the production default (no `cwdEntries` arg) — after WP2 that default is `CliRendering.CurrentDirectoryEntries()`. | **Sufficient. Agree with the architect: no direct unit test for `CurrentDirectoryEntries()`.** The private helper is a thin projection of `Directory.GetFileSystemEntries(".")` (BCL behavior); a unit test would re-assert the BCL and still depend on the test host's cwd having entries — the same environmental assumption the smoke test already carries. The smoke test is the correct level: it proves the default path end-to-end (hint fires with the real cwd). |
| **(d) stdout/stderr routing** | The 7 `CliOutputRoutingTests` stay unmodified and exercise the **facade** `CliArgs.Render`; `Render_GlobExpansion_AppendsHintAfterParseErrors` pins hint append + exit code 1 through the facade; `Render_Help_ReturnsZeroExitCode` / `Render_Version_ReturnsZeroExitCode` pin exit codes. | **Sufficient, including the Render forwarder.** If the forwarder is dropped → ~9 call sites fail to compile. If the forwarder misroutes the writer, the writer-content assertions and the `Console.SetOut` redirection checks fail. No dedicated "forwarder calls through" test — that would be structural over-testing; behavior through the facade is the contract. |

**Bottom line: exactly one new test (Int32 template) and one added assertion (`Verbs`) beyond the architect's inventory.** Everything else in the architect's test plan is adequate; its two `CliCommandTreeTests` suffice.

---

## 3. Per-work-package RED→GREEN sequence

Repo convention: for pure moves, the RED is the **compile break** of the moved/new call sites; the
gate after each package is `dotnet build` (0 errors, 0 warnings) + `dotnet test` (full suite green).

### WP1 — Command tree (partially executed; finish it)

- **RED (already consumed):** `tests/AiRaccoon.Tests/Unit/Setup/Cli/CliCommandTreeTests.cs` exists
  with the 2 tests; they failed to compile before `CliCommandTree.cs` existed. **Strengthen before
  GREEN:** add the `CliCommandTree.Verbs` assertion to `Root_ExposesAllVerbFamilies` (§2 hot point e).
- **GREEN (remaining production change):** slim `CliArgs.cs` — `Parse` calls
  `CliCommandTree.BuildFullRootCommand()` / `CliCommandTree.BuildLaunchRootCommand()`; `ContainsVerb`
  reads `CliCommandTree.Verbs`; add `using AiRaccoon.Setup.Cli;`; delete the moved members. The RED
  for *this* step is the compile break inside `CliArgs.cs` itself (references to deleted members
  fail to compile until rewired). Keep `CliCommandTree.BuildLaunchRootCommand` **internal** (the
  test calls it — §0).
- **Gate:** build green; **all 56 `CliArgsTests` green unmodified** (they pin every verb family and
  the launch-flag fallback end-to-end — if `Parse` were rewired wrongly, they fail); 7 routing tests
  green; 2 tree tests green (with the strengthened assertion).

### WP2 — Rendering extraction

- **RED (compile-break-as-RED, one step):** move
  `tests/AiRaccoon.Tests/Unit/Setup/CliGlobExpansionHintTests.cs` →
  `tests/AiRaccoon.Tests/Unit/Setup/Cli/CliGlobExpansionHintTests.cs`, namespace →
  `AiRaccoon.Tests.Unit.Setup.Cli`; switch the **8** hint call sites to
  `CliRendering.GlobExpansionHint` (assertions unchanged); keep `Render_GlobExpansion_AppendsHintAfterParseErrors`
  on `CliArgs.Render`; **add** the new Int32 test (§2 hot point a, written against
  `CliRendering.GlobExpansionHint`). The file cannot compile until `CliRendering.cs` exists — that
  compile failure is the RED.
- **GREEN:** create `src/AiRaccoon/Setup/Cli/CliRendering.cs` — move `Render`, `GlobExpansionHint`,
  `TryUnrecognizedToken`, `TryTypedArgumentToken` verbatim; extract
  `private static IReadOnlySet<string> CurrentDirectoryEntries()`; add the 1–3 line pin comment
  (SCL 2.0.10 templates). In `CliArgs.cs`: `Render` body → the one-line forwarder; delete
  `CliArgs.GlobExpansionHint` + both parsers; move usings as the compiler demands.
- **Gate:** build green; **all 10 hint tests green** (9 moved assertions byte-unchanged, 1 new);
  7 routing tests green unmodified; 56 `CliArgsTests` green unmodified.

### WP3 — Seam-hardening review (no behavior, no tests)

- Review-only pass per architect plan §4 WP3: pin comments on `CliRendering.cs` (SCL 2.0.10
  message templates) and `CliArgs.Parse` (`VersionOptionAction`); the only
  `Directory.GetFileSystemEntries` call in the CLI layer lives in `CliRendering`; the facade has
  exactly one forwarder. **No test changes** (nothing to pin that isn't already pinned — a comment
  states the existing pin, per architect plan).

### WP4 — Final verification + PR

- Run §4 gates in full. Confirm `Program.cs`, `ConfigCommands.cs`, `ServerConfig.cs`,
  `CliArgsTests.cs`, `CliOutputRoutingTests.cs`, and all BDD/E2E files appear in **no** commit of
  this refactor. Refactor ships as its own PR (draft PR #19 stays scoped to the fix).

---

## 4. Coverage the current suite is missing that a regression would silently break

| Question | Finding |
|----------|---------|
| Does any test pin that `Parse` routes through the **full root tree**? | **Yes — all 56 `CliArgsTests` by construction.** Every verb-family and launch-flag test calls `CliArgs.Parse`, which builds the full root. If `Parse` were rewired to the launch root or a partial tree, dozens of tests fail immediately. Also `Render_Help_ListsEncryptionVerb` pins the full tree's help output. |
| Does BDD/E2E exercise the **watch commands** end-to-end so the hint stays reachable in production? | **Partially — parse/dispatch yes, rendering no.** `FileWatcherSteps.RunCliAsync` (BDD, 6 watch-config scenarios) and `McpServerE2ETests.RunConfigCliAsync` run real `CliArgs.Parse` + `ConfigCommands.RunAsync` — the watch verb stays reachable and parseable end-to-end. **No test anywhere calls `CliArgs.Render`/the hint outside the unit layer** (verified by grep): the hint's production reachability rests on `Program.cs:13` (`CliArgs.Render(parsed, Console.Error)`, untouched by the refactor) plus the 9 hint + 7 routing unit tests. **No new E2E needed** — an E2E that renders a glob-error through the real process would only re-assert what the unit tests pin (writer discipline + hint text) at much higher cost; the refactor is byte-identical by construction (verbatim move). |
| Launch-root fallback pinned? | **Yes, behaviorally:** `Parse_LaunchFlagsOnly_StillLaunchesServer`, `Parse_ParsesDataRootOption`, `Parse_HostBootstrapFlags_AreAcceptedAndIgnored` exercise the verb-less re-parse path; `McpServerLaunchArgsE2ETests` drives `--install-scope=project` through the **real entry point**. The new `LaunchRoot_ExposesLaunchOptionsAndNoVerbs` adds the structural pin. |
| **Int32 SCL template** (found gap) | **Not covered today** — fixed by the one new hint test (§2 hot point a). This is the only real coverage hole the refactor touches: it lives in the code being moved, so WP2 is the right moment to pin it. |
| **`Verbs` ↔ tree sync** (found gap) | **Not pinned today** — fixed by the added assertion (§2 hot point e). |

---

## 5. Acceptance criteria for coverage after the refactor

1. **Per-file test counts (final):**
   - `tests/AiRaccoon.Tests/Unit/Setup/CliArgsTests.cs` — **56**, unmodified.
   - `tests/AiRaccoon.Tests/Unit/Setup/CliOutputRoutingTests.cs` — **7**, unmodified.
   - `tests/AiRaccoon.Tests/Unit/Setup/Cli/CliGlobExpansionHintTests.cs` — **10** (9 moved with
     assertions unchanged + 1 new Int32-template test).
   - `tests/AiRaccoon.Tests/Unit/Setup/Cli/CliCommandTreeTests.cs` — **2** (one added assertion
     inside `Root_ExposesAllVerbFamilies`).
   - In-scope CLI unit total: **75**. Every existing assertion of the 9 moved hint tests and the 56
     parse tests is byte-unchanged (call-site type change only for 8 of them).
2. **Build gate:** `dotnet build` (repo root, `AiRaccoon.slnx`) → 0 errors, 0 warnings.
3. **Full-suite gate:** `dotnet test` (repo root) → all green — in-scope 75 plus the untouched
   BDD/E2E/ConfigCommands surface (≈900 declared `[Fact]`/`[Theory]` project-wide incl. the 1 new).
4. **No touched-untouched files:** `Program.cs`, `ConfigCommands.cs`, `ServerConfig.cs`,
   `CliArgsTests.cs`, `CliOutputRoutingTests.cs`, BDD + E2E files appear in no refactor commit.
5. **Drift detectors named and present:** the 3 SCL 2.0.10 message templates each have a
   real-args test that fails on drift (`Unrecognized…` × routing + hint tests; `System.Boolean` ×
   `GlobExpansionHint_TwoFilesAndValueToken_StillFiresViaTypedArgument`; `System.Int32` × the new
   `GlobExpansionHint_WatchConcurrencyInt32Value_StillFiresViaTypedArgument`); `Verbs` and the root
   children are pinned as one ordered set.

---

## 6. Out of scope

- Any production or test change beyond the inventory above (no direct parser tests, no
  `CurrentDirectoryEntries()` unit test, no hidden-flag pins, no forwarder test, no new E2E).
- Any change to the architect's production design (facade shape, visibility, file layout).
- SCL upgrades, new hint signatures, new hint text.
