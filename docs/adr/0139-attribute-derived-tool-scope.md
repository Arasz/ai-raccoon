# 0139. The MCP tool scope is declared on the tool and compiled

**This document records a decision. It does not describe the running system.**

Date: 2026-10-21

Status: Accepted

## Context

The reserved `shared` document target may only be used by the tools that are safe on it, and
ADR-0137 put that list in `ToolNameScope`: seven exact tool names plus two ordinal prefix
families (`memory_workspace_*`, `memory_promotion_*`). Every name that was not on the list was
*allowed* on the reserved target.

That is a hand-maintained mirror of the tool surface, and nothing compared the two. The
assembly already knows its own tool surface — the Roslyn generator emits the MCP tool inventory
from the `[McpServerTool]` methods — but the scope list was a second, unrelated list. A tool
added to the surface was allowed on the reserved target until somebody remembered the list, and
a list that defaults to *allow* fails open: the cost of the omission is paid by the target, not
by the tool.

The prefix families were the same problem in a smaller dress. `memory_workspace_*` was ordinary
because workspaces are ordinary; `memory_promotion_*` was ordinary because promotion is. A
future `memory_workspace_x` would have inherited the rule by spelling, and a future
`memory_promotion_x` would have inherited it too, whatever it did.

The first cut of this decision made the scope a bool — "requires an ordinary project" — which
names what the tool forbids rather than what it supports, and which has no room for a tool that
belongs to the shared tier alone. ADR-0137 defers exactly such a tool ("a future owned-only
shared query, shared workspace workflow or document extraction format requires a separate
contract"), so the shape had to make room for it.

## Decision

Every `[McpServerTool]` method declares its scope, and the generator compiles the declarations
into the classification.

```csharp
[McpServerTool(Name = TnMemoryWrite)]
[McpToolScope(ToolScope.Project)]
public async Task<ApiEnvelope<WriteResult>> Write(...)
```

### The scope is an enum

`ToolScope` has three values — `All`, `Shared`, `Project` — and they name the targets a tool
may run against:

| Scope | The tool may run against |
|---|---|
| `All` | any target: an ordinary project or the reserved shared target |
| `Shared` | the reserved shared target only |
| `Project` | an ordinary project only |

### The rule is symmetric

A tool's scope must **cover** the target's scope. The reserved `shared` target's scope is
`Shared`; an ordinary project's is `Project`. A tool runs when its scope is `All` or equal to
the target's scope.

The rule is symmetric because a scope that names one target is a claim about that target, not
only a prohibition elsewhere. The one-sided rule that preceded it could not express
`ToolScope.Shared` at all: it read "may not be the reserved target" and nothing more, so a
shared-only tool was refused on the reserved target too.

A name that is not a declared tool is `ToolScope.Project`: the map knows every tool, so a name
outside it names no tool at all, and the default is deny. No declared tool's behaviour changes
when the bool becomes the enum — the same fourteen tools are ordinary-only and the same sixteen
may use the reserved target.

### The declaration is enforced twice

- The attribute's constructor argument is **required**. `[McpToolScope]` without a value does
  not compile, so the compiler — not the generator — enforces that a tool states its scope.
- A tool method that declares no scope is a **build error**, reported by a Roslyn
  `DiagnosticAnalyzer` (`ToolScopeAnalyzer`, `AIRGEN002`) at the method's own declaration. The
  analyzer owns the error and the generator emits only the classification, so the build names it
  once.

### The two cannot disagree about what a tool is

The generator and the analyzer each need the same predicate — a public instance method, in a
class, in the `AiRaccoon.Tools` namespace, carrying the MCP tool attribute — and it lives in
one place, `ToolScopeFacts`, that both call. The build enforces the sharing as well: one
diagnostic id cannot be declared twice (`RS1019`).

The attribute lives in the product assembly because it describes the MCP tool surface, which
the host owns — `McpToolInventory`, `IToolNameScope` and `ScopeCheck` all live there, and the
domain assembly carries no host concept. The generator matches attributes by metadata name, so it
needs no reference to the attribute's assembly and the product keeps its single project
reference to the generator.

`IToolNameScope`, `ScopeCheck`, the tool gate's check order and the refusal message are
unchanged: the contract the composition root already depends on stays the contract.

## Consequences

- The classification cannot drift from the tool surface. The surface is the input; the list is
  not a second copy of it.
- A new tool costs one declaration. The compiler asks the question, so the answer cannot be
  forgotten — it can only be wrong, and then it is wrong in one visible place.
- `ToolScope.Shared` has no user today. It is there so that the shared-only tool ADR-0137
  defers can be written without changing the shape, and the rule that honours it is tested
  rather than assumed.
- The default flipped from allow to deny for names outside the tool surface. No tool's behaviour
  changes: the gate only ever passes a declared tool's own name.
- The attribute is a product-assembly concept, and the generator keeps matching by name. A
  future generator that needed the attribute's type would need a reference the netstandard2.0
  generator project does not have.
- The generated file is never written to disk (no project sets
  `EmitCompilerGeneratedFiles`), so a test that asserts what the generator emits has to read it
  from the generator's own harness rather than from a file.
- The analyzer's diagnostic message is itself under the analyzer rules: a message that reads as
  two sentences (a dotted abbreviation followed by a space) fails the build with `RS1032`.

## Alternatives considered

- **Keep the hand-written list and add a drift test.** Rejected: a test that fails when the
  tool surface grows catches the omission one build later, and the list still defaults to
  allow. The decision would still live in a list rather than on the tool.
- **Compare the list with the generated inventory at runtime.** Rejected for the same reason,
  weaker: it detects the drift rather than making the decision impossible to omit.
- **A bool — "requires an ordinary project".** Rejected: it names the prohibition rather than
  the scope, so the third value (a shared-only tool) has nowhere to live, and every reader has
  to invert it to learn what the tool may do.
- **Put the attribute in the domain assembly.** Rejected for layering, not for reachability: a
  host concept does not belong in the domain assembly, and the generator can see either
  assembly's attribute by name.
- **Keep the error in the source generator.** Rejected: the generator's job is to emit the
  classification, and an analyzer is the instrument for a rule about the code. One owner means
  one error in the build output.
- **A configurable rule pipeline, one composition per client.** Rejected: there is one MCP
  surface and one client contract, and a pipeline would add configuration without a current
  behavioural difference.

## Evidence

- `src/AiRaccoon/Tools/ToolScope.cs` — the enum.
- `src/AiRaccoon/Tools/McpToolScopeAttribute.cs` — the declaration.
- `src/AiRaccoon.Generators/ToolScopeAnalyzer.cs` — the error.
- `src/AiRaccoon.Generators/ToolScopeFacts.cs` — the shared definition of a tool.
- `src/AiRaccoon/Tools/ToolNameScope.cs` — the adapter over the generated map, and the
  default-deny rule.
- `tests/AiRaccoon.Tests/Unit/Mcp/ScopeCheckTests.cs` — the pin: the ordinary-only set, the
  shared-allowed set, the union over the tool inventory, the `memory_delete` refusal, the
  default-deny case and the shared-only case.
- `tests/AiRaccoon.Generators.Tests/McpToolScopeAnalyzerTests.cs` — the diagnostic, and the
  two cases that must stay silent.
- The review that produced this decision:
  [the scope-check review of PR #906](../work/2026-10-21-pr906-scope-check-review.md) (F8).
