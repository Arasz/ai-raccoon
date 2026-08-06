# Hermes plugin ABI vs ai-badger's deployment (verified 2026-08-06)

## The Hermes plugin contract (from ~/.hermes/hermes-agent/hermes_cli/plugins.py)

- **Directory plugins only**: `~/.hermes/plugins/<name>/` must contain `plugin.yaml`
  (or `.yml`) AND `__init__.py` exposing `register(ctx)`. Sources: bundled
  `<repo>/plugins/<name>/`, user `~/.hermes/plugins/`, project `./.hermes/plugins/`
  (opt-in via `HERMES_ENABLE_PROJECT_PLUGINS`), pip `hermes_agent.plugins` entry points.
- `_scan_directory` iterates SUBDIRECTORIES looking for `plugin.yaml`; non-directories
  are skipped entirely — flat `.py` files in `~/.hermes/plugins/` are invisible.
- `plugin.yaml` shape (example: bundled `plugins/disk-cleanup/plugin.yaml`):
  `name`, `version`, `description`, `author`, `hooks: [post_tool_call, on_session_end]`.
- `register(ctx)` registers callbacks via `ctx.register_hook(name, callback)`.
  `VALID_HOOKS` (plugins.py:135-160) includes `pre_tool_call`, `post_tool_call`,
  `pre_llm_call`, `post_llm_call`, `on_session_start`, `on_session_end`, `pre_verify`,
  `transform_tool_result`, `transform_llm_output`, `pre_api_request`,
  `post_api_request`, `api_request_error`, `on_skill_lifecycle`, `subagent_start/stop`,
  `pre_gateway_dispatch`, approval hooks, kanban task hooks.
- **User plugins are OPT-IN**: loaded only when listed in `plugins.enabled` (config or
  `hermes plugins enable <name>`); "None = opt-in default (nothing enabled)". Explicit
  disable wins. Later sources override earlier on name collision (user > bundled,
  project > user).
- Debug aid: `HERMES_PLUGINS_DEBUG=1` tees verbose discovery logs to stderr + agent.log.

## The mismatch (why the ai-badger hermes hook surface is dead)

`features/hermes/adjustments/adjust_hooks.py` copies FLAT `.py` files
(`ai_badger_hooks.py`, `learned_skills_sync.py` + siblings `memory_grade.py`,
`commit_reminder.py`, `impact_estimator.py`, `tokenizer.py`, `bm25.py`,
`mcp_matcher.py`) directly into `~/.hermes/plugins/`, plus
`~/.hermes/plugins/.ai-badger/manifest.json` (frameworkRoot + version record).
Result: zero `plugin.yaml` manifests → zero plugins loaded → `register()` never
called → all four hermes entries in `hooks-manifest.json` (drift-notice,
context-enrichment, commit-reminder, memory-grade) are dead declarations.

The module itself is ABI-correct: `register(ctx)` with `ctx.register_hook(...)` on
valid names, `**kwargs`-tolerant callbacks, `COPY_SKEW_REFUSAL` gate. Only the
packaging shape is wrong.

## Empirical verification recipe (a module's presence ≠ execution)

1. `find ~/.hermes -name 'plugin.yaml'` → empty means no directory plugins exist.
2. No `__pycache__` for the plugin modules in `~/.hermes/plugins/` → never imported.
3. `grep ~/.hermes/logs/agent.log*` for the plugin logger name → only lint mentions,
   no execution lines.
4. Decisive live test: trigger the watched action (e.g. `memory_search` via MCP) and
   check the hook's output artifact (e.g. `~/.ai-badger/memory-grade/memory-quality.jsonl`)
   gains a line.

## Trap: reading file contents as runtime behavior

The 2026-08-02 task-tracking research (`docs/work/2026-08-02-hermes-task-tracking.md`
F3) described "the installed plugin registers exactly three hooks" by reading
`register()`'s body — the file never executes. Always verify EXECUTION (pycache /
logs / live artifact), not presence or contents.

## Payload keys per hook (verified 2026-08-06 — the adapter trap)

Callbacks receive `**kwargs`; **no payload carries `cwd`**:

- `post_tool_call` (PLUGIN emitter, `model_tools._emit_post_tool_call_hook`):
  `function_name`, `function_args`, `result`, `session_id`, `task_id`, `tool_call_id`,
  `turn_id`, `api_request_id`, `duration_ms`, `status`, `error_type`, `error_message`,
  `middleware_trace`. The shell-hook spelling (`tool_name`, `args`, `cwd` —
  `agent/shell_hooks.py` / `hermes hooks test` `_DEFAULT_PAYLOADS`) is a DIFFERENT
  surface; plugins get `function_name`/`function_args`.
- `pre_llm_call`: `session_id`, `user_message`, `conversation_history`, `is_first_turn`,
  `model`, `platform`.
- `on_session_start`: `session_id`.

Adapters must normalize both spellings (`tool_name|function_name`, `args|function_args`)
and derive the project dir via `os.getcwd()` at callback time — matching the
`pre_llm_call` pop side's `_project_cwd(os.getcwd())`; keep stash/pop keys symmetric or
the round-trip silently misses (test with `monkeypatch.chdir`). `__init__.py` imports as
`hermes_plugins.<slug>` with `submodule_search_locations=[plugin_dir]`, so
`from .ai_badger_hooks import register` works and lazy-imported siblings must live
INSIDE the plugin dir.

## Fix direction — IMPLEMENTED (0.80.0, PR #311)

Shipped: `~/.hermes/plugins/ai-badger/` with `plugin.yaml`
(`hooks: [on_session_start, pre_llm_call, post_tool_call]`) + `__init__.py`
re-exporting `register`; sibling modules land INSIDE the plugin dir (lazy
sibling imports resolve `Path(__file__).parent`); legacy flat copies + old manifest dir
removed; the installer record moved INTO the plugin dir (`.ai-badger/manifest.json`) so
`badger_lib.copy_skew` (reads `copies_dir/.ai-badger/manifest.json`) still judges the
copies; `post_tool_observer` payload normalization; memory-grade JSONL line gains
`host` + `sessionId` (null in manual lines); scaffold notes print
`hermes plugins enable ai-badger` (plugins.enabled — never hand-edit config.yaml).
Live gate passed: `hermes plugins list` shows the plugin enabled; a fresh
`hermes chat -q` session's `memory_search` appended the first organic quality-log line
with `host: hermes`.

Remaining known issue: `scaffold_freshness_guard` re-runs the hermes adjustment with a
TEMP framework root on every commit — the recorded `frameworkRoot` in the plugin-dir
manifest then points at a temp dir (cosmetic; degrades gracefully to no-version-context,
never breaks the session).
