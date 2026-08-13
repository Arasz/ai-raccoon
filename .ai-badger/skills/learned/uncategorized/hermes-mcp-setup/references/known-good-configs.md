# Known-good MCP server configurations for Hermes

Configs verified working as of 2026-07-22. Add with `yes | hermes mcp add <name> ...`.

## Rider (JetBrains IDE) — 42 tools

Source: Rider's built-in MCP server plugin. Requires Rider running with the MCP server plugin enabled.

```bash
yes | hermes mcp add rider --url http://127.0.0.1:64482/stream
```

Port: Rider assigns local ports (e.g. `64482` or `64342`). Omitting project-path headers (`IJ_MCP_SERVER_PROJECT_PATH`) makes the Hermes configuration project-agnostic across all repositories opened in Rider — Rider automatically routes tool calls to the active solution window.

Tools: build_solution, search_symbol, get_file_problems, rename_refactoring,
execute_run_configuration, execute_sql_query, get_solution_projects,
get_project_dependencies, reformat_file, open_file_in_editor, and 32 more.

## Microsoft Docs — 3 tools

Source: Copilot config (`~/.copilot/mcp-config.json`) and https://learn.microsoft.com/api/mcp.

```bash
yes | hermes mcp add microsoft-docs --url https://learn.microsoft.com/api/mcp
```

Tools: microsoft_docs_search, microsoft_code_sample_search, microsoft_docs_fetch.

## Playwright — 24 tools

Source: https://mcpservers.org/servers/playwright-mcp-server. Requires Node.js 18+.

```bash
yes | hermes mcp add playwright --command npx --args "@playwright/mcp@latest"
```

Tools: browser_navigate, browser_click, browser_type, browser_snapshot,
browser_take_screenshot, browser_network_requests, browser_tabs, and 17 more.

## .NET SDK — 12 tools

Source: https://github.com/jongalloway/dotnet-mcp. Install first:

```bash
dotnet tool install -g community.mcp.dotnet
export PATH="$PATH:$HOME/.dotnet/tools"
```

Then add:

```bash
yes | hermes mcp add dotnet-sdk --command dotnet-mcp
```

Tools: dotnet_project, dotnet_package, dotnet_sdk, dotnet_ef, dotnet_tool,
dotnet_build, dotnet_test, dotnet_server_capabilities, and 4 more.

NOTE: `dnx` is NOT the MCP server. It's `dotnet-exec`, a temporary tool runner.
Install `community.mcp.dotnet` for the actual `dotnet-mcp` command.

## Glider — 49 tools

Source: https://glidermcp.com/glider. Roslyn-powered C# semantic analysis.
Requires .NET 10 SDK. Install first:

```bash
dotnet tool install -g glider
```

Then add:

```bash
yes | hermes mcp add glider --command glider
```

Tools: load_solution, find_symbol, get_symbol, get_symbol_references,
get_type_hierarchy, get_call_graph, get_diagnostics, rename_symbol,
extract_method, format_document, get_nuget_packages, and 39 more.

## GliderTrace — 21 tools

Source: https://glidermcp.com/glider-trace. Runtime evidence for .NET.
Requires .NET 10 SDK. Install first:

```bash
dotnet tool install -g glider-trace
```

Then add:

```bash
yes | hermes mcp add glider-trace --command glider-trace
```

Tools: trace_run, trace_test, trace_benchmark, get_exceptions, get_counters,
get_traces, get_artifacts, get_code_coverage, start_profile, stop_profile,
and 11 more.

## Servers that DON'T work with Hermes

| Server | Source | Why not |
|---|---|---|
| Claude Superpowers | `https://claude.com/plugins/superpowers` | Claude-specific plugin URL, returns HTML |
| Claude OAuth servers | `~/.claude/mcp-needs-auth-cache.json` | Claude internal OAuth, not portable |
| Angular CLI (IntelliJ) | `~/.config/github-copilot/intellij/mcp.json` | JetBrains-specific, not generic MCP |
| LLM Studio (local) | `http://127.0.0.1:1235` | LM Studio-specific, not a standard MCP endpoint |
