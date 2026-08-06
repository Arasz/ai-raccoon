# Writing a memory provider plugin — implementation + testing notes

From implementing the ai-raccoon provider (MemoryProvider shim over the
AiRaccoon MCP server, 2026-08-06, PR #61). Interface background:
`references/memory-provider-interface.md` in this skill.

## Structure that works

- Plugin dir `$HERMES_HOME/plugins/<provider-name>/` (or bundled
  `plugins/memory/<provider-name>/`): `__init__.py` (provider class +
  `register(ctx)`), `client.py` (backend client), `plugin.yaml`, README.
- Constructor takes `config: dict | None` AND an optional `client_factory`
  kwarg so unit tests inject a fake client. `register(ctx)` constructs with
  config only; `initialize` uses `factory(config)` then `client.connect()`.
- Keep hermes-internal imports (`hermes_cli.config`) LAZY inside functions —
  the class then imports cleanly under plain pytest with the hermes venv.

## Pitfalls hit

1. **The fake client must implement `connect()`.** `initialize()` calls
   `factory(config)` and then `client.connect()`. A fake without `connect()`
   raises AttributeError, which initialize SWALLOWS (graceful-failure
   design) — the provider silently runs client-less and every subsequent
   test fails in a confusing way. The fake's `connect()` is a no-op.
2. **Discovery heuristic reads only the first 8 KB of `__init__.py`** — it
   scans for `register_memory_provider` OR `MemoryProvider`. A `register()`
   at the end of a long file is invisible to discovery. Put the marker in
   the module docstring / class docstring near the top. (Verified: a
   14 KB plugin with the marker only in the docstring IS discovered.)
3. **Provider dir name = provider name, and it may be unhyphenatable**
   (`ai-raccoon`). Python cannot `import ai-raccoon`; tests must spec-load
   the plugin the same way the Hermes loader does:
   `importlib.util.spec_from_file_location(name, path)` + exec_module
   (the loader pre-registers sibling `*.py` modules in sys.modules, so the
   plugin's own relative imports resolve at runtime — absolute-import
   fallback `except ImportError: from client import ...` keeps tests simple).
4. **`is_available()` must never construct the client** (ABC: no network
   calls). Binary resolution via `shutil.which` for stdio, non-empty url for
   http. Keep the factory out of it.
5. **`sync_turn` threading**: capture `client`/`project_id` into locals
   before defining the worker closure (avoids closure-narrowing bugs and
   None races), start a daemon thread, keep a list joined on `shutdown()`
   (5 s timeout). Skip writes unless `agent_context in (None, "primary")`.
6. **`handle_tool_call` must return a JSON string always** — including
   errors (`json.dumps({"error": ...})`). The ABC contract.

## Testing recipe

- Unit tests: fake client + `client_factory` injection; assert arg
  forwarding (projectId injected, camelCase→snake mapping), prefetch block
  formatting against the REAL result shape (`{"results": [{snippet,
  ranking, ...}]}`), JSON-string returns, mirror rules (add only).
- Integration (slow, `--run-slow` marker): spawn the REAL installed binary
  via the provider's stdio transport with `AIRACCOON_DATA_ROOT=<tmp>`
  (child inherits env → temp bank, real bank untouched). FTS-only mode
  still answers searches — pass `minScore: 0.0` to avoid threshold flakes.
  Assert write→search round trip, prefetch hit, stats entries, clean
  shutdown (session None, loop thread dead).
- Discovery proof: copy the plugin into a TEMP `HERMES_HOME/plugins/`,
  run the loader probe (`load_memory_provider("<name>")` from the hermes
  checkout venv) — server logs from the spawned child confirm the full
  lifecycle. Never point the probe at the real `~/.hermes` when the
  provider spawns a server child.
- Run everything with the hermes venv python
  (`~/.hermes/hermes-agent/venv/bin/python -m pytest`) — it has `mcp` and
  `pytest`; a bare repo .venv has neither.

## Activation notes

- `memory.provider` is single-select: activating a new provider REPLACES the
  current one (e.g. holographic) — its DB stays on disk, its tools stop
  being injected. Rollback = flip the config key back.
- The plain-MCP server registration in config.yaml can coexist with the
  provider (full tool surface via MCP; curated surface + lifecycle via the
  provider). `on_memory_write` mirrors built-in memory writes (add only in
  v1; remove/replace need hash tracking).
