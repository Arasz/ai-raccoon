# Worked example: 0.74.0 -> 0.77.1 refresh of job-search-ai-assistant (2026-08-03)

Ran via the standard flow (locate framework, rebuild index, first run does the re-scaffold,
second confirms). PR #716, merged as 462a7508, squash + delete-branch.

## What the 0.77.1 re-scaffold actually changed (19 files, +588/-259)

- `config.json` frameworkVersion 0.74.0 -> 0.77.1; manifest.json regenerated.
- Invariants with real framework edits (the rest were stamp-only regenerations):
  - pr-per-task: exception language tightened — "an agent never grants itself this exception";
    the exception lifts only the PR requirement, every gate still runs before the push.
  - guard-clauses: reworded to "idiomatic guard utility for the language/stack in use".
- New `hermes` MCP server declared in `.mcp.json`
  (`${HOME}/.local/bin/hermes mcp serve`) and enabled in `.claude/settings.json`
  (`mcp__hermes__*` allow + enabledMcpjsonServers). Framework catalog growth lands via refresh.
- auto-wm skill gained `forget [PATH] [--force]` (removes a project's state.json entry
  entirely, audit-logged as `mode_forgotten`, refuses a live window without --force,
  exact-path match so deleted worktrees stay reachable); `most_specific()` now shared by
  entry_here / covering_entry.
- welcome-ai-badger mcp_tools.py: MCP servers gated by optional `availability.command` on
  PATH, plus `AI_BADGER_MCP_AVAILABILITY=all|none` override so scaffold output is
  deterministic across hosts (hermes-installed vs not).

## Report shape notes

- frameworkVersion: scaffolded == manifest == current after the first run (config.json
  advances only when a re-scaffold ran). Second run: all drift sections empty,
  reScaffolded: false, newStacks: [] — the clean confirm.
- frameworkCopies: the whole Claude Code plugin-cache version history (0.61.2..0.76.0) is
  listed; none prunable, owner claude-code. Never offer to delete that path.
- hermesSkillLinks.created listed the 19 delivered skills (auto-wm...welcome-ai-badger) —
  ai-badger re-created the Hermes skill links during the re-scaffold.

## Gate & merge behavior (this repo)

- Pre-push gate ran only the docs lane: 18.7s, passed. "1 document(s) not yet recorded" is
  benign pre-existing state per jsaa-quality-gates skill; "CLAUDE.md has 220 lines; warning
  threshold is 200" is a soft warning.
- CI: build pass 3m7s, label pass, test pass 27s; plan/apply/api-endtoend-tests/emulator-
  contract-tests all `skipping` (path-filtered — no code/terraform changes in the branch).
- Merged via `gh pr merge 716 --squash --delete-branch` once checks were green (the
  diffstat-echo after the command IS the success summary; verify with
  `gh pr view <n> --json state,mergeCommit` if unsure).

## Prune-candidate report (informational, not acted on)

8 skills unused over the 25.5-day window (3977 transcripts): code-review-checklist,
create-task-spec, debug-issue, differential-feature-refactor, evidence-first-research,
explore-codebase, refactor-safely, review-changes. Decline path: `exclude.skills` in
config.json (self-executing next refresh). cannotTell: welcome-ai-badger (runs once per
project lifetime — never propose pruning).