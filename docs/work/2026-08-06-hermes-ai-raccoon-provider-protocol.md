# Protocol design: AiRaccoon as a Hermes memory provider plugin

**Date:** 2026-08-06
**Status:** design for discussion — decisions D1-D5 open, recommendations given

## Goal

A Hermes memory provider plugin (`plugins/memory/ai-raccoon/`, user-installed at
`$HERMES_HOME/plugins/ai-raccoon/`) implementing the `MemoryProvider` ABC
(`agent/memory_provider.py`) so the AiRaccoon MCP memory server gains the full provider
lifecycle: per-turn prefetch, background sync, system-prompt block, session hooks — instead of
being only a plain MCP tool server (the current setup). The interface contract is documented in
`docs/work/2026-08-06-hermes-memory-provider-interface.md` (findings F1-F10).

## Constraints (verified)

- The ABC is in-process Python; every call site is a direct method call (interface record F5).
- Only ONE external provider can be active (`memory.provider` config); the default profile
  currently runs `holographic` (~/.hermes/config.yaml:89) — activating ai-raccoon replaces it.
- `projectId` is REQUIRED on every AiRaccoon tool call (`Tools/MemoryTools.cs:55-61`
  `RequireProjectId`; validated per tool).
- The hermes venv ships the official MCP Python SDK `mcp 1.28.1` (+ httpx/httpx-sse) — the
  plugin is an MCP *client*; no raw protocol hand-rolling.
- AiRaccoon transports: stdio (plain host, no port binding) and Streamable HTTP at `/mcp`
  (default port 7721; `Setup/McpServerSetup.cs:82` `MapMcp("/mcp")`).
- Tool contracts (from `Tools/MemoryTools.cs`):
  - `memory_write(projectId, content, workspaceId?, agentId?, context?, sourceFile?, section?)`
    → `{hash, path, context, createdAt}`
  - `memory_search(projectId, query, scope=all|project|shared, workspaceId?, limit=20,
    minScore=0.7, rrfK, ftsWeight, vectorWeight, contextLabel?)` → result list
  - 18 further tools (list/stats/share/share_extract/delete/delete_context/ingest_file/
    ingest_directory/embed_pending/workspace_begin|status|consolidate|discard/sweep/sync).
- `is_available()` must NOT make network calls (ABC docstring); the real connect happens in
  `initialize()`.

## D1 — Transport: how the plugin talks to AiRaccoon

| Option | Shape | Pros | Cons |
|---|---|---|---|
| A. stdio child | plugin spawns `ai-raccoon` (installed tool) as a child, MCP over stdio | self-contained; no ports, no external orchestration; mirrors today's config.yaml MCP entry (command-based); lifecycle owned by plugin (`initialize` spawns, `shutdown` terminates) | one extra process per Hermes session (~1-2 s startup); per-session server, so bank is shared via SQLite WAL (safe — stdio-only binds no port) |
| B. Streamable HTTP | plugin connects to a running server at `http://127.0.0.1:<port>/mcp` | one long-running server can serve the provider AND other clients (the 5094-bridge pattern); no per-session process | requires the server to be running; port/lifecycle management; is_available cannot probe (no network per contract) — availability only known at initialize |

**Recommendation: A (stdio child) as the default, B (HTTP) as a config option.**
Both speak the identical MCP protocol — the transport is a client-construction detail (mcp SDK
`stdio_client` vs `streamable_http_client`). `transport: stdio|http` is a config field.

## D2 — Tool surface: which AiRaccoon tools the provider exposes to the model

Provider tools are injected alongside core tools; they must not shadow `_HERMES_CORE_TOOLS`
(rejected at registration, `memory_manager.py:430-464`). `memory_*` names are safe. Tool bloat
is the reason the one-provider rule exists — holographic exposes 2 tools.

**Recommendation (v1, curated 4):** `memory_search`, `memory_write`, `memory_stats`,
`memory_share`. The remaining 16 stay reachable through the existing plain-MCP bridge
(config.yaml) while both integrations coexist; the provider can grow later. All four are
pass-through proxies: args forwarded verbatim, `projectId` injected by the provider.

## D3 — Project scoping: what `projectId` the plugin passes

`initialize(session_id, **kwargs)` provides `agent_workspace` ("hermes") and `agent_identity`
(profile name) exactly for per-profile identity scoping (ABC docstring).

**Recommendation:** `project_id = config.project_id OR f"{workspace or 'hermes'}-{identity or
'default'}"` — i.e. `hermes-default` for the default profile. Config override wins. This keeps
each Hermes profile's memory in its own AiRaccoon project scope inside the shared
`~/.ai-raccoon` bank, and matches the agent_identity contract rather than inventing a scheme.

## D4 — Provider method → tool mapping (the protocol)

| MemoryProvider method | AiRaccoon call | Notes |
|---|---|---|
| `name` | — | `"ai-raccoon"` |
| `is_available()` | — | config present + (stdio) binary resolvable on PATH; no network |
| `initialize(session_id, **kwargs)` | MCP handshake | stdio: spawn child; http: connect; store session_id; skip heavy work |
| `system_prompt_block()` | — | short static block: memory-first ladder (search before asking, cite sources) — mirrors the existing ai-raccoon prompt |
| `prefetch(query, *, session_id)` | `memory_search(projectId, query, scope="all", limit=5, minScore=0.5)` | format top hits as a `## AiRaccoon Memory` block; return "" on none; runtime already gates trivial prompts (turn_context.py:1171) and applies the 8 s external timeout |
| `sync_turn(user_content, assistant_content, *, session_id, messages)` | `memory_write(projectId, content=<assistant final message>, sourceFile=f"hermes/{session_id}", section="turn", agentId=f"hermes-{identity}")` | must be non-blocking (daemon thread per threading contract); **open question D4a: content shape** — raw assistant message vs distilled line vs nothing in v1 |
| `get_tool_schemas()` | curated D2 set | OpenAI function format; converted from MCP tool definitions |
| `handle_tool_call(name, args)` | matching tool + injected projectId | JSON-string result (MCP result payload) |
| `on_memory_write(action, target, content)` | `action=add` → `memory_write(projectId, content, sourceFile="hermes-memory", section=target)` | v1 mirrors adds only; remove/replace need hash tracking — deferred |
| `on_session_end(messages)` | optional `memory_write` session summary | **open question D4b**: on by default or off? |
| `on_delegation(task, result)` | optional `memory_write` | parent-side observation; v1 no-op |
| `shutdown()` | stdio: terminate child (graceful EOF); http: close session | deterministic, no hanging |

Failure posture: any provider exception is caught per-method by MemoryManager (system prompt,
prefetch, sync all swallow and log — verified in the interface record F5); a dead server never
breaks the agent turn.

## D5 — Config surface (`get_config_schema()` → `hermes memory setup`)

| key | default | secret |
|---|---|---|
| `transport` | `stdio` (choices stdio/http) | no |
| `url` (http mode) | `http://127.0.0.1:7721/mcp` | no |
| `binary` (stdio mode) | `ai-raccoon` (PATH) | no |
| `project_id` | empty → derived (D3) | no |
| `search_limit` / `min_score` | 5 / 0.5 | no |
| `scope` | `all` | no |

No secrets: AiRaccoon is local; the bank passphrase stays in the server's own env, never in the
plugin. Non-secret fields persist via `save_config` into `plugins.ai-raccoon` in config.yaml
(the holographic pattern: `cfg_get(config, "plugins", "hermes-memory-store")`).

## D6 — Interaction with the existing integration

- Today: `~/.hermes/config.yaml` registers `ai-raccoon` as a plain MCP server (stdio, installed
  tool). The provider ADDS lifecycle; the two can coexist (provider = curated tools +
  prefetch/sync; bridge = full 20-tool surface). Later the bridge can be dropped if the
  provider grows its surface.
- Activating `memory.provider: ai-raccoon` replaces `holographic` (one-external rule). The
  holographic fact store DB stays on disk untouched; its `fact_store`/`fact_feedback` tools
  stop being injected. Rollback = flip the config key back.
- Subagents (`agent_context != "primary"`) must not sync (per ABC contract, and subagents run
  `skip_memory=True` anyway) — v1 writes only for primary agents.

## Open questions for the owner

1. **D1 transport**: stdio child (recommended) or HTTP-to-running-server, or both?
2. **D2 tool surface**: curated 4 (recommended) or a different set (e.g. + ingest_file for
   doc ingestion)?
3. **D3 project scoping**: derived `hermes-<profile>` (recommended) or a fixed `project_id`?
4. **D4a sync content**: write the assistant's final message per turn (recommended), a
   distilled summary, or nothing in v1?
5. **D4b session end**: write a session summary on `on_session_end` (recommended off in v1)?
6. **D6**: keep the plain-MCP bridge alongside (recommended) or remove it once the provider
   lands?

## Decision record (filled as decided)

| # | Decision | Chosen |
|---|---|---|
| D1 | transport | — |
| D2 | tool surface | — |
| D3 | project scoping | — |
| D4a | sync content | — |
| D4b | session end | — |
| D6 | bridge coexistence | — |
