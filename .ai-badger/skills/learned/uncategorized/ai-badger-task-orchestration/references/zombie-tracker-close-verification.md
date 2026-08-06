# Zombie-tracker close verification

Closing an ai-badger task tracker as finished when the work did not fully ship is the
recurring failure mode of the `/task` finish protocol. This file carries the verification
protocol, the finish/park mechanics, and the undo recipe.

## The shipped check — run ALL of these before `task_tracker.py finish`

1. **PR evidence**: `gh pr view <n> --json state,mergedAt,title -q '"\(.number) \(.state) \(.mergedAt // "not-merged") \(.title)"'` for every PR named in state.json (`pr`/`prs` fields). Merged = necessary, not sufficient.
2. **Branch diff (the real evidence)**:
   - `git log origin/main..<task-branch> --oneline` → must print NOTHING
   - `git diff origin/main..<task-branch> --stat` → must be empty
   - Do NOT trust `git merge-base --is-ancestor <tip> origin/main` alone. Squash merges break commit ancestry: a tip can be a non-ancestor while ALL content shipped (normal after a squash merge), and can be an ancestor while later branch commits did not ship (local merge, then more work on the branch).
3. **Sibling branches**: `git branch -r | grep -i <task-keywords>` — parallel lanes (wp1/wp2/lane-*) often hold the unmerged half of a task. Each must be merged or provably dead.
4. **Open PRs**: `gh pr list --state open` — empty is NOT proof; work can sit on local/origin branches with no PR at all.
5. **Worktrees**: `git worktree list` — a live worktree on the task branch almost always means in-flight work.

If ANY unmerged delta exists → **park, don't finish**: leave the tracker STARTED (or restore it, below) and report the unmerged scope to the owner.

## Undoing a wrong finish

- `.ai-badger/task-tracking/executed-tasks.json` (repo root, gitignored): restore `state: "STARTED"`, remove `finishedAt` and `stateJsonUpdated` (python one-liner; validate JSON after).
- Revert any `completedTasks` entry prepended to `.ai-badger/state.json`, including the `lastUpdated` bump (restore the previous value).
- Leave the worktree untouched — it holds the work.

## Finishing a verified-shipped tracker

1. Prepend a lean entry to `.ai-badger/state.json` `completedTasks`: `{id, title, status: "DONE", mergedAt, prs: [...], commit, summary, hasNotes: false}` — match existing zombie-entry style; the summary must cite the live evidence (gh MERGED states, branch deleted on merge).
2. Bump `lastUpdated`.
3. `python3 .ai-badger/skills/task/scripts/task_tracker.py finish <id>` — from the MAIN checkout. It exits 3 if state.json wasn't updated since task start. It KEEPS the worktree when it holds uncommitted changes or commits that exist nowhere else (`worktree.keptBecause` in the output) — resolve the delta or pass `--keep-worktree` deliberately.

## Pitfalls

- **Nagging loop**: the session-start hook counts any non-FINISHED state as unfinished, and `reattach` rewrites non-FINISHED back to IN_PROGRESS — a bulk "abandon" that doesn't set FINISHED doesn't stick. Only FINISHED stops the resume prompts.
- **Per-checkout tracking state**: `task-tracking/` is gitignored, so finishing from a worktree closes the WORKTREE's copy, not the repo's (same root cause as the "Tracker state lives in the MAIN checkout" pitfall in SKILL.md).
- **gh jq template**: `gh pr view --json` with `-q` — use the single-quoted string template `'"\(.number) \(.state) ..."'`; composing `.number|tostring + ...` fails with "expected an object but got: number" (the json root holds primitives).
- **CLAUDE.md budget warning**: `finish` prints an over-budget warning when CLAUDE.md exceeds the project limit (13000 chars / 225 lines in jsaa) — pre-existing drift, not a blocker; compact at task boundaries, never mid-task (rewriting the prefix invalidates subagent caches).

## Worked example — 2026-08-05, shell-prompt-real-username

- State: tracker STARTED; PRs #744 (refactor: shell prompt as shared const) and #745 (docs: plan real username) MERGED; branch task/shell-prompt-real-username gone from origin.
- Initial (wrong) conclusion: zombie, close. `finish` ran; the worktree was kept (node_modules + "1 commit(s) on the branch and nowhere else").
- Counter-evidence found while cleaning up: the kept worktree's branch tip was a merge of origin/task/wp2-use-current-user; `git diff origin/main..task/shell-prompt-real-username --stat` = 33 files, +575/−64 (feat: GET /identity, useCurrentUser hook, e2e stubs); origin still carried task/wp1-identity-endpoint (2 commits) and task/wp2-use-current-user (7 commits); `gh pr list --state open` was EMPTY.
- Lesson: merged wave-1 PRs ("refactor:", "docs: plan") plus an empty open-PR list still left the implementation unmerged. The diff check was the only one that caught it.
- Undo: executed-tasks.json restored to STARTED; state.json entry reverted; worktree left in place; owner informed the task is still in flight.
