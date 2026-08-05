# Implementation Plan — CLI Rendering Structure Refactor (extract Render out of CliArgs.cs)

> **Based on:** current state of `src/AiRaccoon/Setup/CliArgs.cs` at commit `e394958` (branch
> `task/watch-star-glob`, draft PR #19) — the shell-glob-expansion hint fix just landed there.
> Request: "clean up the Render code, it is noticeably large — extract it to a separate
> directory; do a structure review and a plan: what components can we extract? how should we
> split them? what are the hot points that change often?"
> **Date:** 2026-08-05 · **Branch:** `task/watch-star-glob` · **PR scope:** one PR, on top of
> the existing fix branch (the refactor is a separate work package, not bundled into #19's diff).
> **Workflow:** TDD mandatory — every production change below is preceded by a failing,
> behavior-focused xunit test (RED → GREEN → REFACTOR). Pure moves use the repo's
> compile-break-as-RED convention (see `docs/plans/cli-args-parsing.md` §7 WP5a): the moved
> test's new call site fails to compile until the new type exists.

---

## 0. Goal

`CliArgs.cs` (392 lines) grew to four responsibilities. The refactor splits it into a thin
facade plus two extracted components under a new `Setup/Cli/` directory, with **zero behavior
change**: same parse results, byte-identical help/version/error rendering, same exit codes,
same stdout/stderr discipline. The split is designed around the hot points that change often
(§3) so each future edit lands in one obvious file. TDD steps in §4; acceptance gates in §6.

**Scope:** only `src/AiRaccoon/Setup/CliArgs.cs` and its immediate test files
(`CliOutputRoutingTests.cs`, `CliGlobExpansionHintTests.cs`). `Program.cs` and
`ConfigCommands.cs` are **not touched** — `Program.cs` keeps calling `CliArgs.Parse` +
`CliArgs.Render(parsed, Console.Error)`; `ConfigCommands` keeps receiving
`commandPath`/`parseResult` exactly as today.

---

## 1. Structure review — what is in CliArgs.cs today

One file, four responsibilities (line counts approximate, commit `e394958`):

| # | Responsibility | Lines | Members |
|---|----------------|-------|---------|
| 1 | **Command-tree construction** | ~156 (40%) | `Verbs`, `BuildFullRootCommand`, `BuildLaunchRootCommand`, `AddLaunchOptions`, `AccessCommand`, `ModelCommand`, `RetrievalCommand`, `SweepCommand`, `SyncCommand`, `WatchCommand`, `EncryptionCommand` |
| 2 | **Parse entry point** | ~84 (21%) | `Parse` (verb detection + launch-root re-parse fallback), `ContainsVerb`, `CommandPathOf`, `ReadOptions`, `OptionValue`, `InstallScopeValue` |
| 3 | **Render loop** | ~106 (27%) | `Render` (help/version/errors via one writer + hint append), `GlobExpansionHint`, `TryUnrecognizedToken`, `TryTypedArgumentToken` |
| 4 | **Parse contract records** | ~19 (5%) | `CliOptions`, `CliParseResult` |
| — | Usings / doc comments / blank lines | ~27 (7%) | — |

Key facts verified while reviewing:

- **External surface is tiny.** Nothing outside the file calls the tree builders or the hint
  directly: `Program.cs` calls `CliArgs.Parse` + `CliArgs.Render`; `ConfigCommands` never sees
  the record (Program.cs unpacks `CommandPath`/`ParseResult` before calling
  `ConfigCommands.RunAsync`); tests call `CliArgs.Parse`/`CliArgs.Render`/
  `CliArgs.GlobExpansionHint` (verified by grep across the repo — the BDD/E2E/ConfigCommands*
  test files use `CliArgs.Parse` only). Everything is `internal`, reached via
  `InternalsVisibleTo` (`src/AiRaccoon/AiRaccoon.csproj:60`) — no csproj change needed.
- **The hot string pins live in the render code:** `TryUnrecognizedToken` pins
  `"Unrecognized command or argument '{t}'."`; `TryTypedArgumentToken` pins
  `"Cannot parse argument '{t}' as expected type 'System.Boolean'."` and
  `"...'System.Int32'."` — exact System.CommandLine 2.0.10 message shapes. A second 2.0.10 pin
  (`Action?.GetType().Name == "VersionOptionAction"`) lives in `Parse`.
- **The cwd-injection seam already exists:** `Render`/`GlobExpansionHint` take an optional
  `IReadOnlySet<string>? cwdEntries`; production defaults to
  `Directory.GetFileSystemEntries(".")`; tests inject a fixed set.

**Verdict:** the tree (1) and the render+hint block (3) are the two extractable components.
The parse logic (2) and the records (4) stay in `CliArgs.cs` — they are the CLI facade and its
contract, and extracting them would churn `Program.cs`/`ConfigCommands`/~40 parse tests for no
behavior gain ("ask if a simpler shape would do" — the simpler shape is: move the two big
change-prone blocks, leave the small stable core in place).

---

## 2. Proposed layout

New concept folder **`src/AiRaccoon/Setup/Cli/`** (namespace `AiRaccoon.Setup.Cli`,
mirroring the repo's folder-namespace convention — cf. `Access/` → `AiRaccoon.Access`):

```
src/AiRaccoon/Setup/CliArgs.cs                  (~150 lines after)  — facade: records + Parse + Render forwarder
src/AiRaccoon/Setup/Cli/CliCommandTree.cs       (~160 lines)        — verb surface + launch options
src/AiRaccoon/Setup/Cli/CliRendering.cs         (~110 lines)        — Render loop + glob hint + SCL-template parsers
```

**Why `Setup/Cli/` and not `Setup/Cli/Rendering/`:** `Setup/` is the CLI chassis (the
technical-chassis exception the screaming-architecture invariant allows). The two extracted
components are both "the CLI surface" — one defines what the CLI *can say* (tree), the other
how it *talks* (rendering). A single `Cli/` folder with two files tells a reader what the
system does; a `Cli/Rendering/` subfolder for a single file would be over-splitting.

**Why only two new types — what stays and why:**

| Piece | Where | Why |
|-------|-------|-----|
| `CliOptions`, `CliParseResult` | stays in `CliArgs.cs` | the CLI contract consumed by Program.cs/ConfigCommands; moving them churns every consumer for no gain |
| `Parse`, `ContainsVerb`, `CommandPathOf`, `ReadOptions`, `OptionValue`, `InstallScopeValue` | stays in `CliArgs.cs` | the facade's own logic; ~84 lines, changes only when the surface (tree) or options change |
| `Verbs`, `BuildFullRootCommand`, `BuildLaunchRootCommand`, `AddLaunchOptions`, the 7 verb builders | → `CliCommandTree.cs` | one responsibility (define the CLI surface), the file's biggest block (~40%), the dominant growth point |
| `Render`, `GlobExpansionHint`, `TryUnrecognizedToken`, `TryTypedArgumentToken` | → `CliRendering.cs` | one responsibility (turn a parse result into CLI text), ~27% of the file, holds every SCL-pinned string |

Rejected shapes (asked "would a simpler shape do"): one new file only (leaves CliArgs.cs at
~290 lines — the size complaint is not addressed); per-verb-family files (7 files for 7 small
builders is ceremony; the repo keeps `ConfigCommands.cs` as one file per layer, with
`EncryptionCommands.cs` split off only because it was built separately); an interface or
delegate for the cwd listing (the optional parameter is already the seam — see §3 hot point 4).

**Member visibility after the move:** `CliCommandTree.Verbs` becomes
`internal static readonly string[]` (read by `CliArgs.ContainsVerb`); `BuildFullRootCommand`
stays `internal static` (called by `CliArgs.Parse`; directly testable via InternalsVisibleTo);
everything else in both classes stays `private static`. `CliRendering.Render` and
`CliRendering.GlobExpansionHint` are `internal static` with the **exact signatures they have
today** (so the moved tests change only the type name, not the call shape).

**CliArgs.cs after the refactor** keeps: the two records, `Parse` (with the two tree calls
pointing at `CliCommandTree`), `ContainsVerb` (reading `CliCommandTree.Verbs`), the
`CommandPathOf`/`ReadOptions`/`OptionValue`/`InstallScopeValue` helpers, and one forwarder:

```csharp
internal static int Render(CliParseResult result, TextWriter output, IReadOnlySet<string>? cwdEntries = null)
    => CliRendering.Render(result, output, cwdEntries);
```

`CliArgs.GlobExpansionHint` is **deleted** (its tests move with the code — see §4 WP2).
`CliArgs.BuildFullRootCommand` is **deleted** (no external caller exists — verified).

---

## 3. Hot points — ranked, with the seam that contains each

Ranked by change frequency × blast radius (the two top ones are the reason the file grew):

| # | Hot point | Change driver | Seam |
|---|-----------|---------------|------|
| 1 | **(b) The command tree** | every new verb family (7 today — `watch` was the most recent) edits the root builder + a verb builder + the verb list | `CliCommandTree.cs`: one file holds `Verbs` + all builders; a new family touches exactly two spots (one private builder + one `root.Add` + one `Verbs` entry), all in that file. `CliCommandTreeTests` pins the surface (§4 WP1). |
| 2 | **(a) SCL error-message templates** | a System.CommandLine upgrade silently breaks the hint (messages drift) and the `VersionOptionAction` name check | all three message templates + the two parsers live as private members of `CliRendering.cs`, with a 1-3 line pin comment ("pinned to System.CommandLine 2.0.10 — on upgrade run `CliGlobExpansionHintTests` and update the templates here"). The 9 hint tests parse *real* args, so they are the drift detector. The `VersionOptionAction` pin stays in `Parse` beside its existing comment. |
| 3 | **(e) Token extraction** (argv + error messages) | `ContainsVerb` must learn every new top-level verb; the message parsers must learn every new typed-argument pattern | `ContainsVerb` reads `CliCommandTree.Verbs` (single source of verb names); the message parsers are pure `message → token` functions isolated in `CliRendering.cs`. |
| 4 | **(c) Current-directory entries injection** | tests must override the real cwd; production reads `Directory.GetFileSystemEntries(".")` | the optional `cwdEntries` parameter on `Render`/`GlobExpansionHint` (exists today, kept); the production default moves to a private `CliRendering.CurrentDirectoryEntries()` so the only `Directory` call in the CLI layer sits inside the rendering file. |
| 5 | **(d) stdout/stderr routing discipline** | any new CLI text must go to the Render writer, never stdout | the writer parameter on `Render` (kept) + the 7 `CliOutputRoutingTests` (kept, unmodified) are the seam and the gate; the convention stays "all CLI text is produced inside `CliRendering.Render`". |

---

## 4. Work packages (TDD — failing test first, build green after every step)

Commit per package; build gate after each: `dotnet build` (repo root, solution
`AiRaccoon.slnx`) → 0 errors, 0 warnings (warnings-as-errors). Test gate after each:
`dotnet test` (repo root) → full suite green.

### WP1 — Extract the command tree → `Setup/Cli/CliCommandTree.cs`

- **RED:** new test file `tests/AiRaccoon.Tests/Unit/Setup/Cli/CliCommandTreeTests.cs`
  (namespace `AiRaccoon.Tests.Unit.Setup.Cli`, xunit.v3 + Shouldly, `[Trait]` Unit/Fast per
  convention). Two behavior-focused tests demanding the new type (compile fail = RED):
  - `Root_ExposesAllVerbFamilies` — `CliCommandTree.BuildFullRootCommand().Children`
    command names equal exactly `{access, model, retrieval, sweep, sync, watch, encryption}`.
  - `LaunchRoot_ExposesLaunchOptionsAndNoVerbs` — `CliCommandTree.BuildLaunchRootCommand()`
    exposes `--transport`/`--data-root`/`--install-scope` and **no** subcommands (pins the
    launch-only root that the verb-less re-parse fallback depends on — untested directly today).
- **GREEN:** create `src/AiRaccoon/Setup/Cli/CliCommandTree.cs` (namespace
  `AiRaccoon.Setup.Cli`): move `Verbs`, `BuildFullRootCommand`, `BuildLaunchRootCommand`,
  `AddLaunchOptions`, and the seven verb builders **verbatim** (no behavior change; move
  `using System.CommandLine;` with the code). In `CliArgs.cs`: `Parse` calls
  `CliCommandTree.BuildFullRootCommand()` / `CliCommandTree.BuildLaunchRootCommand()`;
  `ContainsVerb` reads `CliCommandTree.Verbs`; add `using AiRaccoon.Setup.Cli;`; delete the
  moved members. `BuildLaunchRootCommand` and `AddLaunchOptions` stay `private` in the new
  class (only the root is exercised by tests).
- **Gate:** build + full test suite green. `CliArgsTests.cs` (parse behavior, ~40 tests) is
  **unmodified** — it already pins every verb family end-to-end through `Parse`.

### WP2 — Extract rendering → `Setup/Cli/CliRendering.cs`

- **RED:** move `tests/AiRaccoon.Tests/Unit/Setup/CliGlobExpansionHintTests.cs` →
  `tests/AiRaccoon.Tests/Unit/Setup/Cli/CliGlobExpansionHintTests.cs`, namespace →
  `AiRaccoon.Tests.Unit.Setup.Cli` (add `using AiRaccoon.Setup;` for `CliArgs` and
  `using AiRaccoon.Setup.Cli;` for `CliRendering`). **8 of the 9 tests** change their hint
  call site from `CliArgs.GlobExpansionHint(parsed, CwdEntries)` to
  `CliRendering.GlobExpansionHint(parsed, CwdEntries)` — assertions unchanged.
  `Render_GlobExpansion_AppendsHintAfterParseErrors` keeps calling `CliArgs.Render` (the
  facade) — unchanged. Compile fail = RED.
- **GREEN:** create `src/AiRaccoon/Setup/Cli/CliRendering.cs` (namespace
  `AiRaccoon.Setup.Cli`): move `Render`, `GlobExpansionHint`, `TryUnrecognizedToken`,
  `TryTypedArgumentToken` **verbatim**; extract the `Directory.GetFileSystemEntries` default
  into a private `static IReadOnlySet<string> CurrentDirectoryEntries()`; add the 1-3 line
  class doc comment pinning the message templates to System.CommandLine 2.0.10 (§3 #2). In
  `CliArgs.cs`: replace `Render`'s body with the one-line forwarder, delete
  `CliArgs.GlobExpansionHint` and the two parsers; move the now-unused usings
  (`System.CommandLine.Help` stays with `Parse`'s `HelpAction` check; the rest follow the
  code as the compiler demands).
- **Gate:** build + full suite green. `CliOutputRoutingTests.cs` (7 tests) is **unmodified**
  — it exercises the facade only. Rendering is byte-identical by construction: `Render` still
  calls `parseResult.Invoke(new InvocationConfiguration { Output = output, Error = output })`
  with the same writer, same exit-code return, same hint append order.

### WP3 — Seam hardening review (code review pass, no new behavior)

After WP2 is green, a review-only pass verifies the seams are legible: the pin comments on
`CliRendering.cs` (SCL 2.0.10 message templates) and `CliArgs.Parse`
(`VersionOptionAction` name check) state the upgrade procedure; `CliRendering` contains the
only `Directory.GetFileSystemEntries` call in the CLI layer; no member that moved kept a
duplicate behind in `CliArgs.cs` (the facade has exactly one forwarder). No production change
is expected in this WP — if the review finds a missing pin comment, add it with no test
(there is no behavior to test; the comment states the existing pin).

### WP4 — Final verification + PR

- Run the §6 gates in full; confirm `Program.cs`, `ConfigCommands.cs`, `ServerConfig.cs`,
  `CliArgsTests.cs`, `CliOutputRoutingTests.cs`, and all BDD/E2E files appear in **no** commit
  of this refactor (`git diff <fix-commit>..HEAD --stat` against the pre-refactor state shows
  only the files listed in §5).
- Open the refactor as its own PR (draft PR #19 stays scoped to the fix; this work is a
  separate PR per "One PR per task").

---

## 5. File map

| Action | File | Notes |
|--------|------|-------|
| **create** | `src/AiRaccoon/Setup/Cli/CliCommandTree.cs` | namespace `AiRaccoon.Setup.Cli`; moved verbatim from CliArgs.cs |
| **create** | `src/AiRaccoon/Setup/Cli/CliRendering.cs` | namespace `AiRaccoon.Setup.Cli`; moved verbatim + `CurrentDirectoryEntries()` + pin comments |
| **create** | `tests/AiRaccoon.Tests/Unit/Setup/Cli/CliCommandTreeTests.cs` | 2 new tests (WP1 RED) |
| **move** | `tests/AiRaccoon.Tests/Unit/Setup/CliGlobExpansionHintTests.cs` → `tests/AiRaccoon.Tests/Unit/Setup/Cli/CliGlobExpansionHintTests.cs` | namespace → `AiRaccoon.Tests.Unit.Setup.Cli`; 8 hint call sites → `CliRendering.GlobExpansionHint`; 1 Render-wiring test unchanged in assertions |
| **modify** | `src/AiRaccoon/Setup/CliArgs.cs` | keep records + Parse + helpers; tree/hint code removed; `Render` → one-line forwarder; add `using AiRaccoon.Setup.Cli;` |
| **untouched** | `src/AiRaccoon/Program.cs`, `src/AiRaccoon/Setup/ConfigCommands.cs`, `src/AiRaccoon/Setup/ServerConfig.cs`, `tests/AiRaccoon.Tests/Unit/Setup/CliArgsTests.cs`, `tests/AiRaccoon.Tests/Unit/Setup/CliOutputRoutingTests.cs`, BDD + E2E tests | all call `CliArgs.Parse`/`CliArgs.Render` only — unaffected |

No `.csproj`, `Directory.Packages.props`, or `InternalsVisibleTo` changes.

---

## 6. Acceptance criteria & quality gates

1. **Build:** `dotnet build` (repo root) → 0 errors, 0 warnings.
2. **Full suite:** `dotnet test` (repo root) → all green. The 16 in-scope CLI tests: the 7
   routing tests green **unmodified**; the 9 hint tests green **with assertions unmodified**
   (they moved with their code, call-site type change only); `CliArgsTests` (~40) green
   unmodified.
3. **Rendering byte-identical:** help/version/parse-error text flows through the same
   `ParseResult.Invoke` call with the same caller-supplied writer — no formatting code was
   touched (move was verbatim). The routing tests pin "Usage"/"Unrecognized command or
   argument '--bogus'." and the empty-stdout contract; the hint tests pin both hint strings.
4. **Exit codes unchanged:** `Render` returns the same `Invoke` exit code — 0 for
   help/version, 1 for parse errors (pinned by `Render_Help_ReturnsZeroExitCode`,
   `Render_Version_ReturnsZeroExitCode`, `Render_ParseError_WritesOnlyToErrorWriter`,
   `Render_GlobExpansion_AppendsHintAfterParseErrors`).
5. **Hint behavior unchanged:** same detection signature (cwd-hit majority / typed-argument
   fallback), same reconstructed example text, same `cwdEntries` injection — pinned by the 9
   moved tests, including the real-cwd smoke test.
6. **Parse behavior unchanged:** `Parse` logic untouched apart from the two
   `CliCommandTree` call sites and the `Verbs` reference — the re-parse fallback,
   `ContainsVerb` scanning, and `ReadOptions` semantics are all pinned by `CliArgsTests`.
7. **Program.cs / ConfigCommands.cs untouched:** they appear in no commit of this refactor.

---

## 7. Out of scope

- Any change to `Program.cs`, `ConfigCommands.cs`, `ServerConfig.cs`, or the verb-handler
  layer; any user-visible CLI surface change (option names, help text, descriptions).
- Extracting per-verb-family files, splitting `CliRendering` further, adding interfaces or
  DI for the rendering/hint layer.
- Moving `CliOptions`/`CliParseResult` out of `CliArgs.cs`; changing namespaces of the
  facade, `ServerConfig`, or any existing type.
- The planned QA test-coverage work (separate agent) — this plan names the seams (§3) and
  the tests that must move (§4) so that work can proceed in parallel or after.
- SCL upgrade, new hint signatures (e.g. new typed-argument patterns), new hint text.

---

## 8. Risks & open items

- **SCL 2.0.10 pins (pre-existing, now contained):** (1) the three error-message templates in
  `CliRendering.cs`; (2) the `Action?.GetType().Name == "VersionOptionAction"` check in
  `CliArgs.Parse`; (3) root-options-after-subcommand and required-subcommand rules (pinned by
  `Parse_RootOptionAfterVerb_ReturnsError`, `Parse_VerbWithoutSubcommand_ReturnsError`).
  **Upgrade procedure after a SCL bump:** run the suite — any drifted template surfaces as a
  failing hint/routing test pointing at the single file that owns it.
- **Move-vs-change review risk:** reviewers may read the moved files as rewritten code. Mitigate:
  moves are verbatim (the PR diff should show pure renames for the code blocks; `git diff -M`
  helps), and §6 gates pin the behavior.
- **`GlobExpansionHint_RealCurrentDirectory_DetectsExpansion`** depends on the test host's
  working directory containing ≥ 3 entries — pre-existing environmental assumption, unchanged
  by the refactor.
- **Optional follow-up (not in scope):** a direct test for `CliRendering.CurrentDirectoryEntries()`
  is deliberately not added — the real-cwd smoke test covers the default path; a unit test
  would only re-assert `Directory.GetFileSystemEntries` behavior.
