# Hermes memory provider plugin interface

Verified against the Hermes source checkout `~/.hermes/hermes-agent/` at commit
8f2712725 (2026-08-05) — the exact revision the installed `hermes` CLI runs — plus a
live loader probe. Official doc: `website/docs/developer-guide/memory-provider-plugin.md`
in that checkout. The interface is a dated, moving target: optional hooks are additive by
design (backward-compat defaults), but re-verify against the checkout before writing a
provider.

## What a memory provider is

A specialized plugin type (with context engines, one of two "provider plugin" types).
Single-select: only ONE external provider active at a time, chosen by `memory.provider`
in config.yaml (empty = built-in MEMORY.md/USER.md only). Auto-detected as
`kind: exclusive`, routed via `memory.provider`, NOT `plugins.enabled`. The bundled set
is closed to upstream PRs — new providers ship as standalone repos.

## The interface: agent/memory_provider.py::MemoryProvider

Abstract (must implement):

- `name` (property) — short id, e.g. "holographic". Must match the plugin dir name.
- `is_available()` — config/deps check only, NO network calls; gates activation at init.
- `initialize(session_id, **kwargs)` — once at startup. kwargs always include
  `hermes_home` (profile-scoped storage — never hardcode ~/.hermes), `platform`
  ("cli"/"telegram"/...); may include `agent_context` ("primary"/"subagent"/"cron"),
  `agent_identity`, `parent_session_id`, `user_id`, `session_title`.
- `get_tool_schemas()` — list of OpenAI function schemas `{"name","description",
  "parameters"}`; empty list for context-only providers.

Optional (override to opt in; contracts from docstrings):

- `system_prompt_block()` — static text for system prompt (status/instructions).
- `prefetch(query, *, session_id="")` — recall before each API call; must be FAST,
  background the real work, return formatted text or "".
- `queue_prefetch(query, *, session_id="")` — called after each turn to pre-warm the
  next turn's prefetch.
- `sync_turn(user_content, assistant_content, *, session_id="", messages=None)` — after
  each completed turn; MUST be non-blocking (daemon thread if backend has latency).
  `messages` = OpenAI-style conversation incl. tool calls/results.
- `shutdown()` — flush queues, close connections.
- `on_turn_start(turn_number, message, **kwargs)` — per-turn tick.
- `on_session_end(messages)` — ONLY at real session boundaries (exit, /reset, gateway
  expiry), not per turn.
- `on_session_switch(new_session_id, *, parent_session_id, reset, rewound, **kwargs)` —
  on /resume, /branch, /reset, /new, compression; providers caching per-session state
  must update/reset it here.
- `on_pre_compress(messages) -> str` — text folded into the compression summary prompt
  so provider insights survive compaction.
- `on_memory_write(action, target, content, metadata=None)` — mirror of built-in memory
  writes (action ∈ add/replace/remove, target ∈ memory/user).
- `on_delegation(task, result, *, child_session_id, **kwargs)` — parent-side observation
  when a subagent completes (subagents have no provider session).
- `get_config_schema()` — field dicts for `hermes memory setup`: key, description,
  secret (True → written to .env with env_var), required, default, choices, type,
  minimum/maximum, step, url.
- `save_config(values, hermes_home)` — write non-secret fields; env-var-only providers
  leave the no-op default.
- `backup_paths() -> list[str]` — absolute paths OUTSIDE HERMES_HOME to include in
  `hermes backup` (resolved from config/env only, callable pre-initialize).

`handle_tool_call(tool_name, args, **kwargs) -> str` — dispatch for tools declared in
get_tool_schemas; MUST return a JSON string; default raises NotImplementedError.

## Discovery + registration

`plugins/memory/__init__.py`:

- Scans bundled `<checkout>/plugins/memory/<name>/` then user
  `$HERMES_HOME/plugins/<name>/`. Bundled wins name collisions.
- Heuristic: `__init__.py` source (first 8 KB) contains `register_memory_provider` or
  `MemoryProvider`. User dir plugins failing the heuristic are skipped silently.
- Loading: `register(ctx)` with `ctx.register_memory_provider(provider)` first; fallback
  = instantiate a top-level MemoryProvider subclass. Sibling `*.py` modules are
  pre-registered so relative imports resolve (user plugins get a synthetic
  `_hermes_user_memory` parent package).
- Optional `cli.py` with `register_cli(subparser)` + handler → `hermes <provider>`
  subcommands, but ONLY while that provider is the active `memory.provider`
  (`discover_plugin_cli_commands`, active-provider gating).
- plugin.yaml (name/version/description/hooks) is read for metadata when present; the
  discovery heuristic does not require it.

## Runtime wiring (agent/memory_manager.py + agent/agent_init.py)

- agent_init reads `memory.provider`, `load_memory_provider(name)`, gates on
  `is_available()`, registers on MemoryManager; initialize kwargs assembled there.
- MemoryManager: built-in provider (`name == "builtin"`) always first and always
  admitted; a SECOND external provider is rejected with a warning; provider tools that
  shadow reserved core tool names are rejected at the door.
- Per turn: build_system_prompt → prefetch_all (external prefetch 8 s timeout) →
  tools injected (gated by `memory` toolset; name collisions skipped) → after turn:
  sync_all + queue_prefetch_all on a background executor (shutdown drains ≤5 s).
- normalize_tool_schema accepts BOTH bare `{"name",...}` and pre-wrapped
  `{"type":"function","function":{...}}` shapes — double-wrapping broke strict
  providers with HTTP 400, so never return the wrapped shape.

## Bundled providers (as of 2026-08-05)

byterover, hindsight, holographic, honcho, mem0, openviking, retaindb, supermemory.
Only holographic (local SQLite fact store + FTS5 + HRR vectors, tools `fact_store` /
`fact_feedback`) reports is_available=True without credentials; the rest gate on env
keys / config files.

## Live probe (proves discovery→load→instance end to end)

```bash
cd ~/.hermes/hermes-agent && ./venv/bin/python -c "
from plugins.memory import discover_memory_providers, load_memory_provider, list_memory_provider_names
print(list_memory_provider_names())
print(discover_memory_providers())
p = load_memory_provider('holographic')
print(type(p).__name__, p.name, p.is_available(), [s['name'] for s in p.get_tool_schemas()])
"
```

## Implication for server-backed memory (e.g. an MCP memory server)

The ABC is pure in-process Python with no transport abstraction — every call site is a
direct method call. A remote backend (like the AiRaccoon MCP server) plugs in as a thin
MemoryProvider shim: `initialize` opens a client to the server's HTTP/stdio endpoint,
`get_tool_schemas`/`handle_tool_call` proxy the server's tools, `sync_turn`/`prefetch`
call search/ingest verbs non-blocking. The honcho/mem0/openviking providers are the
precedent (cloud APIs behind the same ABC). Tools must be declared as OpenAI function
schemas, so a shim needs one schema per proxied verb.
