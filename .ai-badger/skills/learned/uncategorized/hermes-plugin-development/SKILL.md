---
name: hermes-plugin-development
description: Use when writing or debugging Hermes Agent Python plugins.
version: 1.0.0
metadata:
  hermes:
    tags: [hermes, plugins, hooks, abi, debugging]
    related_skills: [hermes-agent, ai-badger-task-orchestration]
---

# Hermes plugin development

Hermes loads agent plugins as DIRECTORY plugins, not loose `.py` files. Most "my hook
never fires" reports trace to a packaging-shape mismatch. Everything below was verified
against `hermes_cli/plugins.py` in the Hermes source checkout (`~/.hermes/hermes-agent/`),
2026-08-06.

## The ABI (verified)

- **Discovery sources** (plugins.py:1350-1393): bundled `<repo>/plugins/<name>/`; user
  `~/.hermes/plugins/<name>/`; project `./.hermes/plugins/<name>/` (opt-in via
  `HERMES_ENABLE_PROJECT_PLUGINS=1`); pip packages exposing the `hermes_agent.plugins`
  entry-point group. Later sources override earlier on name collision.
- **A directory plugin MUST contain `plugin.yaml` AND `__init__.py` with a
  `register(ctx)` function.** `_scan_directory` skips anything that is not a subdirectory
  containing `plugin.yaml`/`plugin.yml` (`if not child.is_dir(): continue`) — flat `.py`
  files in `~/.hermes/plugins/` are INVISIBLE to the loader. Category layout
  (`<root>/<category>/<name>/plugin.yaml`) is supported, depth capped at two segments.
- **plugin.yaml shape** (example: bundled `plugins/disk-cleanup/plugin.yaml`):
  `name`, `version`, `description`, `author`, `hooks: [<VALID_HOOKS names>]`.
- **register(ctx)**: callbacks registered with `ctx.register_hook("<hook_name>", cb)`.
  Callbacks must tolerate `**kwargs` (forward compatibility). Declare hook names in the
  manifest's `hooks:` list.
- **VALID_HOOKS** (plugins.py:135-216): `pre_tool_call`, `post_tool_call`,
  `transform_terminal_output`, `transform_tool_result`, `transform_llm_output`,
  `pre_llm_call`, `post_llm_call`, `pre_verify`, `pre_api_request`, `post_api_request`,
  `api_request_error`, `on_session_start`, `on_session_end`, `on_session_finalize`,
  `on_session_reset`, `on_skill_lifecycle`, `subagent_start`, `subagent_stop`,
  `pre_gateway_dispatch`, `pre_approval_request`, `post_approval_response`, plus the
  `kanban_*` task-lifecycle hooks.
- **User plugins are OPT-IN**: "None = opt-in default (nothing enabled)" — a manifest not
  listed in `plugins.enabled` (config) is recorded but NOT loaded. Enable via
  `hermes plugins enable <name>` or `hermes config set plugins.enabled [...]`. Bundled
  `backend`/`platform` plugins auto-load; standalone, user-installed and entry-point
  plugins all require `plugins.enabled`.
- **Debugging**: `HERMES_PLUGINS_DEBUG=1` prints verbose discovery logs (scanned dirs,
  parsed manifests, skip reasons, what `register()` registered) to stderr AND
  `~/.hermes/logs/agent.log`.

## Memory provider plugins (specialized type)

Memory providers are a SPECIALIZED plugin type, not plain hook plugins: single-select,
routed through `memory.provider` in config.yaml (NOT `plugins.enabled`), auto-detected as
`kind: exclusive`. The interface is the `MemoryProvider` ABC in
`agent/memory_provider.py` — 4 abstract members (`name`, `is_available()`,
`initialize(session_id, **kwargs)`, `get_tool_schemas()`) plus ~14 optional hooks
(`prefetch`, `sync_turn`, `on_session_end`, `on_session_switch`, `on_memory_write`,
`get_config_schema`, `save_config`, `backup_paths`, ...). Registration:
`register(ctx)` → `ctx.register_memory_provider(provider)`. Discovery scans bundled
`plugins/memory/<name>/` and `$HERMES_HOME/plugins/<name>/` (text heuristic:
`register_memory_provider` or `MemoryProvider` in `__init__.py`; bundled wins on
collision; a bare MemoryProvider subclass also loads). Tool schemas are OpenAI
function-calling format; `handle_tool_call` returns a JSON string. The provider runs
IN-PROCESS — no MCP/IPC anywhere in the call path, so a server-backed memory (e.g. an
MCP memory server) plugs in as a thin Python shim. Full ABC surface, per-turn call
points, threading contract, config/CLI surfaces, and a live loader probe:
`references/memory-provider-interface.md`.

## Empirical verification — a plugin file present ≠ a plugin loaded

Never infer execution from file contents (a `register()` body reads as if it runs) or
from a manifest that declares the hook. Check in order:

1. **`__pycache__`**: a loaded plugin module leaves a pycache next to it. Absent pycache
   in `~/.hermes/plugins/` = never imported.
2. **Logs**: `grep <plugin-name> ~/.hermes/logs/agent.log*` for execution lines (logger
   output, "Plugin discovery complete: N found, M enabled") — not lint mentions.
3. **Live probe**: trigger the hook's event and observe its side effect (e.g. run
   `memory_search` with the grade env on, then confirm the JSONL line lands).

## Pitfalls

- **Flat-file deployment (the ai-badger case, diagnosed 2026-08-06).**
  `features/hermes/adjustments/adjust_hooks.py` copies loose `.py` files into
  `~/.hermes/plugins/` (plus a `.ai-badger/manifest.json` record). Zero manifests →
  zero plugins loaded → none of the four hermes entries in `hooks-manifest.json`
  (drift-notice, context-enrichment, commit-reminder, memory-grade) has EVER fired in a
  Hermes session — yet the module's `register(ctx)` + `ctx.register_hook(...)` calls are
  written correctly against the ABI; only the packaging shape is wrong. Full diagnosis:
  ai-badger repo `docs/work/2026-08-06-hermes-integration-diagnosis.md` (PR #310). Fix
  direction: real directory plugin, `plugins.enabled` registration, and a live-session
  verification gate (the WP7-style probe that was missing).
- **Sibling modules must ship INSIDE the plugin directory**: lazy sibling imports resolve
  `Path(__file__).parent` — a module that loads `memory_grade.py`/`commit_reminder.py`
  from its own dir breaks if siblings land elsewhere.
- **Staleness/refusal guards run at register() time** — a `COPY_SKEW_REFUSAL`-style gate
  never runs if the plugin never loads; it cannot be the only protection.
- **Prior research can misread file contents as runtime**: `docs/work/2026-08-02-hermes-task-tracking.md`
  treated the installed `register()` body as proof of registration. Any "the plugin
  registers X" claim needs the empirical checks above.

## Verification checklist

- [ ] Plugin lives at `~/.hermes/plugins/<name>/` with `plugin.yaml` + `__init__.py`
- [ ] `hermes plugins list` shows it enabled (or `plugins.enabled` contains its key)
- [ ] pycache exists / agent.log shows registration after a fresh session
- [ ] Live side-effect probe confirms the hook fires end-to-end
