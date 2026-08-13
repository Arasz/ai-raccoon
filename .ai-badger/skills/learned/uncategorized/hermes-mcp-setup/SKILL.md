---
name: hermes-mcp-setup
description: "Discover, configure, and add MCP servers to Hermes Agent from multiple sources: Copilot configs, mcpservers.org, .NET global tools, and standard npm packages. Use when adding MCP tools for IDE integration, browser automation, documentation search, code analysis, or runtime evidence."
version: 1.0.0
author: Hermes Agent
license: MIT
platforms: [macos, linux, windows]
metadata:
  hermes:
    tags: [mcp, setup, configuration, tools, discovery, dotnet, playwright, rider]
    related_skills: [hermes-agent]
---

# Hermes MCP Server Setup

Discover and configure MCP servers for Hermes Agent. Covers finding server configs, adding them non-interactively, verifying, building complementary tool stacks, and optimizing agent tool selection with a tag+intent index.

## Discovery sources (in priority order)

### 1. Copilot MCP configs (`~/.copilot/mcp-config.json`)

Copilot stores its MCP server configs in a standard JSON file. Read it to find servers:

```bash
cat ~/.copilot/mcp-config.json | python3 -c "import sys,json; [print(f'{k}: {v.get(\"url\",v.get(\"command\",\"?\"))}') for k,v in json.load(sys.stdin).get('mcpServers',{}).items()]"
```

Copy the config to Hermes syntax. HTTP servers use `--url`, stdio servers use `--command` + `--args`.

### 2. IntelliJ/JetBrains Copilot MCP configs

```bash
cat ~/.config/github-copilot/intellij/mcp.json
```

These are often IDE-specific (e.g., Angular CLI for IntelliJ) and may not be portable.

### 3. MCP server directories (mcpservers.org, mcp.so)

Browse the server page to find the standard config. Look for the JSON config block:
```json
{"mcpServers": {"name": {"command": "npx", "args": ["@scope/package@latest"]}}}
```

Extract the command and args for `hermes mcp add --command <cmd> --args <arg1> <arg2>`.

### 4. .NET global tools

Search NuGet for MCP servers:
```bash
dotnet tool search mcp
```

Install globally:
```bash
dotnet tool install -g <package-id>
```

The command name is listed in the install output. If not on PATH:
```bash
export PATH="$PATH:$HOME/.dotnet/tools"
```

### 5. Claude MCP configs

Claude's MCP auth cache is at `~/.claude/mcp-needs-auth-cache.json`. These are Claude OAuth-authenticated and generally NOT portable to Hermes. Claude-specific plugin URLs (e.g., `https://claude.com/plugins/superpowers`) return HTML, not MCP responses — skip these.

## Adding servers non-interactively

`hermes mcp add` is interactive by default. Pipe `yes` to auto-answer:

```bash
# HTTP/SSE server
yes | hermes mcp add <name> --url <endpoint>

# Stdio server
yes | hermes mcp add <name> --command <cmd>

# Stdio with args (args must come last)
yes | hermes mcp add <name> --command npx --args "@playwright/mcp@latest"

# Stdio with environment variables for the subprocess (e.g. data-root, RID pins)
yes | hermes mcp add <name> --command /abs/path/tool --env AIRACCOON_DATA_ROOT=/path/to/project AIRACCOON_RID=osx-arm64
```

`--env KEY=VALUE` (repeatable, space-separated) is how stdio servers that need
per-project env (data roots, RID overrides, base URLs) get it — Hermes filters
the subprocess env down to a safe baseline, so anything the server needs beyond
PATH/HOME/etc. MUST be passed via `--env`. Mirror an existing Claude `.mcp.json`
entry's `env` block exactly when porting a server. Absolute command paths avoid
PATH surprises; a missing env var on a server that requires it fails only at
startup in the NEXT session, not at `hermes mcp add` time.

## Diagnosis and repair

List all servers:
```bash
hermes mcp list
```

Test a disabled/broken server:
```bash
hermes mcp test <name>
```

Remove and re-add:
```bash
hermes mcp remove <name>
yes | hermes mcp add <name> --command <corrected-command>
```

Activation requires a session restart (`/reset` in chat, or new `hermes` invocation).

## Building complementary tool stacks

MCP servers work best in pairs. Common combinations for .NET development:

| Pair | Why |
|---|---|
| **Glider + Rider** | Semantic navigation (Glider) + IDE operations (Rider): find symbols with Glider, open/edit in Rider |
| **GliderTrace + Rider** | Run tests with structured results (GliderTrace), fix diagnostics via Rider inspections |
| **dotnet-sdk + Glider** | Create projects/templates via SDK CLI, navigate code semantically with Glider |
| **Playwright + Microsoft Docs** | Research docs, verify on live pages |
| **Playwright + dotnet-sdk** | Scaffold web projects, test them in browser |

## Pitfalls

- **Claude OAuth servers are not portable.** If a config references `claude.ai` subdomains or uses OAuth, it won't work with Hermes. Look for the public MCP equivalent on mcpservers.org instead.
- **`dnx` is NOT the .NET MCP server.** `dnx` is `dotnet-exec`, a temporary tool runner. Install `community.mcp.dotnet` for the actual MCP server (`dotnet-mcp` command).
- **Version sync after adding.** After adding MCP servers, run `/reset` or restart Hermes to pick up the new tools. They won't appear mid-conversation.
- **Event loop crashes on test.** If `hermes mcp test` shows `RuntimeError: Event loop is closed`, the server was added but tools weren't discovered. Remove and re-add.
- **`npx` servers need Node.js 18+.** Playwright MCP and other npm-based servers require a recent Node.js.
- **Rider MCP is project-agnostic in user config.** When configured in `~/.hermes/config.yaml` with `--url http://127.0.0.1:<port>/stream` without `IJ_MCP_SERVER_PROJECT_PATH`, Rider's embedded MCP server automatically binds to whichever active solution/project window is open or focused in Rider.
- **Rider MCP tools fail on git worktrees.** When the project uses a git worktree (e.g. `job-search-ai-assistant-interview-prep` alongside the main checkout `job-search-ai-assistant`), all `mcp__rider__*` tools return *"doesn't correspond to any open project"* regardless of which `projectPath` you pass. Rider tracks the path it opened, not worktree siblings. Fall back to `terminal`, `write_file`, and `patch` for file operations in worktree paths. The error is deterministic — do not retry with different `projectPath` values.
- **`.mcp.json` Python commands must use absolute paths.** Bare `python` or `python3` in `.mcp.json` resolves to the system Python (e.g., `/Library/Developer/CommandLineTools/usr/bin/python3` on macOS), which typically doesn't have MCP packages installed (causing `ModuleNotFoundError` for packages like `semantica` or `code-review-graph`). Always use the full path to the Python that has the package: `/opt/homebrew/bin/python3` for Homebrew, or a venv's `.venv/bin/python3` (e.g., `--command /path/to/project/.venv/bin/python3 --args -m semantica.mcp_server`). Verify with `<path> -m <package> --version` before committing the config.
- **Claude Code `.mcp.json` requires interactive approval.** MCP servers defined in a project's `.mcp.json` show as "⏸ Pending approval" in `claude mcp list` and won't load until the user approves them interactively (run `claude` and accept, or use `/mcp`). If a server is configured in `.mcp.json` but Claude Code can't see its tools, add the same config to `.claude/settings.local.json` under `mcpServers` — that scope loads without approval. Example: `{"mcpServers": {"code-review-graph": {"command": "...", "args": ["-m", "code_review_graph", "serve"], "cwd": "/path/to/project"}}}`.
- **`claude mcp list` rewrites the project's `.mcp.json`.** Health-checking a
  server (which `mcp-index update` triggers via `claude mcp list`) can normalize
  the project config — measured 2026-08: `${HOME}/.dotnet/tools/ai-raccoon`
  became the absolute path, leaving an uncommitted `.mcp.json` diff that NOBODY
  in the session wrote. Harmless and often an improvement (absolute paths are
  the robust form), but expect it: check `git status` after any run that
  invoked `claude mcp list`, and commit the normalization deliberately rather
  than chasing it as an unexplained change.
- **web_extract fails on DuckDuckGo backend.** If `web_extract` returns "DuckDuckGo (ddgs) is a search-only backend", use `curl -s <raw-url>` in terminal instead. Raw GitHub content at `raw.githubusercontent.com` and simple API endpoints work with curl. Reserve `browser_navigate` for pages that need JavaScript rendering.

## Verification

After adding servers and restarting, verify tools are available:
```bash
hermes mcp list
```
All expected servers should show `✓ enabled`. If any show `✗ disabled`, run `hermes mcp test <name>` and check the error.

## Reference

- `references/known-good-configs.md` — verified working MCP server configs with install commands
- `references/rider-tool-categories.md` — all 42 Rider MCP tools categorized by tag and intent (by-task lookup table)
- `references/mcp-tool-indexing.md` — tag+intent index for steering agent tool selection (proven at 100% accuracy on 42 Rider tools)