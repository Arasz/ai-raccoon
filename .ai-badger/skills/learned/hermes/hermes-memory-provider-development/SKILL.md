---
name: hermes-memory-provider-development
description: Use when building or testing Hermes memory provider plugins.
---

# Hermes memory provider plugins

Writing a plugin that plugs into Hermes Agent's memory system as the single external
memory provider (an additional recall channel alongside built-in MEMORY.md/USER.md).
Verified against the Hermes source checkout (agent/memory_provider.py, agent/memory_manager.py,
plugins/memory/__init__.py, 2026-08-06) and a working implementation (AiRaccoon provider,
see references/ai-raccoon-bridge.md).

## The interface — one Python ABC

`agent.memory_provider.MemoryProvider` (imported from the hermes runtime; the plugin runs
IN-PROCESS, every call is a direct method call — no IPC in the interface).

Abstract (must implement):
- `name` (property) — provider name == plugin directory name
- `is_available()` — config/deps check ONLY; the ABC contract forbids network calls and
  client construction here
- `initialize(session_id, **kwargs)` — kwargs always include `hermes_home`, `platform`; may
  include `agent_context` (primary/subagent/cron/flush), `agent_identity` (profile),
  `agent_workspace`, `parent_session_id`, `user_id`. Called once at startup; failure must be
  graceful (log + continue client-less)
- `get_tool_schemas()` — OpenAI function-calling format `[{name, description, parameters}]`
- `handle_tool_call(tool_name, args, **kwargs)` → JSON **string** (default raises
  NotImplementedError; required if tools are exposed)

Optional hooks (override to opt in): `system_prompt_block()` (static system-prompt text),
`prefetch(query, *, session_id="")` (fast recall before each turn; "" = nothing),
`queue_prefetch`, `sync_turn(user, assistant, *, session_id, messages)` (MUST be
non-blocking — daemon thread), `shutdown()` (deterministic + idempotent),
`on_turn_start`, `on_session_end` (real session boundaries only), `on_session_switch`
(/resume /branch /reset /new), `on_pre_compress` (return text folded into the compression
summary), `on_memory_write(action, target, content, metadata)` (mirror built-in writes:
action add/replace/remove, target memory/user), `on_delegation` (parent-side subagent
observation), `get_config_schema` / `save_config`, `backup_paths` (state kept outside
HERMES_HOME must be declared for `hermes backup`).

## Registration & discovery

- Entry point: `def register(ctx): ctx.register_memory_provider(MyProvider())`. A bare
  `MemoryProvider` subclass in the module also loads.
- Discovery dirs: bundled `plugins/memory/<name>/` and user `$HERMES_HOME/plugins/<name>/`.
  Heuristic: `__init__.py`'s FIRST 8192 bytes contain `register_memory_provider` OR
  `MemoryProvider` — keep one marker in the module docstring. Bundled wins collisions.
- Provider name == directory name. A hyphenated dir (e.g. `ai-raccoon`) loads fine at
  runtime (file-location loads; the loader pre-registers sibling modules in sys.modules so
  relative imports resolve) but is NOT importable as a normal Python package — tests must
  spec-load it.
- `plugin.yaml` optional for discovery (description shows in listings); the manifest schema
  field for hooks is `provides_hooks`.
- Selection: single-select via `memory.provider` config key; empty = built-in only. Only ONE
  external provider active — a second is rejected with a warning. Provider tools must not
  shadow reserved core tool names (rejected at registration) — prefix your tools
  (`memory_*` etc. is safe; bare `memory` is taken).

## Runtime call points (MemoryManager)

System prompt assembly → `system_prompt_block()`; pre-turn → `prefetch_all` (gated by
`is_trivial_prompt` at agent/turn_context.py:1171 — greetings skip recall; skill scaffolding
stripped; external prefetch bounded ~8s); post-turn → `sync_all` + `queue_prefetch_all` on a
background executor (~5s drain); tool injection at agent init (gated by the `memory`
toolset, name-collision skip); `handle_tool_call` dispatch; session-boundary hooks.

## Threading & lifecycle contract

- `sync_turn` MUST be non-blocking: daemon thread, join the previous sync thread (≤5s)
  before starting the next.
- `prefetch` must be fast — background the real recall and return cached results if needed.
- Writes only for primary agents: when `agent_context` is not primary (cron/subagent),
  skip sync.
- Profile isolation: storage paths from the `hermes_home` kwarg, never hardcoded `~/.hermes`.
- Re-init must close the previous client (double initialize would leak the child).

## Config surface

`get_config_schema()` field dicts drive `hermes memory setup`: `secret: True` + `env_var`
→ .env; non-secrets → `save_config(values, hermes_home)`. Providers can read their own
block via `cfg_get(load_config_readonly(), "plugins", "<name>")`. Optional `cli.py` with
`register_cli(subparser)` registers `hermes <provider>` subcommands, gated on being the
active provider.

## MCP-bridge pattern (server-backed providers)

A remote memory server plugs in as a thin in-process Python shim implementing
MemoryProvider. Use the official `mcp` SDK — it ships in the hermes venv
(`~/.hermes/hermes-agent/venv/`, with pytest 9.x — that venv is the plugin test runtime).
- Import `mcp` LAZILY inside `connect()` so unit tests with a fake client never need it.
- Sync-over-asyncio: persistent loop thread + `asyncio.run_coroutine_threadsafe`; for stdio
  the child must NOT be re-spawned per call (session is persistent).
- `stdio_client` yields a **2-tuple** (read, write); `streamable_http_client` yields a
  **3-tuple** (read, write, get_session_id) — verify against the installed SDK, they differ.
- `StdioServerParameters(command, args)` — pass CLI flags through `args` (e.g. `--data-root`)
  for test isolation.
- On connect timeout, CANCEL the pending `_open` task before closing the loop, or a
  half-spawned child leaks.
- Tool results: `CallToolResult.content[0].text` is a JSON string — parse, don't re-wrap.

## Testing patterns

- Unit: duck-typed fake client (connect/search/write/stats/share/close), injected via a
  `client_factory` ctor param. Provider must behave client-less: prefetch → "",
  handle_tool_call → `{"error": ...}` JSON.
- Spec-load the plugin module in pytest: `importlib.util.spec_from_file_location` +
  sys.modules registration; conftest adds the plugin dir to sys.path for the
  absolute-import fallback inside the plugin.
- Integration (slow marker + `--run-slow`): spawn the REAL server with a temp data root via
  spawn args; **fail (not skip) on spawn failure** when --run-slow was explicitly requested;
  binary-missing is a skip.
- Pin result shapes against the server's real records — read the server source, don't guess.

## Pitfalls

- **Test isolation is only as real as the spawn args.** If the server CLI resolves its data
  root ONLY from a flag (e.g. `--data-root`), an env var that works for in-process test
  hosts is IGNORED by the spawned binary → integration tests silently write into the REAL
  bank. Pass the flag through spawn args AND verify isolation by counting your test
  project's rows in the real store before and after the run.
- `is_available()` must not construct the client — pin with a test whose factory raises.
- Do not expose server-injected params (projectId) in model-facing tool schemas — the
  provider injects them at dispatch.
- The loader heuristic scans only the first 8192 bytes of `__init__.py`.

## References

- `references/ai-raccoon-bridge.md` — AiRaccoon-specific record: ratified design decisions,
  tool contracts + result shapes, the real-bank isolation incident, HTTP smoke recipe.
