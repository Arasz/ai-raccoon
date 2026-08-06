# Hermes memory-provider plugin interface (verified 2026-08-06)

Source: `~/.hermes/hermes-agent` at commit `8f2712725` (the revision the installed CLI runs).
Every claim below was verified by reading the source AND by a live loader round trip through the
checkout's own venv; a review pass re-checked each path:line.

Memory providers are a SPECIALIZED plugin type — not hooks plugins. They are single-select,
routed via `memory.provider` (not `plugins.enabled`), and auto-detected as `kind: exclusive`.
The bundled set is closed to upstream PRs; new providers ship as standalone repos.

## The ABC (`agent/memory_provider.py:81-357`)

**Abstract (must implement):**
- `name` (property) — short id, e.g. `"holographic"`
- `is_available()` — config/deps check only, **NO network calls** (real connect happens in `initialize`)
- `initialize(session_id, **kwargs)` — once at agent startup
- `get_tool_schemas()` — OpenAI function format `{"name","description","parameters"}`

**De-facto required if you expose tools:**
- `handle_tool_call(tool_name, args, **kwargs) -> str` — must return a JSON string; default raises `NotImplementedError`

**Optional (14):** `system_prompt_block()`, `prefetch(query, *, session_id="")`,
`queue_prefetch(query, *, session_id="")`, `sync_turn(user_content, assistant_content, *,
session_id="", messages=None)`, `shutdown()`, `on_turn_start(turn_number, message, **kwargs)`,
`on_session_end(messages)`, `on_session_switch(new_session_id, *, parent_session_id, reset,
rewound)`, `on_pre_compress(messages) -> str`, `on_memory_write(action, target, content,
metadata=None)`, `on_delegation(task, result, *, child_session_id="")`,
`get_config_schema()`, `save_config(values, hermes_home)`, `backup_paths()`.

**`initialize` kwargs contract (from the ABC docstring):** `hermes_home` (str) ALWAYS present —
use it for profile-scoped storage, never hardcode `~/.hermes`; `platform` ("cli"/"telegram"/…);
`agent_context` ("primary"/"subagent"/"cron"/"flush" — skip writes for non-primary);
`agent_identity` (profile name — use for per-profile scoping); `agent_workspace` ("hermes");
`session_title` (when the session DB has one).

**Threading rules:** `sync_turn` MUST be non-blocking (daemon thread; the manager drains 5 s at
shutdown); `prefetch` must be fast (the runtime applies an 8 s external timeout); `shutdown` must
be deterministic (close connections on the caller's thread).

## Registration & discovery (`plugins/memory/__init__.py`)

- Plugin = a DIRECTORY `plugins/memory/<name>/` (bundled) or `$HERMES_HOME/plugins/<name>/`
  (user-installed) whose `__init__.py` contains `register(ctx)` calling
  `ctx.register_memory_provider(provider)`. A bare top-level `MemoryProvider` subclass also
  loads (no `register` needed). `plugin.yaml` is optional (description only).
- Discovery heuristic: `__init__.py` text contains `register_memory_provider` or `MemoryProvider`
  (cheap scan, first 8 KB). Bundled providers win name collisions.
- Sibling `.py` modules in the dir are pre-registered so relative imports resolve; user plugins
  load under a synthetic `_hermes_user_memory` namespace.
- **Selection:** `memory.provider` config key (config.yaml `memory:` section). Empty = built-in
  only. ONE external provider max — `MemoryManager.add_provider` rejects a second with a warning
  (`agent/memory_manager.py:404-428`). Provider tool names that shadow `_HERMES_CORE_TOOLS` are
  dropped (`:430-464`).
- Optional `cli.py` with `register_cli(subparser)` + a handler → `hermes <provider>` subcommands,
  but ONLY when that provider is the active `memory.provider` (active-provider gating).

## Runtime call points (MemoryManager; wired at `agent/agent_init.py:1697-1730`)

1. Agent init: `load_memory_provider(name)` → `is_available()` gate → `add_provider` →
   `initialize(session_id, **kwargs)`.
2. System prompt assembly: `build_system_prompt()` collects `system_prompt_block()` text.
3. Pre-turn: `prefetch_all(query)` — gated by `is_trivial_prompt`
   (`agent/turn_context.py:1171`: greetings/slash commands/acknowledgements skip recall);
   skill/bundle scaffolding stripped before the provider sees it; 8 s external timeout.
4. Post-turn: `sync_all(user, assistant)` + `queue_prefetch_all(query)` on a background executor
   (single worker serializes writes; 5 s drain at shutdown).
5. Tools: `get_all_tool_schemas()` injected at agent init by `inject_memory_provider_tools`
   (gated on the `memory` toolset; name collisions skipped). `normalize_tool_schema` accepts both
   the bare function schema and an already-wrapped `{"type":"function","function":{...}}` —
   double-wrapping breaks strict providers (HTTP 400).
6. Dispatch: `handle_tool_call` routes by tool name to the owning provider.
7. Session boundaries: `on_session_end` (real ends only — CLI exit, /reset, gateway expiry),
   `on_session_switch` (/resume, /branch, /reset, /new, context compression), `on_pre_compress`
   (returned text is folded into the compression prompt).

Built-in memory (MEMORY.md/USER.md) is ALWAYS active alongside the external provider;
`on_memory_write` mirrors built-in writes (action ∈ add/replace/remove, target ∈ memory/user).

## Config contract

- `get_config_schema()`: list of dicts `{key, description, secret, required, default, choices,
  type, url, env_var}`. `hermes memory setup` walks it; `secret: True` + `env_var` → `.env`,
  non-secrets → `save_config(values, hermes_home)`.
- Per-plugin config is readable from config.yaml `plugins.<name>` (holographic reads
  `plugins.hermes-memory-store` via `cfg_get(config, "plugins", "hermes-memory-store")`).
- All storage paths derive from the `hermes_home` kwarg — profile isolation is a hard rule.

## Reference implementations (bundled, `plugins/memory/`)

- `holographic` — local SQLite fact store (FTS5 + HRR vectors), 2 tools
  (`fact_store`, `fact_feedback`) — the minimal in-process reference.
- `honcho` — cloud API behind the same ABC + 13-subcommand `cli.py` — the full reference.
- Others: `byterover`, `hindsight`, `mem0`, `openviking`, `retaindb`, `supermemory`.

## Verification recipe (prove a provider loads — MEASURED round trip)

```bash
cd ~/.hermes/hermes-agent && ./venv/bin/python -c \
  "from plugins.memory import load_memory_provider, discover_memory_providers; \
   print(discover_memory_providers()); \
   p = load_memory_provider('holographic'); \
   print(p.name, p.is_available(), [s['name'] for s in p.get_tool_schemas()])"
```

Use the checkout's OWN venv (`~/.hermes/hermes-agent/venv/`), not the system python — the
installed CLI runs that interpreter. Expected: availability differs per provider (local
providers True; credential/deps-gated ones False — byterover checks the `brv` CLI, hindsight a
local runtime), so `is_available()` is what decides activation.

## Bridging a server-backed memory (AiRaccoon design, in progress 2026-08-06)

- The hermes venv ships the official MCP SDK (`mcp 1.28.1`) — a provider can be an MCP *client*.
  Two transports: stdio child (spawn the installed tool; plugin owns lifecycle: spawn in
  `initialize`, terminate in `shutdown`) or Streamable HTTP (connect to a running server at
  `/mcp`, default port 7721). Same MCP protocol, client-construction detail only.
- AiRaccoon requires `projectId` on EVERY tool call (`Tools/MemoryTools.cs` `RequireProjectId`)
  — the provider must inject it: derive from `initialize` kwargs
  (`f"{agent_workspace or 'hermes'}-{agent_identity or 'default'}"`), config override wins.
- Method mapping: `prefetch` → `memory_search(scope="all", limit=5, minScore=0.5)`; `sync_turn`
  → `memory_write(sourceFile=f"hermes/{session_id}", section="turn")`; curated tool surface
  via pass-through `get_tool_schemas`/`handle_tool_call`.
- Full protocol design: ai-raccoon repo `docs/work/2026-08-06-hermes-ai-raccoon-provider-protocol.md`.
