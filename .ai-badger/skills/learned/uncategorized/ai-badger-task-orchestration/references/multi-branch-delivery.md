# Multi-branch delivery: rebase, ownership checks, tracker, contracts

Session-tested recipe (2026-08-04: file-watcher + cli-config delivered as two parallel
task branches with a shared settings-key contract).

## Rebase when main moves under a task branch

Symptoms: `git diff main..HEAD` lists src/test files your sub-agents never touched.
Cause: main advanced (user committed YOUR untracked spec files, a sibling branch merged,
another session force-pushed) — the task branch sits on a stale base, and
`git diff main..HEAD` shows main-side changes as phantom branch edits.

Diagnose:

```bash
git merge-base main HEAD            # branch base vs current main
git log --oneline main -5           # what main gained since
git ls-tree main -- <file>          # who has the file: main?
git ls-tree HEAD -- <file>          # ...and the branch?
```

`git log main..HEAD -- <file>` EMPTY + file present in main only = main-side change,
NOT a scope violation. Check before accusing a sub-agent.

Fix:

```bash
git fetch origin
git rebase origin/main
# add/add conflicts (both sides created the same file, e.g. the spec you emitted and the
# user committed separately): during rebase 'ours' = the branch commit being replayed
git checkout --ours docs/features/<name>/file-watcher.feature docs/.../spec.json
git add docs/
GIT_EDITOR=true git rebase --continue   # non-interactive continue
# later commits in the chain may conflict on the same files again (the commit that
# UPDATED the spec): take --theirs there (the replayed commit's own content)
git checkout --theirs docs/.../spec.json && git add docs/ && GIT_EDITOR=true git rebase --continue
```

Then: re-run spec_holes.py (gate must stay green), re-run the wave gates against the new
base, force-push with `--force-with-lease`. The user-committed main copy of the spec may be
an OLDER version (pre-later-rulings) — the branch version wins at merge; say so in the
report.

## Ownership check (post-wave seam review)

```bash
git diff --name-only main..HEAD | grep -v -E '^(src/<allowed>/|tests/<allowed>/|docs/)' \
  || echo "OK: no out-of-scope files"
```
Filter against the plan's file-ownership matrix. False positives from a stale base are
resolved by the `git log main..HEAD -- <file>` test above.

## Tracker state lives in the MAIN checkout

`.ai-badger/task-tracking/` is gitignored → absent from worktrees. `task_tracker.py`
subagent/finish/status run from a worktree fails with "Unknown task <id>". Always run
tracker commands from the main checkout. Record delegation tokens without counting:
`task_tracker.py subagent <taskId> --delegation <deleg_id> --description "..."`.

## Untracked spec files don't travel to worktrees

`task_tracker start` branches from HEAD; untracked emit output (feature/spec.json/render)
stays in the main checkout. Copy into the worktree and commit as the task's FIRST commit so
the acceptance contract rides the branch:
`cp <main>/docs/features/<name>/* <wt>/docs/features/<name>/ && git add docs/ && git commit`.

## Cross-branch contracts (parallel feature branches)

When branch A's feature reads config written by branch B's CLI (e.g. watch.* settings
keys), the contract is: exact key strings + value formats + resolution rules (project wins
over global, '*' wildcard) pinned in BOTH plans and BOTH subagent briefs. Verify parity at
the JOIN (merge B, run the full suite, un-gate scenarios that were tagged @ignore because
B's surface didn't exist yet) — deviation only surfaces there.

## Join order for two PRs

1. Merge the config/CLI PR to main first (its surface is a dependency).
2. Rebase the feature PR onto the new main; re-run the full suite.
3. Un-gate integration scenarios that were @ignore'd for the missing CLI; re-run.
4. Full-suite gate + code-reviewer pass on the joined result ("review every join").
