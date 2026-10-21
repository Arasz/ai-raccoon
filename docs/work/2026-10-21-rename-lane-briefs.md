# Lane briefs — wave 1 of the rename (drafted while the last review lanes finish)

Each lane below follows `.agents/skills/task/references/lane-dispatch-brief.md`. They are drafted,
not dispatched: the plan revision (2) that splits the rename step is what creates them, and the
review findings on gates and greps land in them first.

## Shared rules for every lane in this task

- **Workspace:** `/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-rename-mcp-tools-entry-types`,
  branch `task/air-rename-mcp-tools-entry-types`. Use absolute paths; the working directory does
  not persist.
- **Version: 2.0.0**, assigned at dispatch. Do not pick your own — concurrent lanes edit the
  version marker and the changelog index.
- **TDD:** the failing test comes first, and its RED output is pasted into the report. A gate that
  has never been seen red is not a gate.
- **Do not touch** files another lane owns (see each lane's file list). If the work needs a file
  you do not own, stop and report it.
- **Every `[McpServerTool]` method keeps its `[McpToolScope]` declaration.** A method without one
  is a build error (ADR-0139, AIRGEN002).
- **No aliases.** A renamed tool is gone; do not leave a shim, an alias table or a deprecated
  duplicate behind.
- **Model note:** the harness refuses the personas' bare model aliases (`opus`, `sonnet`), so
  every lane in this task runs on the session's model. Panel independence is by persona and angle
  only — say so in your report if it affects your confidence.

## Lane R1 — rename the read and query family

**Task.** Rename the query and read tools in `MemoryTools.cs` and `CodeTools.cs` to their settled
names, with no aliases.

**The finding.** The MCP tool surface still carries the pre-2.0.0 names
(`memory_search`, `memory_get`, `memory_list`, `memory_stats`, `memory_performance`,
`memory_record_followthrough`, `memory_record_grade`, `code_get`). The owner has settled the new
names — see `docs/plans/2026-10-21-mcp-tool-surface-rename.md`. **Verify the list before editing
it**; that document is the source of truth, and it was corrected twice.

**The recommendation.** Rename the `Tn*` constant and the `[McpServerTool(Name = ...)]` value
together, and update the tool's own `Description` text where it names another tool. Do not change
any behaviour, parameter or return shape in this lane — the entry-type parameter is a later step.

**Acceptance criteria.**
1. Every tool in `MemoryTools.cs` and `CodeTools.cs` carries its settled name, and the method
   that hosted `code_get` now hosts `get_entry` (the merged read — the entry-type parameter that
   completes the merge lands in a later step; this lane only renames it).
2. The project compiles: `dotnet build` (warnings as errors).
3. No `[McpServerTool]` method lost its `[McpToolScope]` declaration: `dotnet build` is green,
   which is what proves it — a missing scope is a build error.
4. No old name survives in the two files you own.

**Files you own.** `src/AiRaccoon/Tools/MemoryTools.cs`, `src/AiRaccoon/Tools/CodeTools.cs`.
**Files you must not touch:** every other file under `src/AiRaccoon/Tools/`, `tests/**`,
`docs/**`, `scripts/**` — lane R3 owns the pins and the documentation, and lane R2 owns the
other tool files.

**Report.** The list of names you changed (old → new, with path:line), the RED output of the
build check before and after, what you rejected, and anything you found that the plan got wrong.

## Lane R2 — rename the write, admin and telemetry families

**Task.** Rename the remaining MCP tools to their settled names, with no aliases.

**The finding.** Same as R1, for the other tool files: `PromotionTools.cs`, `ProjectTools.cs`,
`QualityTools.cs`, `PerformanceTools.cs`, `ShareTools.cs`, `SweepTools.cs`, `SyncTools.cs`,
`WatchTools.cs`, `WorkspaceTools.cs`. The settled names are in
`docs/plans/2026-10-21-mcp-tool-surface-rename.md` — **verify the list before editing it**; it
was corrected twice (the sharing, statistics, workspace, cloud and project-id families all moved).

**The recommendation.** Rename the `Tn*` constant and the `[McpServerTool(Name = ...)]` value
together; update the tool's own `Description` text where it names another tool (several of these
tools name `memory_search` in their text). Do not change behaviour: the TTL rule
(`set_memory_ttl` refusing File entries) is a later step, and the quality writes are already
split in the naming, so `memory_record_followthrough` becomes `set_memory_usage_record` and
`memory_record_grade` becomes `set_query_result_grade` — two methods, not one.

**Acceptance criteria.**
1. Every tool in the files you own carries its settled name.
2. `dotnet build` is green (warnings as errors) — which also proves no method lost its
   `[McpToolScope]` declaration.
3. The two quality tools are two methods with two names, not one merged method.
4. No old name survives in the files you own.

**Files you own.** `src/AiRaccoon/Tools/PromotionTools.cs`, `ProjectTools.cs`,
`QualityTools.cs`, `PerformanceTools.cs`, `ShareTools.cs`, `SweepTools.cs`, `SyncTools.cs`,
`WatchTools.cs`, `WorkspaceTools.cs`.
**Files you must not touch:** `src/AiRaccoon/Tools/MemoryTools.cs`, `CodeTools.cs` (lane R1);
`tests/**`, `docs/**`, `scripts/**` (lane R3).

**Report.** As R1.

## Lane R3 — move every pin, doc and harness call to the new names

**Task.** Update every place that names an MCP tool — the test pins, the BDD step lists, the
reference documentation, the how-to pages and the Python evaluation harness — so that no old name
survives anywhere in the repository.

**The finding.** The names are pinned in several places at once: the contract test
(`tests/AiRaccoon.Tests/Unit/Mcp/McpToolContractTests.cs`), the inventory test
(`ToolInventoryTests.cs`), the scope test's two name lists (`ScopeCheckTests.cs` — these pin the
*scope* of each tool, so the rename moves them without changing what they assert), the BDD step
lists under `tests/AiRaccoon.Tests/BDD/`, `docs/reference/agent-memory-server.md`,
`docs/how-to/**`, and `scripts/src/retrieval_tuning/mcp.py` (the harness speaks MCP by tool
name). **Verify each location yourself** — a `git grep` for the old names is the starting point,
not the ending point: the pins also live in prose and in tool description strings.

**The recommendation.** Update names without changing what is asserted: a contract test that
pinned `memory_search` now pins `query`, and a test that pinned behaviour stays as it is. The
"no old name survives" criterion is the lane's own gate, and it must be honest — the grep
pattern has to match every old name, including the `memory_workspace_*` and `memory_watch_*`
prefix families, and it has to cover `docs/`, `scripts/`, `tests/` and `src/`. It must exclude
only the two documents that legitimately quote the old names (`docs/plans/2026-10-21-mcp-tool-surface-rename.md`
and `docs/work/2026-10-21-tool-surface-rename-and-entry-types.md`).

**Acceptance criteria.**
1. `dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj` passes.
2. The rename-completeness grep returns nothing outside the two excluded documents — paste the
   command and its (empty) output.
3. The scope test still asserts the same *scopes* for the same tools: the two name lists in
   `ScopeCheckTests.cs` are updated, and the reserved shared target still refuses the same
   ordinary-only set (ADR-0137, ADR-0139).
4. The Python harness calls the new names and still parses the tool list it asks for.

**Files you own.** `tests/**`, `docs/reference/**`, `docs/how-to/**`, `scripts/**`.
**Files you must not touch:** `src/AiRaccoon/Tools/**` (lanes R1 and R2).

**Report.** As R1, plus the list of every location you changed with path:line, and the paste of
the empty grep output that proves completeness.
