# ai-raccoon refresh, 2026-08 (worked example + framework facts)

den-refresh run on the ai-raccoon repo. Two real-world shapes worth recognizing:

## Drift shapes seen in one refresh

- **configChanged with summary drift**: config.json's `project.summary`/`project.domain`
  had been edited by the project (old text: "random-number generation tools"; new text:
  "agent memory management over sqlite-memory"). The re-scaffold propagated the new text
  into every managed agent file (CLAUDE.md, .hermes.md, HERMES.md, .ai-badger/*,
  .github/copilot-instructions.md) — same 4-line summary/domain swap in each. This is the
  expected `drift.configChanged` regeneration, not data loss. Seed-once files
  (state.json, markers-context.json, model.json) were untouched.
- **frameworkCommit/frameworkDirty in manifest.json**: manifest recorded the old framework
  commit with `"frameworkDirty": true`; after refresh it recorded a new commit with
  `"frameworkDirty": false`. A dirty framework checkout stamps dirty into every scaffold —
  refresh against a clean checkout produces a cleaner manifest diff.
- **welcome-ai-badger script content drift**: `.ai-badger/skills/welcome-ai-badger/scripts/
  mcp_tools.py` gained the `AI_BADGER_MCP_AVAILABILITY` env override ("all" / "none") in
  `_server_available()`, forcing every declared MCP server available/unavailable. Purpose:
  the scaffold-freshness guard's re-scaffold comparison must be deterministic across hosts —
  a machine with `hermes` on PATH and one without must produce the same tree. Useful when
  debugging why a scaffold diff differs between two machines.

## Framework facts confirmed on this machine

- Framework checkout: `/Users/arasz/RiderProjects/ai-badger` (VERSION file = the framework
  version; compare against config.json `frameworkVersion`).
- `$AI_BADGER` is NOT exported in Hermes sessions — pass `--root` explicitly.
- Framework python: `/Users/arasz/RiderProjects/ai-badger/.venv/bin/python` (bare macOS
  `python3` is the CLT build with no site-packages; refresh.py needs jsonschema).
- `tooling/index.json` was missing → ran `tooling/index_build.py` first (writes
  `<root>/index.json`; 19 stacks, 106 feature items on this run).
- Claude Code's plugin cache held 18 ai-badger versions (0.61.2 → 0.76.0), all
  `prunable: false`, owner claude-code — report, never touch.

## Refresh-run sequencing lesson

The FIRST invocation of refresh.py performed the re-scaffold (created `.ai-badger.bckp/`,
rewrote managed files). A SECOND invocation then reported `reScaffolded: false`, zero drift,
`frameworkVersion` all 0.76.0 — a clean report that contradicts `git status` full of
changes, because the first run already applied them. If you truncate the first run's output
(e.g. `| tail`), you lose the drift report; parse the full JSON from run #1, not run #2.
