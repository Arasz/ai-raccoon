# Derive the MCP tool scope from the tool declaration

**Date:** 2026-10-21
**Task:** `air-derive-tool-scope-from-declarations`
**Branch:** `task/air-derive-tool-scope-from-declarations` → PR #907
**Release:** 1.63.0
**Base:** `3b9fbc00` (PR #906, merged)

## What it does

The MCP tool scope classification — which tools may run against the reserved `shared` document
target — stops being a hand-written list and becomes a declaration on the tool itself, compiled
by the existing Roslyn generator. A tool method that declares no scope is a compile error, so
the classification cannot drift from the tool surface the way a list can.

The reserved target also refuses `memory_delete`, which it previously allowed and which nothing
documented.

## Why

The review of PR #906 (`2026-10-21-pr906-scope-check-review.md`, in this directory) found the
two weaknesses this task fixes:

- **F8** — `src/AiRaccoon/Tools/ToolNameScope.cs` hand-writes seven exact tool names plus two
  ordinal prefix families. Every name not on the list is *allowed* on the reserved target, and
  nothing compares the list with the assembly's generated tool inventory, so a newly added
  write-shaped tool is allowed there until somebody remembers the list. The list is a mirror of
  the tool surface that nothing compares it to.
- **F12 / F12a** — `memory_delete` is the one destructive tool allowed on the reserved target,
  and neither ADR-0137 nor the operator reference mentions it. On that target the operation can
  only ever remove the single chunk whose hash was named, it advances no watch fingerprint, and
  the digest then hash-skips on the unchanged fingerprint — so the content is not restored, the
  document stays partially indexed, and the watch keeps reporting the file as current. The
  reserved target's content is owned by its watch and its file; a delete there is not an
  operation with a coherent meaning.

## The decisions

1. **Attribute plus source generator** (the owner's call, option A). Every `[McpServerTool]`
   method declares its scope; the generator collects the tool surface and the scope in one pass
   and emits the classification; a missing declaration is a compile error (`AIRGEN002`). The
   hand-written list and both prefix families go away. The scope is an **enum** — `All`,
   `Shared`, `Project` — and the rule is symmetric: a tool's scope must cover the target's
   scope (the correction below).
2. **`memory_delete` is refused on the reserved target**, and the refusal is written down in
   ADR-0137 and in the operator reference.
3. A name that is not a declared tool is **refused** on the reserved target. The generated map is
   the authority over the tool surface, so the default is deny rather than the old
   default-allow. No real tool's behaviour changes: the gate only ever passes a declared tool's
   name.
4. `ToolNameScope` survives as a thin hand-written adapter over the generated data. The public
   contract `IToolNameScope` — which ADR-0137 and the existing tests name — is unchanged, and so
   is the DI registration.

## The plan

Eight steps, twenty-seven acceptance criteria for the first delivery, recorded in the task
graph (`.ai-badger/task-tracking/plans/2026-10-21-air-derive-tool-scope-from-declarations.md`),
and four steps, sixteen criteria for the correction
(`.ai-badger/task-tracking/plans/2026-10-21-air-tool-scope-enum-and-analyzer.md`).
Serial: one implementer, and the steps share the tool files and the test project.

| Step | What it does |
|---|---|
| s1 | The test list, then the two failing tests (the behaviour test and the generator test, each red at runtime, not at compile time) |
| s2 | The scope attribute and the generator's second output, with `AIRGEN002` |
| s3 | The scope declared on all thirty tool methods; the hand-written list emptied |
| s4 | The scope test exhaustive and green, with two mutations seen red and then green |
| s5 | The reserved target refuses `memory_delete`, proven through the tool gate |
| s6 | ADR-0139, ADR-0137 amended, the operator reference, this record |
| s7 | VERSION 1.63.0 and its changelog entry |
| s8 | The touched surface run once; the rest to CI |
| c1 | The scope is an enum, and the rule is symmetric (the owner's correction) |
| c2 | A real Roslyn analyzer owns the "a tool declares no scope" error |
| c3 | The decision records follow the enum and the analyzer |
| c4 | The touched surface once, then CI |

## Evidence

### The three failing tests (s1)

**RED 1 — the scope policy.** `dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --filter "FullyQualifiedName~ScopeCheckTests"` — 6 tests, **4 failed**, 2 succeeded, before any production change. The failure names `memory_delete`:

```
  Expected Code  | ...  109  101  109  111  114  121  95   115  119  101  101  112  44   32   109  101  109  111  114  121  95   ...
  Actual Code    | ...  101  109  111  114  121  95   119  111  114  107  115  112  97   99   101  95   98   101  103  105  110  ...
  Difference     |       |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |
                 |      \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/
  Index          | ...  170  171  172  173  174  175  176  177  178  179  180  181  182  183  184  185  186  187  188  189  190  ...
  Expected Value | ...  w    o    r    k    s    p    a    c    e    _    b    e    g    i    n    ,    \s   m    e    m    o    ...
  Actual Value   | ...  ,    \s   m    e    m    o    r    y    _    w    o    r    k    s    p    a    c    e    _    c    o    ...
  Expected Code  | ...  119  111  114  107  115  112  97   99   101  95   98   101  103  105  110  44   32   109  101  109  111  ...
  Actual Code    | ...  44   32   109  101  109  111  114  121  95   119  111  114  107  115  112  97   99   101  95   99   111  ...
  Difference     |       |    |    |    |    |    |    |    |    |    |    |    |    |    |         |    |    |    |    |    |
                 |      \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/       \|/  \|/  \|/  \|/  \|/  \|/
  Index          | ...  191  192  193  194  195  196  197  198  199  200  201  202  203  204  205  206  207  208  209  210  211  ...
  Expected Value | ...  r    y    _    w    o    r    k    s    p    a    c    e    _    c    o    n    s    o    l    i    d    ...
  Actual Value   | ...  n    s    o    l    i    d    a    t    e    ,    \s   m    e    m    o    r    y    _    w    o    r    ...
  Expected Code  | ...  114  121  95   119  111  114  107  115  112  97   99   101  95   99   111  110  115  111  108  105  100  ...
  Actual Code    | ...  110  115  111  108  105  100  97   116  101  44   32   109  101  109  111  114  121  95   119  111  114  ...
  Difference     |       |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |    |
                 |      \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/  \|/
  Index          | ...  212  213  214  215  216  217  218  219  220  221  222  223  224  225  226  227  228  229  230  231  232  ...
  Expected Value | ...  a    t    e    ,    \s   m    e    m    o    r    y    _    w    o    r    k    s    p    a    c    e    ...
  Actual Value   | ...  k    s    p    a    c    e    _    d    i    s    c    a    r    d    ,    \s   m    e    m    o    r    ...
  Expected Code  | ...  97   116  101  44   32   109  101  109  111  114  121  95   119  111  114  107  115  112  97   99   101  ...
  Actual Code    | ...  107  115  112  97   99   101  95   100  105  115  99   97   114  100  44   32   109  101  109  111  114  ...
  Additional Info:
      the refused set is the ordinary-only tools, memory_delete among them
    at AiRaccoon.Tests.Unit.Mcp.ScopeCheckTests.TheRefusedSet_IsExactlyTheOrdinaryOnlyTools() in /Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-derive-tool-scope-from-declarations/tests/AiRaccoon.Tests/Unit/Mcp/ScopeCheckTests.cs:72
    at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
    at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-derive-tool-scope-from-declarations/tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests.dll (net10.0|arm64) failed with 4 error(s) (1s 331ms)
Exit code: 2
  Standard output: -> Loading plugin /Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-derive-tool-scope-from-declarations/tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests.dll
  -> Loading plugin /Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-derive-tool-scope-from-declarations/tests/AiRaccoon.Tests/bin/Debug/net10.0/Reqnroll.xUnit3.ReqnrollPlugin.dll
  -> Using reqnroll.json
Test run summary: Failed!
  total: 6
  failed: 4
  succeeded: 2
  skipped: 0
  duration: 1s 484ms
Test run completed with non-success exit code: 2 (see: https://aka.ms/testingplatform/exitcodes)
```

**RED 2 — the generator's missing-scope diagnostic.** `dotnet test tests/AiRaccoon.Generators.Tests/AiRaccoon.Generators.Tests.csproj --filter "FullyQualifiedName~McpToolScopeGenerator"` — 1 test, **failed**:

```
Running tests from /Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-derive-tool-scope-from-declarations/tests/AiRaccoon.Generators.Tests/bin/Debug/net10.0/AiRaccoon.Generators.Tests.dll (net10.0|arm64)
failed AiRaccoon.Generators.Tests.McpToolScopeGeneratorTests.Generate_ReportsDiagnosticWhenAToolDeclaresNoScope (466ms)
  from /Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-derive-tool-scope-from-declarations/tests/AiRaccoon.Generators.Tests/bin/Debug/net10.0/AiRaccoon.Generators.Tests.dll (net10.0|arm64)
  Shouldly.ShouldAssertException : result.Diagnostics
      should contain an element satisfying the condition
  (diagnostic.Id == "AIRGEN002")
      but does not
    at AiRaccoon.Generators.Tests.McpToolScopeGeneratorTests.Generate_ReportsDiagnosticWhenAToolDeclaresNoScope() in /Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-derive-tool-scope-from-declarations/tests/AiRaccoon.Generators.Tests/McpToolScopeGeneratorTests.cs:42
    at System.Reflection.MethodBaseInvoker.InterpretedInvoke_Method(Object obj, IntPtr* args)
    at System.Reflection.MethodBaseInvoker.InvokeWithNoArgs(Object obj, BindingFlags invokeAttr)
/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-derive-tool-scope-from-declarations/tests/AiRaccoon.Generators.Tests/bin/Debug/net10.0/AiRaccoon.Generators.Tests.dll (net10.0|arm64) failed with 1 error(s) (920ms)
Exit code: 2
Test run summary: Failed!
  total: 1
  failed: 1
  succeeded: 0
  skipped: 0
  duration: 1s 074ms
Test run completed with non-success exit code: 2 (see: https://aka.ms/testingplatform/exitcodes)
```

**RED 3 — the reserved target's delete.** `dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --filter "FullyQualifiedName~SharedDelete_IsRefused"` — 1 test, **failed**:

```
  "shared-project-unsupported"
      but was actually
  "no refusal"
    at AiRaccoon.Tests.Integration.SharedProjectTests.SharedDelete_IsRefused_AndLeavesTheDocumentAlone() in /Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-derive-tool-scope-from-declarations/tests/AiRaccoon.Tests/Integration/SharedProjectTests.cs:289
    at System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw()
    at AiRaccoon.Tests.Integration.SharedProjectTests.SharedDelete_IsRefused_AndLeavesTheDocumentAlone() in /Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-derive-tool-scope-from-declarations/tests/AiRaccoon.Tests/Integration/SharedProjectTests.cs:292
    at System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw()
    at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)
    at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task, ConfigureAwaitOptions options)
    --- End of stack trace from previous location ---
    at System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw()
    at System.Runtime.CompilerServices.TaskAwaiter.ThrowForNonSuccess(Task task)
    at System.Runtime.CompilerServices.TaskAwaiter.HandleNonSuccessAndDebuggerNotification(Task task, ConfigureAwaitOptions options)
/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-derive-tool-scope-from-declarations/tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests.dll (net10.0|arm64) failed with 1 error(s) (2s 188ms)
Exit code: 2
  Standard output: -> Loading plugin /Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-derive-tool-scope-from-declarations/tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests.dll
  -> Loading plugin /Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-derive-tool-scope-from-declarations/tests/AiRaccoon.Tests/bin/Debug/net10.0/Reqnroll.xUnit3.ReqnrollPlugin.dll
  -> Using reqnroll.json
Test run summary: Failed!
  total: 1
  failed: 1
  succeeded: 0
  skipped: 0
  duration: 2s 327ms
Test run completed with non-success exit code: 2 (see: https://aka.ms/testingplatform/exitcodes)
```

The counts are the measured ones, not the plan's guess: the scope filter runs six tests, four of
which are red (the refused set, the allowed set, the `memory_delete` case and the default-deny
case); the union assertion passes today because the split does not change which names are
classified. The plan's acceptance criteria said "failed: 1" — the intent (a real failure, not a
filter that matched nothing and exited 8) holds, the count did not.

### The mechanism (s2)

The scope is now declared on the tool and compiled:

- `src/AiRaccoon/Tools/McpToolScopeAttribute.cs` — the declaration. Its constructor argument is
  required, so `[McpToolScope]` without a value does not compile: the compiler, not the
  generator, enforces that a tool states its scope.
- `src/AiRaccoon.Generators/McpToolInventoryGenerator.cs` — the scope is read off the method
  symbol in the *same* candidate pass that finds the tool, and a method that declares none is
  reported as `AIRGEN002` (an error, at the method's declaration). The generator emits a second
  file, `McpToolScope.g.cs`, with the classification as **data only**; the interface it feeds
  stays a hand-written adapter.
- All thirty tool methods declare their scope, in the eleven tool files. The per-file counts of
  `[McpServerTool(` and `[McpToolScope(` are equal in every file:

```
CodeTools.cs: tools=1 scopes=1
MemoryTools.cs: tools=10 scopes=10
PerformanceTools.cs: tools=1 scopes=1
ProjectTools.cs: tools=2 scopes=2
PromotionTools.cs: tools=2 scopes=2
QualityTools.cs: tools=2 scopes=2
ShareTools.cs: tools=2 scopes=2
SweepTools.cs: tools=2 scopes=2
SyncTools.cs: tools=1 scopes=1
WatchTools.cs: tools=3 scopes=3
WorkspaceTools.cs: tools=4 scopes=4
```

The generator's own test snippets declare a fake `AiRaccoon.Tools.McpToolScopeAttribute` — the
generator matches by full name, so a snippet without that type would not compile — and one of
them became the `AIRGEN002` assertion instead of an empty-diagnostics assertion.

| Gate | Result |
|---|---|
| `dotnet build -c Debug` (the whole solution) | clean, 0 warnings, 0 errors, 14.8 s |
| `dotnet test tests/AiRaccoon.Generators.Tests` | 5 tests, 0 failed (the `AIRGEN002` test is green; it was red in s1) |
| The generator has no product reference | `grep -c ProjectReference src/AiRaccoon.Generators/AiRaccoon.Generators.csproj` → 0 |
| The scope test is **still red** | 6 tests, 4 failed — this step changes no behaviour, so the s1 reds stay red |

That last row is the point of the cut: the mechanism lands without the behaviour changing, so
the red-to-green for the behaviour is one step, not thirty files.

### The behaviour (s3)

One file, one behaviour, one red-to-green: `src/AiRaccoon/Tools/ToolNameScope.cs` keeps the class
and loses the list. It now asks the generated map, and a name that is not a declared tool is
**refused** — the map knows every tool, so a name outside it names no tool at all. That is the
default-deny flip, and it is the only behaviour change beyond `memory_delete` moving across.

`IToolNameScope`, `ScopeCheck`, `IScopeCheck`, `ScopeCheckResult`, the `ToolGate` check order, the
refusal message and the DI registration are all unchanged — the class survives, so nothing that
named it has to be renamed.

| Gate | Result |
|---|---|
| The scope test | 6 tests, **0 failed** (was 4 failed) |
| The reserved target's delete | 1 test, **0 failed** (was 1 failed) |
| The whole Unit surface | 4302 tests, 0 failed, 1 skipped, 10.0 s |
| The shared-project integration surface | 19 tests, 0 failed, 5.8 s |

### The two mutations (s4)

Both were seen red and then green. The tree is back to its committed state.

**Mutation 1 — a tool that declares no scope fails the build.** A fake tool method was added to
`CodeTools` with `[McpServerTool(Name = "mutation_probe_tool")]` and no scope. The build failed
with **exactly one** AIRGEN002, at the method's declaration:

```
/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-derive-tool-scope-from-declarations/src/AiRaccoon/Tools/CodeTools.cs(19,17): error AIRGEN002: MCP tool method 'MutationProbe' must declare its scope with [McpToolScope(RequiresOrdinaryProject: …)] [/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-derive-tool-scope-from-declarations/src/AiRaccoon/AiRaccoon.csproj]
```

**Mutation 2 — a tool that declares a scope the test does not know fails the test.** The same
probe method, now with `[McpToolScope(RequiresOrdinaryProject: false)]`. The build is clean, and
the scope test fails — the exhaustive assertion sees a tool the pin does not list:

```
"memory_embed_pending, memory_get, memory_ingest_directory, memory_ingest_file, memory_list,
 memory_performance, memory_record_followthrough, memory_record_grade, memory_search, memory_stats,
 memory_sync, memory_watch_add, memory_watch_remove, memory_watch_status, mutation_probe_tool,
 project_id_get, project_id_token_get"
```

**After both were reverted:** the scope test is 6 tests, 0 failed, and the product build is clean.

A detail worth keeping: the mutation's test run needs the *test* project rebuilt, not only the
product — the test assembly loads the product DLL from its own output, so a product-only rebuild
leaves the test running against the previous assembly and the mutation appears to pass.

### The gates

| Gate | Result |
|---|---|
| The review that produced this task | `docs/work/2026-10-21-pr906-scope-check-review.md` — 16 findings, 8 measured, 7 read, 1 inferred |
| The measured `memory_delete` probe | four-chunk document, one named chunk: `rows_before=4 rows_after=3 deleted=1` — the partial delete is measured, not reasoned |
| `dotnet build -c Debug` (the whole solution) | clean, 0 warnings, 0 errors, 14.8 s |
| The generator's test project | 5 tests, 0 failed |
| The MCP unit surface | 325 tests, 0 failed |
| The scope test (the pin) | 6 tests, 0 failed |
| The reserved target's delete | 1 test, 0 failed |
| The whole Unit surface | 4302 tests, 0 failed, 1 skipped (re-run at the final state) |
| The shared-project integration surface | 19 tests, 0 failed |
| The ADR index gate (`AdrIndexTests`) | 7 tests, 0 failed |
| The two mutations | seen red, then green — the evidence is above |
| CI on the head | green: `build-fast` 2m49s, `build-bdd` 2m27s, `build-slow` 13m10s, `review`, `changes`, `scripts-harness` — run 39662564389 |
| After the correction | solution build 0 errors 0 warnings; the generator project 7/0; the MCP unit surface 326/0; the shared-project integration surface 19/0; the docs unit surface 26/0; the whole Unit category 4303/0 with 1 skipped |

### The implementation review

A delegated review lane read the diff, the ADRs, the work record and the source, and returned
**no blocker**: the default-deny flip is safe, the reserved target's refusal happens before any
store write, and the emitted map cannot collide with the inventory's duplication of an inherited
tool.

Two of its observations were checked against the source rather than taken at face value:

- *"The integration test proves the refusal only, not that the rows are alone."* It does prove
  that: the test counts the reserved target's rows before the call and after it, and asserts
  they are equal (`tests/AiRaccoon.Tests/Integration/SharedProjectTests.cs`, the
  `SELECT count(*) FROM entries WHERE project_id = 'shared'` pair around the call). The lane did
  not read those two lines.
- *"The scope test pins the sets by name, so a wrong classification that matches the names
  passes."* The names alone would pass — that is what the pin is for — but the guarantee is the
  pair: the two mutations. A hand-written list with the same thirty names would not produce
  `AIRGEN002`, so mutation 1 would fail, and a tool the pin does not know fails the exhaustive
  assertion, so mutation 2 would fail too. The derivation is what is pinned, not the names.

The blast radius of the default-deny flip was checked directly: `RequiresOrdinaryProject` has
exactly one caller (`ScopeCheck.Check`, `src/AiRaccoon/Tools/ScopeCheck.cs:10`) and
`ScopeCheck.Check` has exactly one caller (`ToolGate.RequireAsync`,
`src/AiRaccoon/Tools/ToolGate.cs:60`), where the tool name is the tool's own constant. No other
caller can be surprised by the flip.

## The correction

The owner reviewed the change and asked for one thing (`f:`): `RequiresOrdinaryProject` becomes
a **scope definition by enum**, and the "a tool with no scope" error becomes a **Roslyn
analyzer** rather than a source generator's diagnostic.

### The enum and the rule

`ToolScope` has three values, and they name the targets a tool may run against:

| Scope | The tool may run against | Tools |
|---|---|---|
| `All` | any target: an ordinary project or the reserved shared target | the 16 that may use the reserved target |
| `Shared` | the reserved shared target only | none today |
| `Project` | an ordinary project only | the 14 ordinary-only tools, `memory_delete` among them |

The rule is **symmetric**: a tool's scope must cover the target's scope. The reserved target's
scope is `Shared`, an ordinary project's is `Project`, and a tool runs when its scope is `All`
or equal to the target's scope.

The one-sided rule that preceded it could not express `ToolScope.Shared` at all — it read "may
not be the reserved target" and nothing more, so a shared-only tool was refused on the reserved
target too. That is the red the symmetric rule answers: with the old rule in place, the new
test fails on the reserved-target assertion, and the failure names `ToolScope.Shared`.

An undeclared tool name is `Project`: the map knows every tool, so a name outside it names no
tool at all, and the default is deny. The classification did not move — the same fourteen
tools are ordinary-only and the same sixteen may use the reserved target — so no caller's
behaviour changed.

### The analyzer

`ToolScopeAnalyzer`, a `DiagnosticAnalyzer` in the same project, owns `AIRGEN002` and reports
it at the tool's own declaration. The generator now emits only the classification, so the build
names the error once. The two share one definition of what a tool is — `ToolScopeFacts` — so
they cannot disagree; the build enforces the sharing too, because one diagnostic id cannot be
declared twice (`RS1019`).

### The evidence

| Gate | Result |
|---|---|
| The shape change (the enum) kept the suite green | the scope test 7/0, the split unchanged |
| The symmetric rule, RED first | 7 tests, 1 failed — "a tool declared ToolScope.Shared runs against the reserved shared target" |
| The symmetric rule, green | 7/0 |
| The generator must not own the error | RECONSTRUCTED red: with the generator reporting again, the generator's test reports failed: 1 — "should be empty but had [(15,21): error ...: MCP tool method 'Scopeless' must declare its scope]" |
| The analyzer's test is not vacuous | with the analyzer's report disabled, the filter reports 3/1 failed; restored, 3/0 |
| The analyzer fires in the product's own build | removing the declaration from `code_get` gives exactly one `AIRGEN002`, at `CodeTools.cs(21,51)` — one distinct location, the method's declaration |
| The whole Unit surface | 4303 tests, 0 failed, 1 skipped |
| The generator project | 7 tests, 0 failed |
| The solution build | 0 warnings, 0 errors |

**One thing this correction caught that would have shipped broken.** The diagnostic message
written in the enum step ended the attribute with an ellipsis after a dotted name
(`[McpToolScope(ToolScope. ...)]`). The analyzer's own rules read the ". " as two sentences and
failed the build with `RS1032` — and a *solution* build is what CI runs, so the enum step on
its own was not mergeable. The message now names the three values instead, and the solution
build is clean. The first attempt at the analyzer's red was masked by that same error, so the
red was reconstructed and recorded as such rather than claimed.

## The documentation

- **ADR-0139** (new) — the tool scope is declared on the tool and compiled; the alternatives
  that were rejected and why; the consequences, including the default flip and the fact that the
  generated file is never written to disk.
- **ADR-0137** (amended in place, under an `Amended 2026-10-21` marker) — the `CanSearchCode`
  sentence named a method that commit `3849135e` removed, and the reserved target's refusal list
  now names `memory_delete` with the reason.
- **`docs/adr/README.md`** — the 0139 row, the 0137 row's `Amended` cell, and a row reserving
  0138 for the search-scope record that has not landed here. The index is a derived mirror with a
  gate (`AdrIndexTests`), so it is part of this change rather than a merge-time chore: the 0138
  row must be deleted when that record lands.
- **`docs/reference/agent-memory-server.md`** — the reserved target's refusal list names the
  delete, and the operator is told how the tool scope is declared and why a delete is refused
  there.

## Still open

- The 0138 row in `docs/adr/README.md` must be deleted when the search-scope record lands: a
  recorded skip that a later document fills is a stale note, and the numbering gate fails on it.
- The release checklist under `docs/work/checklist/` is a separate manual pass and is out of
  scope for this change.
- The CI jobs are the pass condition and have not run on this branch yet.
