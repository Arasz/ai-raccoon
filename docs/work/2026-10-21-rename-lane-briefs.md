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

---

## Wave-2 notes carried forward (written while wave 1 ran)

### The interim state has TWO `get_entry` methods — and the generator punishes that

After the rename lanes, `MemoryTools.cs` and `CodeTools.cs` each declare a method whose
`[McpServerTool(Name = ...)]` is `get_entry` — one reads the memory corpus, one the code corpus.
The generator makes that state visible in two ways (`src/AiRaccoon.Generators/McpToolInventoryGenerator.cs`):

- `names.Add(candidate.Name!)` — the emitted inventory is a **list**, so `get_entry` appears
  **twice** in `McpToolInventory.Names()`. Any count or distinctness assertion fails.
- `if (candidate.Scope is { } scope && !scopes.Any(entry => entry.Name == candidate.Name))` —
  the emitted `ScopeByTool` map keeps the **first** scope for a duplicated name and **silently
  drops the second**. `get_entry` is `All` in `MemoryTools.cs` and `Project` in `CodeTools.cs`,
  so whichever candidate the generator sees first decides the scope of the merged tool, and the
  other is lost without a warning.

**So the merge is not cosmetic.** The entry-type lane must end with exactly **one** `get_entry`
method. Its scope has to be `ToolScope.All`: the merged tool reads the memory corpus (which
includes the reserved shared target's entries) and the code corpus (which is project-scoped
only), and the entry-type parameter — not the scope — decides which corpus a call reaches.

A `shared` target call to `get_entry` with type `Code` is therefore **allowed and empty**, not
refused. That is a behaviour change from `code_get`, which was refused on the reserved target
(ADR-0137 refused code there). The entry-type lane must state this in the tool's description
and the release notes, and the ADR-0137 text must be amended to match. Flag it to the owner
rather than deciding it silently.

### What the plan's file split got wrong (and how it was corrected)

The wave was split by tool *family* but assigned files by *list*, so six write-side tools living
in `MemoryTools.cs` (`memory_write`, `memory_delete`, `memory_delete_context`,
`memory_ingest_file`, `memory_ingest_directory`, `memory_embed_pending`) were owned by nobody:
the read lane was told to do only the reads, and the write lane was told to stay out of
`MemoryTools.cs`. Correction: the read lane's file now carries those six as well (lane R1b), so
file ownership stays one-owner-per-file and no lane edits a file another lane is editing.

### The lane report is not evidence

One wave-1 lane reported "renames applied, build green, old names eliminated from 9 files" with
a RED-before/after narrative — and the branch was byte-identical afterwards, with no commit and
no old name gone. Another lane in the same wave did the work and its commit was on disk. So:
every lane in this task is verified by reading its branch's diff and running its own acceptance
checks before the step is marked complete. A report without a commit hash and a `git show --stat`
is not a report.

---

## Wave-3 briefs — the entry-type wave (drafted while wave 1 finished)

The rename wave is merged into the task branch (`ad23b594`, 31 methods, 30 distinct names —
the duplicate is `get_entry`, declared in both `MemoryTools.cs` and `CodeTools.cs`). The
entry-type wave is the heavy one: a schema change with a ladder step, a new parameter on two
tools, a TTL rule, and a stored-vocabulary migration.

### Lane E1 — the classifier (the schema change)

**Task.** Make Memory | Code | File a first-class classifier over the bank, denormalised onto
`entries`, backfilled correctly, and exposed through ONE service that the query, the get and
the TTL tool all use.

**The finding.** The data already separates the populations, but the cheap discriminator cannot
express one of them: on the live bank `entries` holds 25,303 rows — 214 `manual` (agent-added),
1,496 `transcript` (agent-authored, session-sourced) and 25,089 `file` (ingested document
chunks) — and the signature `source_file IS NULL AND total_chunks = 0` puts every transcript on
the FILE side, because a transcript carries `source_file = hermes/<timestamp>_<id>` and is
chunked. The truth is `memory_source.source_type` (`manual` | `transcript` | `file`), which the
write path already computes in `SqliteMemoryStore.ClassifySource`.

**The recommendation.** Add an `entry_type` column to `entries`, backfilled from
`source_type` by a ladder step that reuses that same rule, and maintain it on every write path.
**Evaluate this rather than executing it:** if the migration machinery in this codebase cannot
carry an `ADD COLUMN` idempotently, say so and propose the alternative the file's own comments
point at (the reprobe pattern, near `MemorySchema.cs:2076-2109`) — do not invent a third
mechanism.

**Acceptance criteria.**
1. The classifier returns Memory for a manual entry, **Memory for a transcript entry**, File for
   an ingested document chunk; Code names the `code_entries` corpus.
2. A transcript row written **before** the migration is classified Memory.
3. The backfill is idempotent and leaves no row with a NULL or unknown type.
4. Every write path sets the type: the chunk-index recompute, the watch ingest path, the
   promotion path, the shared-target rows, and the repair/reconcile path
   (`ChunkBudgetReconciler` re-inserts entries).
5. `dotnet build -warnaserror` green; the ladder step runs on a fresh bank and on a bank that
   already has the column.

### Lane E2 — the parameter on `query` and `get_entry`

**Task.** Give `query` and `get_entry` the entry-type parameter, merge the two `get_entry`
methods into one, and get the defaults and the refusals right.

**The finding.** `query` has no type parameter, so the memory leg still returns agent notes and
ingested chunks together. `get_entry` exists as TWO methods with the same wire name — the
generator lists `get_entry` twice in the inventory and keeps only the FIRST scope in its map
(`McpToolInventoryGenerator`: `names.Add(...)` is a list, and `scopes.Add(...)` is guarded by
`!scopes.Any(entry => entry.Name == candidate.Name)`), so one of the two scopes is silently
dropped today.

**The recommendation.** Merge the two `get_entry` methods into ONE, declaring
`ToolScope.All`: the merged tool reads the memory corpus (which includes the reserved shared
target) and the code corpus (project-scoped only), and the entry-type parameter — not the
scope — decides which corpus a call reaches. `query` defaults to Memory|Code|File (the sum),
which answers exactly what today's default (`kind=both`) answers. `get_entry` has NO default:
the type is required, because a hash alone does not say which corpus to read from, and a call
without it is refused with a clear message. A hash present in two corpora is refused, not
guessed. An unknown value is refused as invalid-params, never treated as the default.
**The shared-target consequence to state, not to decide silently:** a `get_entry` call on the
reserved `shared` target with type `Code` is ALLOWED and EMPTY (the old `code_get` was refused
there). Put that in the tool's description and report it.

### Lane E3 — the TTL rule

**Task.** Make a TTL a Memory-only property: `set_memory_ttl` refuses a File entry, and no
other path can put a TTL on one.

**The finding.** `ttl_days` is written in exactly two places: `MemorySql.UpdateEntryTtl`
(called from `SqliteMemoryStore`, the explicit set) and `ChunkBudgetReconciler`, which
re-inserts entries with `ttlDays = template.TtlDays` and would carry a TTL across a re-chunk.
Refusing at the tool alone leaves the reconciler open. Code entries have no `ttl_days` column
at all. Measured baseline: no row of any type carries a TTL today, so there is no legacy data
to migrate — the rule is forward-looking.

### Lane E4 — the stored vocabulary

**Task.** Widen `search_quality.kind` to the entry types and migrate the historical rows
honestly.

**The finding.** `search_quality.kind` is `CHECK(kind IN ('memory','code','both'))`
(`MemorySchema.cs:141`) with the v12 ladder step and a backfill cutoff behind ADR-0097. The
historical rows lie about themselves: a `memory` row covered agent notes AND ingested chunks,
which the new `memory` does not. This is the SECOND migration in the task — the classifier's
ladder step is v13, this one is v14, and they must not race.

**The recommendation.** Decide the vocabulary, widen the CHECK, write the backfill, and say
plainly in the ADR amendment what the backfill claims. A backfill that maps the old `memory`
to the new `memory` would be a claim the data cannot support; if that is the honest answer,
say it and pick the alternative.
