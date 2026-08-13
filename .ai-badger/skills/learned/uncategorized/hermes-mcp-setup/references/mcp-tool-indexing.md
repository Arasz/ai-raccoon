# MCP Tool Indexing — Better Agent Tool Selection

When a project has 40+ MCP tools (e.g., Rider's 42 tools), the agent wastes tokens scanning all definitions and can pick the wrong tool due to overlapping names. A tool index maps each tool to tags (for filtering) and an intent (for semantic disambiguation), then the `pre_llm_call` hook injects context hints steering the agent to the right tools.

## Index format (`.ai-badger/mcp-tools.yaml`)

```yaml
version: "0.1.0"
generated_at: "2026-07-22T15:15:00Z"
sources:
  - name: rider
    url: http://127.0.0.1:64342/stream
    tools:
      search_symbol:
        tags: [semantic, search, csharp, typescript, navigation]
        intent: "Find a class, method, or field by name fragment using semantic lookup"
      get_file_problems:
        tags: [diagnostic, csharp, typescript]
        intent: "Check a file for Rider code analysis errors, warnings, and suggestions"
      build_solution:
        tags: [dotnet, build, csharp]
        intent: "Compile the solution or specific files and return build status and errors"
```

## Tag taxonomy (closed set)

| Category | Tags |
|---|---|
| Language | `csharp`, `typescript`, `javascript`, `python`, `sql`, `css`, `html` |
| Action | `navigation`, `diagnostic`, `build`, `run`, `refactoring`, `search`, `read`, `write`, `terminal` |
| Domain | `database`, `tracing`, `opentelemetry`, `browser`, `dotnet`, `semantic`, `files` |
| Meta | `batch`, `slow`, `unsafe` |

## Semantic matching approach

Keyword extraction from the user's natural-language query → tag intersection → intent-text word overlap for disambiguation.

Proven on 42 Rider tools with 16 test queries, 100% accuracy after tuning:

| Query | Expected tool | Result |
|---|---|---|
| "Check ApplicationFunctionsTests.cs for errors" | `get_file_problems` | TOP-1 ✓ |
| "Find all references to CvTemplate" | `search_symbol` | TOP-1 ✓ |
| "Build the solution" | `build_solution` | TOP-1 ✓ |
| "Show me what files are open in the editor" | `get_all_open_file_paths` | TOP-1 ✓ |
| "What projects are in the solution?" | `get_solution_projects` | TOP-1 ✓ |
| "Show me the columns of the Applications table" | `get_database_object_description` | TOP-1 ✓ |
| "Rename the class UserService to AccountService" | `rename_refactoring` | TOP-1 ✓ |

## Hook integration

The `pre_llm_call` hook in `ai_badger_hooks.py` loads the index, extracts keywords from the user's message, and injects a compact hint:

```
[ai-badger] Relevant MCP tools: rider:get_file_problems (diagnostic), rider:build_solution (build), rider:execute_run_configuration (run)
```

This steers the agent away from scanning all 42 tool definitions. Combined with `post_tool_observer` logging whether the recommended tool was actually used, the feature is self-measuring.

## Manual authoring workflow

1. List all tools for a server (e.g., from the `tools` block in the session system prompt)
2. Assign tags: tool name contains `sql`/`database` → `[database, sql]`, `build`/`solution` → `[dotnet, build]`, `search`/`symbol` → `[semantic, search]`, `problem`/`error` → `[diagnostic]`, `span`/`trace`/`log` → `[tracing, opentelemetry]`, `browser` → `[browser]`
3. Write a one-sentence intent starting with a verb: "Find...", "Check...", "Compile...", "Run...", "List..."
4. Validate: every tool has non-empty `tags` and `intent`, all tags come from the closed taxonomy

## Pitfalls

- **Index staleness**: When MCP servers change, the index goes out of date. Always re-validate after `hermes mcp add`/`remove`.
- **Tag symmetry**: Tools with identical tags (e.g., all 10 database tools share `[database, sql]`) need intent-text matching to disambiguate — raw query words matched against intent text is the key.
- **Keyword gaps**: "columns", "structure", "tree" need explicit keyword→tag mappings to work. The spike's `KEYWORD_TAG_MAP` is the starting point; extend it when a query fails.
- **Hermes can't filter tools from the prompt**: The `pre_llm_call` context injection is a steering hint, not prompt compression. The agent still sees all tool definitions; the hint just tells it where to look.

## Seeding catalog tools for a server the host listing can't describe (2026-08)

`mcp-index update` adds tools ONLY from a listing that carries tool detail.
`hermes mcp list --json` no longer exists and `claude mcp list` never carries
tools — so a newly added server lands in the index as `status: unknown` with
`tools: {}` and stays empty no matter how many times you update. That's
deliberate ("not asked ≠ exposes nothing"); the completion step is manual:

1. Check the framework catalog for the server:
   `ls <ai-badger-framework-root>/features/common/mcp/<server>/tools.json`
   (a curated `{server, tools: [{name, tags, intent}]}` file).
2. If present, seed the index directly — merge each catalog tool into the
   source's `tools` dict as `{tags, intent, origin: "catalog"}` (the exact shape
   `describe_tool` produces), preserving any existing `manual` entries:
   ```python
   for t in catalog['tools']:
       tools.setdefault(t['name'], {'tags': t['tags'], 'intent': t['intent'], 'origin': 'catalog'})
   ```
3. `mcp_index.py validate --target <project>` → `OK: N tool(s) validated`.
4. Future `update` runs re-describe `catalog`-origin entries from the catalog and
   never clobber `manual` ones — the seed is durable.

Statuses worth knowing: `unknown` = listing carried no tool detail (not an
error); `unreachable` from a `claude mcp list` health check often flips to
`unknown` once the server actually answers; `absent` = the host no longer lists
the server (its tools get marked `removed`). Also: running `mcp-index update`
invokes `claude mcp list`, which health-checks and may REWRITE the project's
`.mcp.json` (see the SKILL.md pitfall) — expect an uncommitted diff afterward.
