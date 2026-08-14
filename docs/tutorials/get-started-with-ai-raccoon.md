# Get started with AiRaccoon

Install, run, and connect AiRaccoon to your coding agent in under two minutes.

## Overview

AiRaccoon is an MCP memory server that gives AI agents persistent, project-scoped memory over SQLite.

```mermaid
flowchart LR
    subgraph Client ["MCP Client (Claude Code / Hermes / IDE)"]
        C[Agent Tool Calls]
    end
    
    subgraph AiRaccoon ["AiRaccoon Stack"]
        P["ai-raccoon (Proxy)"]
        S["ai-raccoon serve (HTTP Backend)"]
        DB[("~/.ai-raccoon/memory.db\nSQLite + FTS5 + vec0")]
        
        C <-->|JSON-RPC stdio| P
        P <-->|HTTP Loopback :7721| S
        S <--> DB
    end
```

---

## Step 1: Install the CLI tool

Install `ai-raccoon` with the .NET 10 SDK:

```bash
dotnet tool install -g ai-raccoon
```

### Migrating from `arasz.ai-raccoon`

If you used the preview package `arasz.ai-raccoon`, uninstall it first. The package moved to `ai-raccoon`, but both use the same binary name (`ai-raccoon`). Your existing memory database under `~/.ai-raccoon` stays untouched:

```bash
dotnet tool uninstall -g arasz.ai-raccoon
dotnet tool install -g ai-raccoon
```

---

## Step 2: Pick a transport mode

AiRaccoon supports three transport modes depending on how your agent runs:

```mermaid
graph TD
    Start([Launch Mode]) --> Choice{Which setup do you need?}
    
    Choice -->|Zero-config / Default| Proxy["Proxy Mode (Default)\n`ai-raccoon`"]
    Choice -->|In-Process / Standalone| Stdio["Stdio Mode\n`ai-raccoon --transport stdio`"]
    Choice -->|Remote / Long-lived Daemon| HTTP["HTTP Serve Mode\n`ai-raccoon --transport http`"]
    
    Proxy --> P_Desc["Auto-spawns HTTP background server on demand\nRecommended for general agent work"]
    Stdio --> S_Desc["Runs server inside the process over stdin/stdout\nNo background daemon or network ports"]
    HTTP --> H_Desc["Exposes HTTP endpoint at /mcp\nSupports multi-client attachment & telemetry"]
```

1. **Proxy mode (Default / Recommended):**
   ```bash
   ai-raccoon
   ```
   Probes port `7721`, starts a background `ai-raccoon serve` process if nothing is listening, and relays JSON-RPC messages. Details in [ADR-0020](../adr/0020-always-on-http-stdio-proxy.md).

2. **Stdio mode (Standalone):**
   ```bash
   ai-raccoon --transport stdio
   ```
   Runs a self-contained server over stdio. No background daemons and no network ports.

3. **HTTP mode (Server / Remote):**
   ```bash
   ai-raccoon --transport http --port 7721
   ```
   Starts a streamable HTTP endpoint on `http://127.0.0.1:7721/mcp`.

---

## Step 3: Add to your `.mcp.json`

Add AiRaccoon to your project or global `.mcp.json`:

```json
{
  "mcpServers": {
    "ai-raccoon": {
      "command": "ai-raccoon"
    }
  }
}
```

When your agent starts up, it connects through the proxy to the backend memory store automatically.

---

## Step 4: Verify the round trip

Confirm the install actually works by asking your agent to write and then find a memory:

1. Ask it to call `memory_write` with `projectId="get-started"` and `content="AiRaccoon install verification note"`.
2. Ask it to call `memory_search` with `projectId="get-started"` and `query="install verification"`.

A successful search returns the note you just wrote in its `results`. If it comes back empty, re-check Step 3's `.mcp.json` entry and confirm your agent actually connected to the `ai-raccoon` server.

---

## Next Steps

- [Configure and run the AiRaccoon server](../how-to/configure-ai-raccoon-server.md) — Passphrases, port binding, and zero-downtime restarts.
- [Configure embedding engines](../how-to/configure-embedding-engines.md) — Local ONNX models vs remote OpenAI-compatible endpoints.
- [Agent Memory Capabilities](../explanation/agent-memory-capabilities.md) — Hybrid search, workspaces, and shared promotion tier.
- [Agent Memory Server Reference](../reference/agent-memory-server.md) — Complete tool contract and CLI verbs.
