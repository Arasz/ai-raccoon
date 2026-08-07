# Research: the Hermes Agent memory plugin interface

**Date:** 2026-08-06
**Question:** What interface must a plugin implement to plug into Hermes Agent's memory system, and how does the runtime discover, select, and call it?

Source inspected: the Hermes Agent source checkout `~/.hermes/hermes-agent/` at commit `8f2712725` (2026-08-05 22:40:51 -0700, "feat: /refine — run the memory/skill self-improvement review on demand"), i.e. the exact revision the installed `hermes` CLI runs.

## Findings

### F1 — The discovery→load→instance round trip works end to end: 8 providers found, holographic loads [MEASURED]

Running the loader against the checkout discovered 8 providers (byterover, hindsight, holographic, honcho, mem0, openviking, retaindb, supermemory), reported availability per provider, and `load_memory_provider("holographic")` returned a live `HolographicMemoryProvider` instance (`name == "holographic"`, `is_available() == True`, tool schemas `fact_store` + `fact_feedback`). The other seven reported `is_available() == False` — the gates vary per provider (config, credentials, or local tool/runtime presence — e.g. byterover checks the `brv` CLI, hindsight checks a local runtime in local modes), so the availability gate is what actually decides activation, not mere presence on disk.

**Evidence:** `cd ~/.hermes/hermes-agent && ./venv/bin/python -c "from plugins.memory import discover_memory_providers, load_memory_provider, list_memory_provider_names; ..."` — macOS arm64, the checkout's own `venv/` interpreter, at commit 8f2712725. Output: `discovered names: ['byterover', 'hindsight', 'holographic', 'honcho', 'mem0', 'openviking', 'retaindb', 'supermemory']`, `loaded: HolographicMemoryProvider | name = holographic | is_available = True`, `tool schemas: ['fact_store', 'fact_feedback']`.

### F2 — The interface is one Python ABC: `agent.memory_provider.MemoryProvider` [READ]

A memory plugin implements the `MemoryProvider` abstract base class (`agent/memory_provider.py`). Four members are abstract: the `name` property, `is_available()` (config/deps check only, no network), `initialize(session_id, **kwargs)`, and `get_tool_schemas()`. `handle_tool_call()` has a default that raises `NotImplementedError` but is de facto required for any provider that exposes tools. Fourteen more methods are optional overrides: `system_prompt_block()`, `prefetch()`, `queue_prefetch()`, `sync_turn()`, `shutdown()`, `on_turn_start()`, `on_session_end()`, `on_session_switch()`, `on_pre_compress()`, `on_memory_write()`, `on_delegation()`, `get_config_schema()`, `save_config()`, `backup_paths()`. The docstrings pin the contracts: `prefetch` must be fast, `sync_turn` must be non-blocking (background the work), `on_session_end` fires only at real session boundaries, `on_memory_write` mirrors built-in memory writes, `backup_paths` extends `hermes backup` for state kept outside HERMES_HOME.

**Evidence:** `~/.hermes/hermes-agent/agent/memory_provider.py:81-357` (class + all method docstrings); also confirmed by the measurement's ABC surface dump (19 public methods, 4 abstract).

### F3 — Registration is plugin-style: `register(ctx)` → `ctx.register_memory_provider(provider)` [READ]

The plugin's `__init__.py` defines `register(ctx)` and calls `ctx.register_memory_provider(MyMemoryProvider())` (see `plugins/memory/holographic/__init__.py:458-462`). Discovery lives in `plugins/memory/__init__.py`: it scans the bundled `plugins/memory/<name>/` directory and user-installed `$HERMES_HOME/plugins/<name>/`; the heuristic for "is this a memory provider" is a text scan of `__init__.py` for `register_memory_provider` or `MemoryProvider`; bundled providers win name collisions; a bare top-level `MemoryProvider` subclass in the module also loads (no `register` needed). The loader also registers sibling `*.py` modules so relative imports inside a plugin resolve.

**Evidence:** `~/.hermes/hermes-agent/plugins/memory/__init__.py:74-87` (heuristic), `:90-121` (directory scan + precedence), `:219-327` (`_load_provider_from_dir`: register-first, subclass fallback, sibling-module registration).

### F4 — Selection is single-select via `memory.provider` in config.yaml [READ]

`memory.provider` (default empty) names the one active external provider; empty means built-in memory only. At agent init (`agent/agent_init.py:1697-1730`) the name is read from config, `load_memory_provider(name)` loads the instance, `is_available()` gates activation, and it is registered on a `MemoryManager`. `MemoryManager.add_provider` enforces the one-external-provider rule (a second is rejected with a warning), always admits a provider named `"builtin"` (registered first — a branch exercised by tests; production registers only the external provider at `agent/agent_init.py:1710`), and rejects any provider tool that shadows a reserved core tool name.

**Evidence:** `~/.hermes/hermes-agent/hermes_cli/config_defaults.py:1658-1662`; `agent/agent_init.py:1697-1730` (load + `is_available` gate + `initialize` kwargs incl. `hermes_home`, `platform`, `agent_context`, `session_title`); `agent/memory_manager.py:364-428` (one-external rule), `:430-464` (core-tool shadow rejection).

### F5 — The runtime calls the provider directly at five points in the turn/init lifecycle [READ]

The `MemoryManager` (wired in `agent_init.py`, one per agent) is the single integration point: (1) `build_system_prompt()` collects `system_prompt_block()` text at prompt assembly; (2) `prefetch_all(query)` runs before each API call on non-trivial turns — gated by `is_trivial_prompt` at `agent/turn_context.py:1171` (greetings/acknowledgements skip recall entirely), with skill/bundle scaffolding stripped at `memory_manager.py:531-533`; the external prefetch is bounded by an 8 s timeout; (3) `sync_all(user, assistant)` + `queue_prefetch_all(query)` run after each completed turn on a background executor (shutdown drains in-flight work for 5 s); (4) `get_all_tool_schemas()` feeds `inject_memory_provider_tools` at agent init, appending provider tools to the agent's tool surface, gated by the `memory` toolset and skipping name collisions; (5) `handle_tool_call` routes a tool name back to its owning provider whenever the model calls one. `on_session_end` and `on_session_switch` fire at session boundaries. All of these are direct in-process method calls — there is no IPC, socket, or subprocess protocol anywhere in the call path.

**Evidence:** `~/.hermes/hermes-agent/agent/memory_manager.py:110-156` (tool injection), `:486-503` (system prompt), `:525-597` (prefetch), `:638-783` (sync), `:784-864` (schemas + dispatch), `:865-1143` (session hooks), `:1144-1241` (shutdown + initialize_all); `agent/agent_init.py:1697-1730`.

### F6 — Tool schemas are OpenAI function-calling format; results are JSON strings [READ]

`get_tool_schemas()` returns bare function schemas `{"name", "description", "parameters"}`; the manager normalizes both the bare shape and an already-wrapped `{"type": "function", "function": {...}}` shape before injection (a double-wrap broke strict providers with HTTP 400, so normalization is load-bearing). `handle_tool_call(tool_name, args, **kwargs)` must return a JSON string as the tool result; the bundled holographic provider's handlers wrap `json.dumps` and use `tools.registry.tool_error` for failures.

**Evidence:** `~/.hermes/hermes-agent/agent/memory_provider.py:172-188`; `agent/memory_manager.py:50-80` (`normalize_tool_schema` rationale); `plugins/memory/holographic/__init__.py:39-91, 225-233, 271-367`.

### F7 — Config and CLI surfaces: schema-driven setup, secrets to .env, optional provider CLI [READ]

`get_config_schema()` returns field dicts (`key`, `description`, `secret`, `required`, `default`, `choices`, `type`, `env_var`, `url`, ...) consumed by `hermes memory setup`; `secret: True` fields with `env_var` go to `.env`, non-secrets go to `save_config(values, hermes_home)`. Providers can also read their own config from `plugins.<name>` in config.yaml (holographic reads `plugins.hermes-memory-store`). An optional `cli.py` with `register_cli(subparser)` + a handler function registers `hermes <provider>` subcommands — but only when that provider is the active `memory.provider` (active-provider gating via `discover_plugin_cli_commands`).

**Evidence:** `~/.hermes/hermes-agent/agent/memory_provider.py:283-320`; `website/docs/developer-guide/memory-provider-plugin.md:86-133, 213-264`; `plugins/memory/__init__.py:365-461`; `plugins/memory/holographic/__init__.py:98-106, 129-154`.

### F8 — Memory providers are a specialized plugin type of the general plugin system [READ]

The developer docs classify memory providers as one of two "provider plugin" types (the other: context engines), both single-select and config-driven, managed via `hermes plugins`. They are auto-detected as `kind: exclusive` and routed through `memory.provider` instead of `plugins.enabled`; `ctx.register_memory_provider` is one of the context capabilities of the general plugin ABI. The bundled set is closed to upstream PRs — new providers are published as standalone plugin repos and shared in the Nous Research Discord.

**Evidence:** `~/.hermes/hermes-agent/website/docs/developer-guide/plugins/index.md:358, 1021-1055`; `developer-guide/plugin-llm-access.md:447`; `developer-guide/secret-source-plugin.md:12`; `developer-guide/architecture.md:234`.

### F9 — Built-in memory is always active alongside; providers add, and can mirror [READ]

The built-in memory (MEMORY.md/USER.md, `tools/memory_tool.py::MemoryStore`) is created unless both `memory_enabled` and `user_profile_enabled` are off (gate at `agent/agent_init.py:1685`); the external provider is a separate, additional channel. The `on_memory_write(action, target, content, metadata)` hook notifies the provider of every built-in memory write (`action` ∈ add/replace/remove, `target` ∈ memory/user) so it can mirror entries — the holographic provider uses it to store a copy of every `add` as a fact.

**Evidence:** `~/.hermes/hermes-agent/agent/agent_init.py:1678-1693`; `agent/memory_provider.py:322-339`; `plugins/memory/holographic/__init__.py:245-252`.

### F10 — For a server-backed memory (e.g. the AiRaccoon MCP server): the plugin is a thin in-process Python shim [INFERRED]

Reasons from F1-F8: the ABC is pure Python with no transport abstraction — every call site is a direct method call on an instance living in the agent process, and tools must be declared as OpenAI function schemas. Nothing in the interface speaks MCP or HTTP. So wiring a remote memory backend into Hermes means writing a small `MemoryProvider` subclass whose `initialize` opens a client (e.g. to the AiRaccoon HTTP server at its MCP endpoint), whose `get_tool_schemas`/`handle_tool_call` proxy the server's tools, and whose `sync_turn`/`prefetch` call the server's search/ingest verbs (non-blocking, per the threading contract). An MCP server's tools can of course also surface directly as plain MCP tools with no Python plugin at all (`developer-guide/plugins/index.md:1119-1136`) — but that path yields no provider lifecycle (prefetch/sync_turn/session hooks), which is precisely why the shim is the memory-plugin shape. The existing seven credential/deps-gated providers (honcho, mem0, openviking, etc.) are this shape for their own cloud APIs.

**Evidence:** reasoning from the call-path evidence in F2-F7; the closest concrete precedent is the Honcho provider (cloud API behind the same ABC — `plugins/memory/honcho/`).

## Still open

- The full `hermes memory setup` wizard flow was not traced end-to-end (only the `load_memory_provider` call in `hermes_cli/memory_setup.py:219` was seen); the wizard→schema→save_config loop is documented but not verified by running it.
- Whether `plugin.yaml` is required for user-installed memory providers: the discovery heuristic does not need it, but `hermes plugins` management and `kind: exclusive` auto-detection may; not verified.
- The published docs (hermes-agent.nousresearch.com) were not re-fetched; the local `website/docs/` at the same commit was used.
- The exact prefetch threading at the run_agent level (inline call vs background pre-warm) is pinned by timeouts but was not exhaustively traced.
- Interface evolution: this snapshot is from 2026-08-05; optional hooks are additive by design (`on_session_switch` documents backward-compat defaults), but a provider written against an older snapshot may miss hooks the runtime now calls.
