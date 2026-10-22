# Research: the reserved `shared` target's tool surface, and the names of the query tools

**Date:** 2026-10-21
**Question:** Is it really true that no shared tool exists, and what are the tools named for query?

**Subject:** the working tree of `main` at `cbb7ad8c` (VERSION 1.63.0), after the ai-badger
refresh. The question came from a "Still open" line in the previous session's record:
"`ToolScope.Shared` has no user yet — it is there so the shared-only tool ADR-0137 defers can be
written without reshaping the rule."
**Machine:** Apple M4, 24 GB, macOS 27.0.1 arm64, .NET SDK 10.0.402.

## Findings

### F1 — No MCP tool declares `ToolScope.Shared`; the generated map holds zero Shared entries [MEASURED]

The tool surface is 30 tools, and every one of them declares either `All` or `Project`. Not a
single declaration reads `ToolScope.Shared`, in production code. The only `ToolScope.Shared` in
the repository is a test fake (`FixedScope("shared_only_tool", ToolScope.Shared)`) that exercises
the third value of the rule.

So the sentence in ADR-0139 is true as written — but it is a claim about *declarations*, not
about the shared target being unused (see F4 and F5).

**Evidence:** a throwaway console probe (`/tmp/scope-probe3`, `dotnet run -c Debug`, deleted
afterwards) that project-references `src/AiRaccoon.Core/AiRaccoon.Core.csproj` and
`src/AiRaccoon/AiRaccoon.csproj`, constructs the production `new ScopeCheck(new ToolNameScope(),
new SharedProjectScope())`, and prints every name in `McpToolInventory.Names()` with its declared
scope and its verdict for the targets `shared` and `acme`. Output: `tools=30`, and the printed
scope column contains 16 `All`, 14 `Project`, 0 `Shared`. The same split is pinned by
`tests/AiRaccoon.Tests/Unit/Mcp/ScopeCheckTests.cs:20-59`; that suite passes —
`dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --filter "FullyQualifiedName~ScopeCheckTests"`
reported `total: 7, failed: 0, succeeded: 7` on this machine.

```chart:bars
title: MCP tools by declared scope (30 tools, main @ cbb7ad8c)
All: 16
Project: 14
Shared: 0
```

### F2 — Sixteen of the thirty tools do run against the reserved `shared` target [MEASURED]

Allowed on `shared` (all declared `All`): `memory_get`, `memory_search`, `memory_list`,
`memory_stats`, `memory_ingest_file`, `memory_ingest_directory`, `memory_embed_pending`,
`memory_performance`, `memory_record_followthrough`, `memory_record_grade`, `memory_sync`,
`memory_watch_add`, `memory_watch_status`, `memory_watch_remove`, `project_id_get`,
`project_id_token_get`.

Refused on `shared` (all declared `Project`): `code_get`, `memory_delete`, `memory_delete_context`,
`memory_promotion_list`, `memory_promotion_discard`, `memory_share`, `memory_share_extract`,
`memory_sweep`, `memory_set_ttl`, `memory_workspace_begin`, `memory_workspace_status`,
`memory_workspace_consolidate`, `memory_workspace_discard`, `memory_write`.

Every one of the thirty is allowed on an ordinary project. `memory_performance` and the two
`project_id_*` tools reach the shared target vacuously: they call the migration check only, so
the scope check never runs for them.

**Evidence:** the same `/tmp/scope-probe3` run as F1 — the `shared=` column reads `allow` for the
sixteen names above and `refuse` for the fourteen; the `acme=` column reads `allow` for all
thirty.

### F3 — The rule is symmetric coverage, and it is the scope check that decides [READ]

`ScopeCheck.Check` computes the tool's declared scope and the target's scope (the reserved id is
`Shared`, an ordinary project is `Project`), and allows the call when the tool's scope is `All`
or equal to the target's scope. The check is the last of the gate's refusals, after access mode,
retired-id and registration.

**Evidence:** `src/AiRaccoon/Tools/ScopeCheck.cs:9-14`; the gate's order at
`src/AiRaccoon/Tools/ToolGate.cs:44-64`; the reserved id at
`src/AiRaccoon.Core/Projects/SharedProjectScope.cs:9,15,18`.

### F4 — "ToolScope.Shared has no user today" describes the enum, not the shared target [READ]

ADR-0139 states it as a consequence of the enum having three values: `Shared` is there so that the
shared-only tool ADR-0137 defers can be written later without changing the shape of the rule.
ADR-0137 defers exactly that tool — "a future owned-only shared query, shared workspace workflow
or document extraction format requires a separate contract". The shared target itself is a live
target: it is the reserved id `shared`, it owns ingested documents, it is watched, and searches
read it.

**Evidence:** `docs/adr/0139-attribute-derived-tool-scope.md:103-105` and `:28-31`;
`docs/adr/0137-shared-target-for-document-watches.md:68-69`;
`src/AiRaccoon.Core/Projects/SharedProjectScope.cs:9`.

### F5 — `memory_search` reads the shared target directly, under a second guard of its own [READ]

The `memory_search` description says `projectId=shared` directly reads shared documents and
promoted knowledge (`scope all/shared` only, without labels or workspaces). The shared target
therefore has a query path today, with the scope check only as its first gate: a separate
domain rule refuses a project-only search, a workspace, or a context label against the reserved
target.

**Evidence:** `src/AiRaccoon/Tools/MemoryTools.cs:106-130` (tool description and the scope
parsing at `:206-242`); the domain rule at
`src/AiRaccoon.Core/Projects/SharedProjectScope.cs:26-28` and
`src/AiRaccoon.Core/Projects/SharedProjectScopeExtensions.cs:11-17`.

### F6 — The tools named for query, and the scope each one carries [MEASURED]

No tool is named `query`. The query surface is spelled as `memory_search` plus the retrieval and
telemetry tools around it:

| tool name | declared scope | what it answers |
|---|---|---|
| `memory_search` | `All` | the hybrid query itself; `scope=all\|project\|shared` |
| `memory_get` | `All` | one entry by content hash |
| `memory_list` | `All` | the bank's indexed files, as a tree |
| `memory_stats` | `All` | entry counts and contexts |
| `code_get` | `Project` | one code chunk by hash |
| `memory_performance` | `All` | per-tool latency series (the query's own telemetry) |
| `memory_record_followthrough`, `memory_record_grade` | `All` | the quality rows a search writes |

The search scope parameter itself has three values: `all`, `project`, `shared`.

**Evidence:** the scope column of the `/tmp/scope-probe3` output (F1/F2); the tool names and
their declaration sites read in `src/AiRaccoon/Tools/*.cs` — `memory_search` at
`src/AiRaccoon/Tools/MemoryTools.cs:107`, `memory_get` at `:86`, `memory_list` at `:447`,
`memory_stats` at `:461`, `code_get` at `src/AiRaccoon/Tools/CodeTools.cs:19`,
`memory_performance` at `src/AiRaccoon/Tools/PerformanceTools.cs:17`, the two
`memory_record_*` at `src/AiRaccoon/Tools/QualityTools.cs:18,45`; the scope values parsed at
`src/AiRaccoon/Tools/MemoryTools.cs:206-210` and declared at
`src/AiRaccoon.Core/Memory/SearchScope.cs:4-9`.

### F7 — The deferred shared-only query has no name anywhere [READ]

Nothing in the repository names a future shared-only query tool. ADR-0137 defers the contract;
ADR-0139 reserves the scope value for it. The naming convention that a future tool would follow
is the `memory_*` family with a `Tn*` constant beside it (`private const string TnMemorySearch =
"memory_search";`), but no candidate name for a shared-only query has been proposed.

**Evidence:** `docs/adr/0137-shared-target-for-document-watches.md:68-69`;
`docs/adr/0139-attribute-derived-tool-scope.md:103-105`; the naming pattern at
`src/AiRaccoon/Tools/MemoryTools.cs:40`.

### F8 — "No shared tool" is true only in the narrow sense, and false in the ordinary one [INFERRED]

The sentence "ToolScope.Shared has no user today" is correct as a statement about the enum, and
would mislead a reader who takes it to mean that nothing can use the reserved `shared` target.
Sixteen tools can and do run against it, `memory_search` among them; what does not exist is a
tool that is *exclusively* the shared target's. This reading follows from F1 (no Shared
declaration), F2 (sixteen tools allowed on `shared`) and F4 (the ADR's own wording is about the
enum value).

## Still open

- Whether the intended shared-only query is a new tool or a mode on `memory_search` is not
  decided anywhere, and the shape of the "owned-only" contract is undefined — ADR-0137 defers it
  to a separate decision. A candidate name (`memory_shared_search`, `memory_shared_query`) is
  offered here as a question, not as a proposal; nothing in the repository chooses between them.
- The second half of the question — "what are tools named for query" — was read as *which tool
  names cover querying* (F6). If it was meant as *what would the deferred shared-only query tool
  be called*, the answer is F7: no name exists yet.
- The ADR-0138 skip row in `docs/adr/README.md` (owed to the other session) was not touched
  here; it is out of this record's scope.
