# Hermes MCP: Non-Interactive Setup

`hermes mcp add` is interactive by default — it prompts for auth, tool selection,
and confirmation. When scripting MCP setup (e.g., in CI, or programmatic
onboarding), use `yes |` to auto-answer all prompts:

```bash
# Add an HTTP MCP server with all tools enabled
yes | hermes mcp add <name> --url <url>

# The 'yes' command answers Y to:
#   "Does this server require authentication?" → Y (then empty token = none)
#   "Enable all N tools?" → Y (enable all)
```

## When the server needs auth

If the MCP server requires a Bearer token or API key, pipe it explicitly:

```bash
# With auth
printf "Y\n<api-key>\nY\n" | hermes mcp add <name> --url <url>
```

## Removing before re-adding

To replace an existing MCP server config, remove it first:

```bash
hermes mcp remove <name> 2>/dev/null
yes | hermes mcp add <name> --url <url>
```

## After adding

MCP tools take effect on the next session. Use `/reset` in-session or start a
new `hermes` invocation. The tools appear as `mcp__<name>__<tool>` in the
available tool list.

## Discovering existing MCP configs

Other agents (Copilot, Claude Code, Junie) store their MCP configs in
predictable locations:

```bash
# Copilot
cat ~/.copilot/mcp-config.json        # HTTP + stdio servers
cat ~/.config/github-copilot/intellij/mcp.json  # JetBrains-specific

# Claude Code
cat ~/.claude/mcp-needs-auth-cache.json  # OAuth-authenticated servers

# Junie
cat ~/.junie/mcp/mcp.json            # template, usually empty
```

Claude's OAuth MCPs (Playwright, Azure, Terraform, etc.) are NOT portable to
Hermes — they use Claude's internal OAuth flow and can't be shared.

## Example: Rider IDE MCP

```bash
yes | hermes mcp add rider --url http://127.0.0.1:64342/stream
```

Rider's MCP server exposes 42 tools: build, search, refactor, run configs,
SQL queries, OpenTelemetry spans, and more. The port varies per Rider instance.

## Server discovery via registries

Use MCP server registries to find public servers and their config details:

```bash
# Browse discoverable servers
open https://mcpservers.org
open https://mcp.so

# Direct config lookup (e.g., for Playwright)
open https://mcpservers.org/servers/playwright-mcp-server
```

These registries show the install command, tools list, and supported
transports (stdio, SSE, streamable-http) for each server.

## .NET SDK MCP server

The .NET MCP server (`community.mcp.dotnet`) wraps the `dotnet` SDK as an MCP
server with 12 tools: project creation, package management, EF Core, SDK info,
template discovery, and enhanced error diagnostics for 52 .NET error codes.

```bash
# Install as a global .NET tool
dotnet tool install -g community.mcp.dotnet

# Then add to Hermes
export PATH="$PATH:$HOME/.dotnet/tools"
yes | hermes mcp add dotnet-sdk --command dotnet-mcp
```

Search for other .NET MCP servers:
```bash
dotnet tool search --prerelease mcp
```

## Verified MCP servers (tested with Hermes)

| Server | Command/URL | Tools | Install |
|--------|------------|-------|---------|
| Playwright | `npx @playwright/mcp@latest` | 24 (browser automation) | `yes \| hermes mcp add playwright --command npx --args "@playwright/mcp@latest"` |
| Microsoft Docs | `https://learn.microsoft.com/api/mcp` | 3 (docs search, code samples, fetch) | `yes \| hermes mcp add microsoft-docs --url https://learn.microsoft.com/api/mcp` |
| .NET SDK | `dotnet-mcp` | 12 (project, package, EF, SDK) | `dotnet tool install -g community.mcp.dotnet` then `yes \| hermes mcp add dotnet-sdk --command dotnet-mcp` |
| Rider IDE | `http://127.0.0.1:64342/stream` | 42 (build, search, refactor, SQL, OTel) | `yes \| hermes mcp add rider --url http://127.0.0.1:64342/stream` |
