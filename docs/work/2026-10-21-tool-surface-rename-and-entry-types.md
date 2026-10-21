# Research: renaming the MCP tool surface and introducing entry types (Memory | Code | File)

**Date:** 2026-10-21
**Question:** When the ai-raccoon MCP tool surface is renamed (`memory_search` → `query`,
`memory_list` → `get_index`, `memory_get` + `code_get` → `get_entry`, `memory_performance` →
`get_tools_latency`, `memory_record_followthrough` + `memory_record_grade` →
`get_quality_report`), and `query`/`get_entry` gain an *entry type* parameter with the values
Memory | Code | File, is the distinction between an agent-added memory and an ingested file
already made in the data — and what does the rename cost?

**Subject:** `main` at `cbb7ad8c` (VERSION 1.63.0) plus the live bank
`~/.ai-raccoon/memory.db`. The question came from the user in one message with the rename map,
the entry-type parameter, and the open question about a memory/file distinction in code and data.
**Machine:** Apple M4, 24 GB, macOS 27.0.1 arm64, .NET SDK 10.0.402.

## Findings

### F1 — The rename touches six live tool names and adds two; the rest of the surface is untouched [MEASURED]

Today's surface is 30 tools (`McpToolInventory.Names()`, measured through a console probe). The
proposed map covers: `memory_search` → `query`; `memory_list` → `get_index`;
`memory_get` + `code_get` → `get_entry`; `memory_performance` → `get_tools_latency`;
`memory_record_followthrough` + `memory_record_grade` → `get_quality_report`. The write side is
"mostly unchanged", with `add_memory` and `ingest_file` named explicitly — which reads as
`memory_write` → `add_memory` and `memory_ingest_file` → `ingest_file`, but is not stated, and
leaves `memory_ingest_directory`, `memory_delete`, `memory_share`, `memory_share_extract`,
`memory_promotion_list`, `memory_promotion_discard`, `memory_sweep`, `memory_set_ttl`,
`memory_delete_context`, the four `memory_workspace_*`, the three `memory_watch_*`,
`memory_sync`, `memory_embed_pending`, `memory_list`, `memory_stats`, and the two `project_id_*`
tools unnamed.

**Evidence:** console probe over `McpToolInventory.Names()` (30 names, printed with declared
scope); the declarations live in `src/AiRaccoon/Tools/*.cs` — `MemoryTools.cs`, `CodeTools.cs`,
`PerformanceTools.cs`, `QualityTools.cs`, `ProjectTools.cs`, `PromotionTools.cs`, `ShareTools.cs`,
`SweepTools.cs`, `SyncTools.cs`, `WatchTools.cs`, `WorkspaceTools.cs`.

### F2 — The blast radius of the rename is large and mostly textual [MEASURED]

Occurrences of each old name in the working tree (`git grep -o`, excluding `docs/work` and
`docs/plans` for the first column):

| old name | code + tests | docs |
|---|---|---|
| `memory_search` | 553 | 1008 |
| `memory_write` | 393 | 698 |
| `memory_get` | 72 | 262 |
| `memory_ingest_file` | 74 | 211 |
| `memory_ingest_directory` | 57 | 143 |
| `memory_performance` | 45 | 143 |
| `code_get` | 35 | 159 |
| `memory_list` | 32 | 64 |
| `memory_record_grade` | 24 | 50 |
| `memory_record_followthrough` | 21 | 46 |

Most of the code-side count is the tool's own `Description` text and doc comments rather than
call sites. The Python evaluation harness speaks the MCP protocol by tool name
(`scripts/src/retrieval_tuning/mcp.py`), and the contract test pins the surface
(`tests/AiRaccoon.Tests/Unit/Mcp/McpToolContractTests.cs`, `ToolInventoryTests.cs`).

**Evidence:** `git grep -o "\b<name>\b" -- . ':!docs/work' ':!docs/plans'` and
`git grep -o "\b<name>\b" -- 'docs/**'` on `main` at `cbb7ad8c`; the harness call at
`scripts/src/retrieval_tuning/mcp.py:146`; the pinned surface at
`tests/AiRaccoon.Tests/Unit/Mcp/ToolInventoryTests.cs:32-60`.

### F3 — The data already separates agent-added memories from ingested files, exactly [MEASURED]

The `entries` table holds both, and the live bank partitions with **no ambiguous rows**:

| population | rows | signature |
|---|---|---|
| agent-added memory ("manual") | 214 | `source_file` NULL, `total_chunks = 0` |
| ingested document chunks | 25,089 | `source_file` set, `total_chunks > 0` |
| both `source_file` set *and* not chunked | **0** | — |
| neither (`source_file` NULL *and* chunked) | **0** | — |

Total `entries` = 25,303. `scope` splits as project 24,916 / custom 204 / shared 168 / NULL 15
(workspace rows). Agent-written rows carry a synthetic `path` (`<sha256>.md`) and no
`source_file`; 22,944 of the file rows have `path = source_file`.

**Evidence:** `sqlite3 "file:/Users/arasz/.ai-raccoon/memory.db?mode=ro"` aggregate queries over
`entries` (read-only, live bank, 2026-10-21), cross-tabulated on
`(source_file IS NULL)` × `(total_chunks > 0)`; row samples inspected for the `manual`
population.

### F4 — A provenance column already carries the same split, with a third value [READ]

`memory_source.source_type` is `CHECK(source_type IN ('file','transcript','manual'))`, and every
entry row joins to one through `entries.source_id` (0 rows with a null `source_id`). On the live
bank the join yields file 23,593 / transcript 1,496 / manual 214 — the same partition as F3,
split further by the transcript case. `SourceType` is a domain enum
(`File = 0, Transcript = 1, Manual = 2`), and the write path classifies a null `source_file` as
`Manual`, a `hermes/`-prefixed one as `Transcript`, anything else as `File`.

So "agent-added versus ingested" is already a **first-class provenance value**, reachable by a
join on `source_id`, and it needs no schema change to be exposed as an entry type.

**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:411-420` (table and the
`source_type` CHECK);
`src/AiRaccoon.Core/Memory/SourceType.cs:4-9`;
`src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs:859-874` (the classifier);
the join counts from the same read-only sqlite3 session as F3.

### F5 — Code is already a separate corpus, so only the Memory/File split needs a new lever [READ]

Code chunks live in their own table, `code_entries`, with its own FTS5 and vec0 indexes, their
own unique index, and no `scope`/`workspace_id`/`rating`/`ttl_days` columns — "a second, code-only
corpus alongside entries". The live bank holds 72,290 `code_entries` rows. "Code" therefore
needs no new discriminator: it is a different table.

**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:526-542` (the table and the
comment above it); `count(*) FROM code_entries` on the live bank (72,290).

### F6 — Today's `kind` splits by corpus, so the new entry type changes what `memory` means [READ]

`SearchKind` is `Memory | Code | Both`, and the dispatcher runs the **memory leg** (the `entries`
table) whenever kind ≠ Code, and the **code leg** (`code_entries`) whenever kind ≠ Memory. The
memory leg today returns *both* agent-written notes and ingested document chunks — the two
populations of F3 are indistinguishable to it. So the current `kind=memory` is "the entries
table", and under the proposed taxonomy `Memory` alone is "the manual rows only"; the ingested
chunks would move into the new `File` type.

That is a semantic break, not a rename: `kind=memory` today answers a question that
`type=Memory` would not. It also means `File` is not a new capability — the rows are already
searched — but a new *label* on an existing population.

**Evidence:** `src/AiRaccoon.Core/Memory/SearchKind.cs:4-9`;
`src/AiRaccoon.Core/Memory/SearchDispatcher.cs:19-45`;
`src/AiRaccoon/Tools/MemoryTools.cs:196,213,267-273` (the `kind` parameter and its parse).

### F7 — Nothing in the surface aliases a renamed tool today [READ]

The MCP server registers exactly the `[McpServerTool]` methods of the assembly, and the tool
inventory is generated from them; there is no alias table, no deprecation shim, and no
version-negotiated name mapping. A rename is therefore a hard break for every MCP client
(Claude Code, pi, the Python harness, any agent configured against this server), and the
project's own tool inventory, contract test, and the `docs/reference/agent-memory-server.md`
reference page all pin the current names.

The CLI is a separate surface with its own verbs (`ai-raccoon settings`, `watch`, `sync`,
`doctor`), so it is not coupled to the MCP tool names.

**Evidence:** `src/AiRaccoon/Tools/McpToolInventory.cs`;
`src/AiRaccoon.Generators/McpToolInventoryGenerator.cs:120-156`;
`tests/AiRaccoon.Tests/Unit/Mcp/McpToolContractTests.cs:21-45`;
`src/AiRaccoon/Setup/Cli/Commands/CommandsRegistration.cs`.

### F8 — The natural vocabulary for the third type already exists, with one wrinkle [INFERRED]

If entry types are `Memory | Code | File`, the mapping onto the data is: `Code` →
`code_entries`; `File` → `entries` rows whose `source_type` is `file`; `Memory` → `entries` rows
whose `source_type` is `manual`. The wrinkle is the 1,496 `transcript` rows: they are
agent-supplied notes sourced from a transcript file, so they are `manual` by origin but `file` by
locator. Whether they count as `Memory` or as `File` is a decision the taxonomy has to make, and
it changes what `type=Memory` returns by 1,496 rows on this bank (214 → 1,710, or 214 → 214).

This follows from F3 (the partition), F4 (the `source_type` values) and the observed
transcript count.

## Still open

- **Hard rename, or rename with deprecated aliases?** F7 shows there is no aliasing mechanism
  today. A hard rename breaks every existing client; an alias layer is new machinery, and the
  project has a "no hand-maintained mirror" rule (ADR-0139) that would apply to it. Deciding
  this changes the size of the change by an order of magnitude.
- **Does `File` include transcripts?** F8's wrinkle — 1,496 rows on this bank.
- **What happens to the unnamed tools?** F1's list — `memory_ingest_directory`,
  `memory_delete`, the `memory_share_*`, `memory_promotion_*`, `memory_sweep`,
  `memory_set_ttl`, `memory_delete_context`, `memory_workspace_*`, `memory_watch_*`,
  `memory_sync`, `memory_embed_pending`, `memory_stats`, `project_id_*` — and whether the new
  names deliberately mix families (`query`, `get_index`, `get_entry`, `add_memory`,
  `ingest_file` are verbs/nouns without the `memory_` prefix while the untouched tools keep it).
- **Is `get_quality_report` a report or a recorder?** It replaces two *writing* tools
  (`memory_record_followthrough`, `memory_record_grade`); a "report" tool that also records
  changes its contract, and the two inputs (which file was read, what grade) do not obviously
  become one report call.
- **Does `query`'s default change meaning?** Under the new taxonomy the default is
  Memory | Code | File, which is today's `both` — but today's `memory` leg contained File rows,
  so `type=Memory` alone is a *narrower* result set than today's `kind=memory`.
- Whether the entry-type parameter is a bit-flag enum (sum-able, as the user described) or three
  booleans, and how it renders on the wire for the MCP SDK.
