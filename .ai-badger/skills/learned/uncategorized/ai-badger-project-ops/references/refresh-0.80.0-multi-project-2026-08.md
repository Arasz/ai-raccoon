# Refresh 0.80.0 — two projects in one session (jsaa + arasz-home-page), 2026-08-06

Session shape: user invoked den-refresh while cwd was the FRAMEWORK checkout
(/Users/arasz/RiderProjects/ai-badger, itself at 0.80.0). Two targets were behind:
job-search-ai-assistant (0.79.0) and arasz-home-page (0.77.3 committed / 0.79.0 partially
applied, uncommitted). User chose "both". Result: PR #752 (jsaa), PR #209 (home-page).

## Target selection

- `grep '"frameworkVersion"' /Users/arasz/RiderProjects/*/.ai-badger/config.json` vs
  `cat "$AI_BADGER/VERSION"` finds behind projects. A self-refresh (--target . in the
  framework repo) is a no-op — sources == scaffold origin.
- The den-refresh skill assumes you are IN the target repo; with multiple candidates,
  ask the user which target (clarify tool) rather than guessing.

## Run mechanics

- `python3 "$AI_BADGER/features/common/skills/den-refresh/scripts/refresh.py" --target <proj> --root "$AI_BADGER" > /tmp/refresh-<proj>.json`
  — ALWAYS redirect to a file; the JSON report is ~11-14 KB and `tail` loses the drift/
  scaffold sections (hit twice this session).
- First run does the re-scaffold (config.json frameworkVersion advances 0.79.0 -> 0.80.0 —
  that is how you know a re-scaffold ran). Second run is the convergence check:
  `reScaffolded: false`, drift all empty, frameworkVersion 0.80.0/0.80.0/0.80.0.
- Both targets had `.ai-badger.bckp/` created (already gitignored in both repos).
- `frameworkCopies` = 17 stale ai-badger versions in ~/.claude/plugins/cache/ai-badger/
  (0.61.2 -> 0.77.3), all `prunable: false` — report, never touch.

## Staging exclusions (what NOT to commit in the refresh PR)

- jsaa `.ai-badger/state.json` — dirty with ANOTHER task's tracker state (PR_READY F1
  entry). Excluded via `git restore --staged .ai-badger/state.json` after a root-path
  stage. Same for the untracked review doc
  `docs/work/reviews/2026-08-06-part2-moe-review-feedback.md` (parallel task).
- home-page `.mcp.json.bak-20260805-223221` — refresh-created backup of .mcp.json (named
  from the source file's mtime); transient, never commit.
- home-page's `.mcp.json` + `.claude/settings.json` WERE included: the 0.80.0 refresh
  delivers the ai-raccoon MCP server (command, tools) into .mcp.json and the
  memory-grade hook + `mcp__ai-raccoon__*` permission + `enabledMcpjsonServers` entry
  into .claude/settings.json — managed deliverable, belongs in the PR.
- New framework files to include: `.ai-badger/hooks/debug_log.py`, memory_grade.py,
  `.ai-badger/skills/ai-raccoon-memory/`, and the agent discovery symlinks
  `.claude/skills/ai-raccoon-memory`, `.github/skills/ai-raccoon-memory`,
  `.junie/skills/ai-raccoon-memory` (all -> ../../.ai-badger/skills/ai-raccoon-memory).
- jsaa already tracked its .claude/skills/ai-raccoon-memory symlink — verify with
  `git check-ignore -v` / `git ls-files` before assuming untracked.

## Rebase onto moved origin/main

- jsaa local main (cb1bce25) was behind origin/main (1bd21a67 = PR #751 merged by a
  parallel session). Committed on a branch from HEAD, then
  `git rebase --autostash origin/main` — autostash carries the dirty tracked state.json
  around the rebase and pops it back. Conflict-free because #751 touched only infra/src.
- Verify the delta is unrelated BEFORE rebasing:
  `git diff --stat cb1bce25 origin/main | tail` — if it touches .ai-badger/* you would
  need to reconcile framework files.

## Pre-push gate episode (jsaa): VERIFY_SKIP=docs

- The refresh commit touched ZERO docs/ files, yet the lefthook pre-push gate failed on
  the docs lane: `readmeIncomplete: 1` + `1 document(s) not yet recorded` +
  `how-to/custom-domain-jsaa-pl.md version-mismatch` (ledger projection drift).
- Root cause: the gate tests the WORKING TREE, and the tree held another task's untracked
  review doc. The gate's repair tool refused to regenerate:
  "not regenerating: docs/work/reviews/2026-08-06-part2-moe-review-feedback.md has
  uncommitted changes, and the repair may only commit bytes it wrote itself."
- Proof of unrelatedness: `git show --stat HEAD | grep docs/` -> empty; the lane's own
  output said "Agent instruction validation passed. Agent instruction drift check passed."
- Sanctioned bypass from the gate's own failure block: `VERIFY_SKIP=docs git push`
  (stage-scoped; all other lanes — bun self-tests, agent-instruction checks — passed).
  Do NOT fix the other task's files and do NOT use --no-verify.
- CI on the PR does not run the docs lane (local gate only), so the PR is unaffected.
  The docs task must run `bun scripts/docs.ts record` when it commits its review doc.

## skillUsage reporting (per repo)

- jsaa: window 25.5 days / 3977 transcripts; 8 unused candidates
  (code-review-checklist, create-task-spec, debug-issue, differential-feature-refactor,
  evidence-first-research, explore-codebase, refactor-safely, review-changes);
  cannotTell: ai-raccoon-memory, welcome-ai-badger. Hook-evidence used (leave alone):
  commit-reminder (293), mcp-index (96), prompt-markers (96).
- home-page: window 17.7 days / 1412 transcripts (SHORT window — say it out loud);
  5 unused (code-review-checklist, create-task-spec, differential-feature-refactor,
  maintain-agent-instructions, owner-gate-review); cannotTell: ai-raccoon-memory.
- Present candidates + windows + limits; decline via config.json `exclude.skills`
  (self-executing on next refresh). Never edit config.json during a refresh.

## PR results

- jsaa #752: pre-commit code-review-graph ran (risk 0.40, 23 changed functions);
  CI build/test/label pending, api-endtoend-tests + emulator-contract-tests skipping
  (scaffold-only branch, path-filtered).
- home-page #209: Vercel deploy + preview comments passed immediately; label pending.
- User merges both himself ("commit, create PR for each" = stop at open PR).
