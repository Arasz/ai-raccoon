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

## Decision

Every `[McpServerTool]` method declares its scope, and the generator compiles the declarations
into the classification.

```csharp
[McpServerTool(Name = TnMemoryWrite)]
[McpToolScope(RequiresOrdinaryProject: true)]
public async Task<ApiEnvelope<WriteResult>> Write(...)
```

- The declaration is an attribute, `AiRaccoon.Tools.McpToolScopeAttribute`, in the product
  assembly: it describes the MCP tool surface, which the host owns — `McpToolInventory`,
  `IToolNameScope` and `ScopeCheck` all live there, and the domain assembly carries no host
  concept. The generator matches attributes by metadata name, so it needs no reference to the
  attribute's assembly and the product keeps its single project reference to the generator.
- The attribute's constructor argument is **required**. `[McpToolScope]` without a value does
  not compile, so the compiler — not the generator — enforces that a tool states its scope.
- The generator reads the attribute off the method symbol in the same candidate pass that finds
  the tool, and reports `AIRGEN002` (an error, at the method's declaration) for a tool method
  that declares none. Adding an MCP tool without deciding its scope is a build failure.
- The generator emits the classification as **data only**, in its own generated file. The
  interface it feeds stays a hand-written adapter over the generated map.
- A name that is not a declared tool is **refused** on the reserved target. The map knows every
  tool, so a name outside it names no tool at all; the default is deny.
- The prefix families are gone. Each tool declares its own scope, and a future
  `memory_workspace_x` is classified by what it does, not by how it is spelled.

`IToolNameScope`, `ScopeCheck`, the tool gate's check order and the refusal message are
unchanged: the contract the composition root already depends on stays the contract.

## Consequences

- The classification cannot drift from the tool surface. The surface is the input; the list is
  not a second copy of it.
- A new tool costs one declaration. The compiler asks the question, so the answer cannot be
  forgotten — it can only be wrong, and then it is wrong in one visible place.
- The default flipped from allow to deny for names outside the tool surface. No tool's behaviour
  changes: the gate only ever passes a declared tool's own name.
- The attribute is a product-assembly concept, and the generator keeps matching by name. A
  future generator that needed the attribute's type would need a reference the netstandard2.0
  generator project does not have.
- The generated file is never written to disk (no project sets
  `EmitCompilerGeneratedFiles`), so a test that asserts what the generator emits has to read it
  from the generator's own harness rather than from a file.

## Alternatives considered

- **Keep the hand-written list and add a drift test.** Rejected: a test that fails when the
  tool surface grows catches the omission one build later, and the list still defaults to
  allow. The decision would still live in a list rather than on the tool.
- **Compare the list with the generated inventory at runtime.** Rejected for the same reason,
  weaker: it detects the drift rather than making the decision impossible to omit.
- **Put the attribute in the domain assembly.** Rejected for layering, not for reachability: a
  host concept does not belong in the domain assembly, and the generator can see either
  assembly's attribute by name.
- **A configurable rule pipeline, one composition per client.** Rejected: there is one MCP
  surface and one client contract, and a pipeline would add configuration without a current
  behavioural difference.

## Evidence

- `src/AiRaccoon/Tools/McpToolScopeAttribute.cs` — the declaration.
- `src/AiRaccoon.Generators/McpToolInventoryGenerator.cs` — the collection, the `AIRGEN002`
  diagnostic and the generated classification.
- `src/AiRaccoon/Tools/ToolNameScope.cs` — the adapter over the generated map, and the
  default-deny rule.
- `tests/AiRaccoon.Tests/Unit/Mcp/ScopeCheckTests.cs` — the pin: the ordinary-only set, the
  shared-allowed set, the union over the tool inventory, the `memory_delete` refusal and the
  default-deny case.
- `tests/AiRaccoon.Generators.Tests/McpToolScopeGeneratorTests.cs` — the diagnostic and the
  data-only shape of the generated file.
- The review that produced this decision:
  [the scope-check review of PR #906](../work/2026-10-21-pr906-scope-check-review.md) (F8).
