# Hermes plugin deployment gap (verified 2026-08-06)

Full record: ai-badger repo `docs/work/2026-08-06-hermes-integration-diagnosis.md`
(PR #310). This reference is the condensed, durable version for framework work.

## The bug

ai-badger's Hermes-side hooks never fire. `features/hermes/adjustments/adjust_hooks.py`
copies FLAT `.py` files into `~/.hermes/plugins/` (`ai_badger_hooks.py`,
`learned_skills_sync.py`, sibling modules `memory_grade.py` / `commit_reminder.py` /
`impact_estimator.py` / `tokenizer.py` / `bm25.py` / `mcp_matcher.py`, plus a
`.ai-badger/manifest.json` record), but Hermes only loads DIRECTORY plugins.

Hermes contract (source: `hermes_cli/plugins.py` in the Hermes checkout):

- User plugins live at `~/.hermes/plugins/<name>/` containing `plugin.yaml`
  (declares `hooks: [...]`) **and** an `__init__.py` exposing `register(ctx)`.
- Callbacks are registered via `ctx.register_hook(name, cb)` for names in
  `VALID_HOOKS` — which includes `on_session_start`, `pre_llm_call`,
  `post_tool_call`, `pre_tool_call`.
- `_scan_directory` iterates subdirectories looking for `plugin.yaml`/`plugin.yml`
  and skips everything else ("if not child.is_dir (): continue") — flat files are invisible by design.
- User plugins are OPT-IN: a manifest not listed in `plugins.enabled` (config or
  `hermes plugins enable <name>`) is recorded but not loaded.

The module itself is written correctly against this ABI: `ai_badger_hooks.py:850`
`register(ctx)` calls `ctx.register_hook("on_session_start"|"pre_llm_call"|
"post_tool_call", ...)` with `**kwargs`-tolerant callbacks and a `COPY_SKEW_REFUSAL`
stale-copy gate. Only the packaging shape is wrong. Consequently all four hermes entries in `hooks-manifest.json` (drift-notice, context-enrichment, commit-reminder, memory-grade) are dead declarations.

## Evidence (all measured 2026-08-06)

- No `__pycache__` for the modules in `~/.hermes/plugins/` — Hermes never imported them.
- `~/.hermes/logs/agent.log*` mentions `ai_badger_hooks` only as pyright lint lines, never execution.
- A live `memory_search` (via Hermes MCP, `AI_BADGER_MEMORY_GRADE=1` set) appended nothing to `~/.ai-badger/memory-grade/memory-quality.jsonl`.
- The quality log's single line ("live probe wp7") came from direct helper invocation — WP7's "live probe" verified the helper pipeline, not a live host. No `memory_search`
  tool_use exists in any Claude Code transcript either.
- `~/.hermes/plugins/.ai-badger/manifest.json` records a TEMP dir as `frameworkRoot`:
  the scaffold-freshness guard (pre-commit, every commit) re-runs the hermes adjustment with a temp root and writes real user-home files as a side effect, so the recorded
  "where did this copy come from" pointer is garbage after the first commit.

## Subsumed prior work

`docs/work/2026-08-02-hermes-task-tracking.md` F3 treated the plugin as loaded ("Its register () registers exactly three hooks...") — a reading of file contents, not runtime. The task-tracking gap is a special case of this deployment bug,
not a separate limitation.

## Verification-gap pattern (the WP7 lesson)

A hook feature's acceptance gate must prove a REAL session fires it — organic
`memory_search` → line lands in the log → grade round-trip fills it in place. Running the helper directly proves the pipeline, not the integration. Same failure class as a gate that checks the artifact instead of the wiring: check "did the
host invoke it", not "does the script run".

## Fix direction (follow-up task, not yet implemented)

1. Ship `~/.hermes/plugins/ai-badger/` as a real directory plugin: `plugin.yaml`
   (`name: ai-badger`, `hooks: [on_session_start, pre_llm_call, post_tool_call]`) +
   `__init__.py` importing the shipped `register`. Sibling modules must land INSIDE the plugin dir — the lazy sibling imports resolve `Path(__file__).parent`.
2. Register via `plugins.enabled` (`hermes config set` / `hermes plugins enable
   ai-badger`) — decide whether the scaffold prints the instruction or edits config.
3. Fix the freshness-guard temp-root side effect on `~/.hermes/plugins/`.
4. Update `hooks-subsystem.md` (done — flagged STALE), `adjust_hooks.py` docstring, and extend `tests/test_hermes_plugin_install.py` to assert the plugin-dir shape.
5. Re-verify with the live-session gate that WP7 was missing (organic search → line).
