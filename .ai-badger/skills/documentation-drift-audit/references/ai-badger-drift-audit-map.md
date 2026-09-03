# ai-badger drift-audit map (verified 2026-08-02/03 during hermes-task-tracking audit)

Where mechanisms live and how they behave, verified by reading the tree and a real
scaffolded project (ai-raccoon: `agents: [claude, copilot, hermes]`,
`stacks: [dotnet, mcp]`). **Re-verify before citing — framework code moves.**

## Scaffolder copy behavior — no per-agent script filtering

- Scaffold package: `features/common/skills/welcome-ai-badger/scripts/`
  (`scaffold.py`, `skill_delivery.py`, `extensions.py`, `_shared.py`, `detect.py`,
  `hook_wiring.py`, `template_rendering.py`, `agent_files.py`, `statusline_wiring.py`,
  `mcp_tools.py`).
- `skill_delivery.py` `scaffold_skills()`: whole skill dir copied via
  `shutil.copytree(src, dest, ignore=_test_ignore)` — **no agent-based filtering of
  `scripts/`**. Only `config.exclude` patterns filter (plus `_test_ignore` which drops
  tests/`__pycache__`/`*.pyc`).
- Consequence: `session_start_hook.py`, `stop_hook.py`, `poll_limit.py` are present in
  every scaffolded project including hermes-only ones. Under Hermes they are inert:
  Hermes executes Python plugins, not `hooks.json`.

## Extension gating — `requires` semantics

- `extensions.py::prune_inline_extensions` reads `extension.json` `requires` from each
  `<skill>/extensions/<name>/`; unmet → dir pruned after copytree.
- `_shared.py::requirement_met` → `_condition_met` → `cfg_get(config, path)` reads
  **config literally** (list membership for `=`/`==`; `||` = OR; bare key = presence).
  `stacks=hermes` checks `config["stacks"]` only — NOT the resolved stacks
  (which `badger_lib.resolve_stacks` augments with agents).
- Asymmetry (decision-gap): `task/extensions/claude/extension.json` requires
  `agents=claude`; `hermes/extension.json` requires `stacks=hermes` (unchanged since
  commit 7fdcb415, #46); `github` requires `sourceControl.platform==github` + `repoUrl`.
  A project with hermes only in `agents` (ai-raccoon) gets the scripts but the hermes
  extension.md is **pruned** — the doc explaining the inert scripts is absent exactly
  where needed.
- `detect.py`: hermes lands in `agents` (via `.hermes.md`/`HERMES.md`/`~/.hermes`) AND
  in `stacks` (stack loop over `features/hermes/stack.json` detectionSignals —
  same signals). Hand-authored configs can diverge.
- Extensions are merged into SKILL.md only when it contains the
  `<!-- MERGE_EXTENSIONS -->` sentinel; the `task` skill has none, so its
  `extensions/*/extension.md` files ship whole into
  `.ai-badger/skills/task/extensions/<agent>/`.

## Hook surface

- `features/common/hooks/hooks-manifest.json`: 8 hooks; hermes entries on
  `drift-notice` (`on_session_start`), `context-enrichment` (`pre_llm_call`),
  `commit-reminder` (`post_tool_call`). The four task hooks — `session-start-tracking`,
  `task-checkpoint`, `task-checkpoint-session-end`, `dispatch-gate` — are claude-only.
- `features/common/hooks/hooks.json`: wires 7 Claude commands (UserPromptSubmit →
  context_enrichment_hook; SessionStart → session_start_hook + drift_notice_hook; Stop
  and SessionEnd → stop_hook; PreToolUse → dispatch_gate_hook; PostToolUse →
  commit_reminder_hook). `${CLAUDE_PLUGIN_ROOT}` paths — Claude-only by construction.
- `features/common/hooks/ai_badger_hooks.py::register()` (L798-815): exactly three
  hooks — `on_session_start` (drift notice only), `pre_llm_call` (context; usage hints
  once per session via `_session_hints_shown` — the hook fires per turn, the hints
  don't), `post_tool_call` (observer / learned-skills sync / commit reminder). No
  task-tracking hook.
- `tooling/validate.py` `HOOKS_MANIFEST_AGENT_EXEMPTIONS` (L84-130): hermes exempted
  from the four task hooks with "Claude-only by design / no Hermes analogue" reasons;
  `prompt-markers` hermes = "acknowledged gap". Those reasons rest on premises the
  research record falsified (state.db stores per-model/per-subagent tokens;
  `post_api_request`/`subagent_stop` hook payloads carry session_id + usage).

## Task tracker under Hermes (measured)

- All tracked-task token checkpoints are all-zero under Hermes; only manual
  `task_tracker.py subagent` writes numbers. `resumeCommand` strings record
  `claude --resume <hermes-session-id>` — a Claude command for a Hermes id.
- `tracker_lib.py` resolves sessions from `CLAUDE_CODE_SESSION_ID` + the `sessions` table
  of the tracking DB (written only by Claude hooks); never reads `HERMES_SESSION_ID`.
- Hermes `~/.hermes/state.db`: `sessions` (input/output/cache/reasoning tokens,
  api_call_count, model, cwd, git_repo_root), `session_model_usage` (per-model
  breakdown = the tracker's `byModel` shape), `async_delegations.result_json`
  (per-delegation `tokens {input, output}`). All data the tracker lacks is present.

## Freshness mirror

- `.ai-badger/` at repo root is a committed, git-tracked mirror of the repo scaffolded
  against itself (156 files, byte-identical to `features/` until a features edit).
- `gates/scaffold_freshness_guard.py` (no refresh mode): re-scaffolds a throwaway copy
  and diffs; any `features/**` edit that never reached `.ai-badger/` fails as "stale".
  Refresh by re-running the scaffolder on the repo itself. Editing only
  features/docs per scope constraints ⇒ flag the stale mirror in the audit report.
