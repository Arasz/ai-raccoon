# ai-raccoon refresh 0.79.1 -> 0.80.0, 2026-08-06 (worked example)

den-refresh run with `--root /Users/arasz/RiderProjects/ai-badger` (VERSION 0.80.0,
matching the user's expectation). `$AI_BADGER` unset; framework python
`.venv/bin/python3` (jsonschema). Committed directly on main as
`237dba8 chore: bump ai-badger framework to 0.80.0 — Hermes hooks as directory plugin,
host/sessionId memory-grade` (16 files, +558/-184), pushed `c1709c1..237dba8`.

## What 0.80.0 delivered (the report's drift was empty — all changes were content)

- **Hermes hooks now ship as a real directory plugin** (`~/.hermes/plugins/ai-badger/`
  with `plugin.yaml` + `__init__.py` re-exporting `register`). This fixes the 0.79.x
  flat-`.py`-drop shape that Hermes never loaded. On this machine the plugin was already
  installed and enabled (`plugins.enabled: [ai-badger]` in `~/.hermes/config.yaml`);
  verify byte-match: `diff -q ~/.hermes/plugins/ai-badger/{ai_badger_hooks.py,memory_grade.py}
  .ai-badger/hooks/...`.
- `post_tool_observer` normalizes the Hermes emitter payload (`function_name` /
  `function_args` / `session_id`, no `cwd` -> `os.getcwd()` fallback) vs the shell-hook
  spelling (`tool_name` / `args` / `cwd`) — the observer now works under either transport.
  `function_args` may arrive as JSON text and is parsed.
- memory_grade logging carries `host` + `sessionId`; `ai-raccoon-memory` SKILL.md gained a
  host-coverage section and capture-verification checklist; JSONL line shape is now a
  superset: `ts, query, scope, projectId, workspaceId, host, sessionId, result,
  usefulness, note`.
- New managed module: `.ai-badger/hooks/debug_log.py` (append-only audit log used by
  call-behaviorist; byte-identical to `features/common/hooks/debug_log.py`).
- Everything else was version stamps (config.json, manifest.json, all agent files) — root
  HERMES.md/.hermes.md/CLAUDE.md diffs were stamp-only, confirming nothing was dropped.

## Report-shape notes

- `reScaffolded: false` on run #2 after run #1 did the work (first-run trap again, see
  refresh-ai-raccoon-2026-08.md). Confirmed application via `git diff HEAD` on
  config.json/manifest.json rather than trusting the second report.
- `frameworkVersion` block: `{scaffolded, current, manifest}` all `0.80.0` after the bump.
- `skillUsage`: zero evidence channels (no Claude Code transcripts, no audit records) ->
  nothing reported unused, `hint` = enable call-behaviorist audit (`behaviorist.py on 4h`).
- `frameworkCopies`: 19 historical Claude Code plugin-cache versions (0.61.2..0.77.3),
  all `prunable: false`, owner claude-code — report only.
- `hermesSkillLinks.created`: 22 links ensured, 0 removed.

## Staging hygiene (concurrent sessions)

Pre-refresh `git status` already showed status-notes.json + task-tracking/* modified and
stack-ignore.json + two `.bak-20260805-*` files untracked. Mid-session a parallel agent
started editing `tests/AiRaccoon.Tests/Unit/Setup/McpServerSetupHostTests.cs`. Staged the
16 refresh paths explicitly (`.ai-badger/` managed files + root HERMES.md/.hermes.md/
CLAUDE.md + `.github/copilot-instructions.md`), never `git add -A`; post-commit status
confirmed only the foreign dirt remained. `.ai-badger.bckp` was already gitignored.
