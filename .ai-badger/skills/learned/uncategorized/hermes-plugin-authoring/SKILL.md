---
name: hermes-plugin-authoring
description: >-
  Use when writing, deploying, or debugging a Hermes plugin.
version: 1.0.0
metadata:
  hermes:
    tags: [hermes, plugins, hooks, abi, lifecycle]
---

# Hermes plugin authoring

The Hermes plugin contract, verified against the live agent source 2026-08-06
(`~/.hermes/hermes-agent/hermes_cli/plugins.py`, `model_tools._emit_post_tool_call_hook`,
`hermes_cli/hooks.py` `_DEFAULT_PAYLOADS`). Covers hooks plugins (the common case);
memory-provider and model-provider plugins have their own discovery paths.

## The one contract that matters

Hermes loads user plugins **only as directory plugins**:
`~/.hermes/plugins/<name>/` containing `plugin.yaml` **and** `__init__.py` exposing
`register(ctx)`. Flat `.py` files in `~/.hermes/plugins/` are never loaded —
`_scan_directory` skips anything that is not a subdirectory containing `plugin.yaml`.
(Flat drops cost ai-badger a week of dead hooks; forensic signal: the flat modules never
produce a `__pycache__` entry and never appear in `~/.hermes/logs/agent.log`.)

## plugin.yaml

Minimal fields that matter (see `_parse_manifest`): `name` (defaults to dir name),
`version`, `description`, `kind` (default `"standalone"`; unknown kinds warn and fall
back), `provides_hooks`, plus the conventional `hooks:` list (the bundled disk-cleanup
plugin ships both). Keys are path-derived (`image_gen/openai`), so category nesting is
fine up to two levels.

**User plugins are opt-in**: a manifest not listed in `plugins.enabled` (config) is
recorded but NOT loaded — "None = opt-in default (nothing enabled)". Enable with
`hermes plugins enable <name>`; a tool-override warning there is informational unless
the plugin actually overrides built-in tools (`--allow-tool-override`).

## register(ctx) and hooks

- `__init__.py` is imported as `hermes_plugins.<slug>` with
  `submodule_search_locations=[plugin_dir]` — so `from .sibling import register` works
  and siblings inside the dir are importable (keep lazy-imported helpers in the dir).
- `register(ctx)` must call `ctx.register_hook(name, callback)` for names in
  `VALID_HOOKS`: `pre_tool_call`, `post_tool_call`, `transform_terminal_output`,
  `transform_tool_result`, `transform_llm_output`, `pre_llm_call`, `post_llm_call`,
  `pre_verify`, `pre_api_request`, `post_api_request`, `api_request_error`,
  `on_session_start`, `on_session_end`, `on_session_finalize`, `on_session_reset`,
  `on_skill_lifecycle`, `subagent_start`, `subagent_stop`, `pre_gateway_dispatch`,
  `pre_approval_request`, `post_approval_response`, kanban task hooks.
- `invoke_hook` wraps every callback in its own try/except — a raising callback logs a
  warning and the core loop continues. Callbacks should still self-guard.
- `pre_llm_call` callbacks may return `{"context": "..."}` (or a plain string) — it is
  injected into the USER message, never the system prompt (preserves the prompt-cache
  prefix). Injected context is ephemeral, never persisted.
- `post_tool_call` callbacks have NO return channel into the model context — the
  stash/pop pattern (disk stash keyed by project path, popped by `pre_llm_call`) is the
  way to inject once.

## Payload keys per hook (the trap)

Callbacks receive `**kwargs`; **no payload carries `cwd`**:

- `post_tool_call` (PLUGIN emitter, `model_tools._emit_post_tool_call_hook`):
  `function_name`, `function_args`, `result`, `session_id`, `task_id`, `tool_call_id`,
  `turn_id`, `api_request_id`, `duration_ms`, `status`, `error_type`, `error_message`,
  `middleware_trace`. Note: the shell-hook spelling (`tool_name`, `args`, `cwd` — see
  `agent/shell_hooks.py` / `hermes hooks test`) is a DIFFERENT surface; plugins get
  `function_name`/`function_args`.
- `pre_llm_call`: `session_id`, `user_message`, `conversation_history`, `is_first_turn`,
  `model`, `platform`.
- `on_session_start`: `session_id`.
- `pre_tool_call`: `tool_name`, `args`, `session_id`, `task_id`, `tool_call_id`.

Adapters must normalize both spellings (`tool_name|function_name`, `args|function_args`)
and derive the project dir via `os.getcwd()` at callback time — matching what the
`pre_llm_call` pop side resolves (`_project_cwd(os.getcwd())`). Keep stash keys and pop
keys symmetric or the round-trip silently misses (test with `monkeypatch.chdir`).

## Verification recipe (prove it, don't assume)

1. Install the plugin dir (scaffold adjustment or manual copy).
2. `hermes plugins enable <name>` — then `hermes plugins list` shows
   name/enabled/version/source.
3. `HERMES_PLUGINS_DEBUG=1` surfaces discovery logs to stderr + `~/.hermes/logs/agent.log`
   (which dirs scanned, which manifests parsed, why a plugin was skipped).
4. **Live gate**: hooks load at SESSION START — enabling mid-session changes nothing in
   the current session. Run a fresh `hermes chat -q "<prompt that exercises the hook>"`
   and check the side effect (log line, file, message). A one-shot session is the only
   honest end-to-end probe.
5. For script-hook payload shapes, `hermes hooks test`/`doctor` show `_DEFAULT_PAYLOADS`;
   for the plugin emitter, read `model_tools._emit_post_tool_call_hook`.

## Pitfalls

- **Flat `.py` deployment = dead plugin** — invisible to `_scan_directory`; no pycache,
  no log lines. The ai-badger fix (reference implementation) is
  `features/hermes/adjustments/adjust_hooks.py` (installer: plugin dir + manifest inside
  it) and `features/common/hooks/ai_badger_hooks.py` (register + payload adapter), with
  contract tests in `tests/test_hermes_plugin_install.py` and
  `tests/test_hermes_plugin_payloads.py`.
- **`plugins.enabled` opt-in** — enabled=false until explicitly enabled; document the
  enable command in the scaffold notes.
- **Payload spelling mismatch** — `function_args` not `args`, `function_name` not
  `tool_name`; normalize both, never assume one.
- **Staleness/skew records** — if the module has a "copies are stale, refuse to
  register" check, the installer record must sit where the checker reads it (ai-badger's
  `badger_lib.copy_skew` reads `copies_dir/.ai-badger/manifest.json` — INSIDE the plugin
  dir, not beside it, or the protection silently no-ops).
- **Graceful degradation** — `register()` returning early (stale copies, missing
  framework root) is fine: the plugin loads, hooks absent, session unaffected. A dead
  recorded `frameworkRoot` (e.g. a temp scaffold dir) degrades to no-version-context,
  never a broken session.
- **Legacy cleanup** — when moving from a flat layout to the directory shape, delete the
  old flat files and the old manifest dir (only framework-owned names) so the loader
  scans a clean user scope.
