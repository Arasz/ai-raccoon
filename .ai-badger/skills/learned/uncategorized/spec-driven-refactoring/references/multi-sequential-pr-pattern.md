# Multi-PR Sequential Merge Pattern

When a spec defines 8+ phases, splitting into multiple independently-mergeable PRs reduces risk
and keeps reviews scoped. This pattern emerged from a v0.7.0 refactor with 3 PRs.

## Splitting strategy

Group phases by dependency:
- **PR 1 (foundation):** schemas, script plumbing, data migration, hooks extraction, adjustments
  — everything that changes the contract
- **PR 2 (integration):** library modules that consume the new contracts (e.g., `install_plugins.py`)
  + scaffold.py wiring
- **PR 3 (docs):** documentation updates that reflect the new structure

Each PR is independently testable and mergeable. PR 2 depends on PR 1 being merged; PR 3
depends on both.

## Workflow per PR

```
# From main checkout (clean)
git worktree add ../project-prN -b feature/prN-slug origin/main
cd ../project-prN

# If this PR depends on a prior unmerged PR:
git merge <prior-pr-branch> --no-edit

# Implement, test, commit
git add -A && git commit -m "feat: description"
git push origin feature/prN-slug

# Create PR, wait for CI, merge
gh pr create --title "..." --body-file /tmp/pr-body.md --base main
gh pr checks <N> --watch
gh pr merge <N> --squash --admin

# Clean up
cd ../project-main
git checkout main && git pull origin main
git worktree remove --force ../project-prN
git branch -D feature/prN-slug
```

## Key rules

1. **One worktree per PR** — create, use, destroy. Don't reuse worktrees across PRs.
2. **Always pull main after merge** — subsequent PRs need the merged commit as ancestor.
3. **Force-remove worktrees** after merge — they may have untracked files from tests.
4. **Force-delete branches** after merge — squash merge creates a new commit, so the
   original branch tip isn't an ancestor of main.
5. **Run full test suite after each merge** on main to catch cross-PR regressions.

## Pitfalls

- **PR body shell escaping** — write body to `/tmp/pr-body.md` and use `--body-file`.
  The `--body` flag breaks on markdown with backticks, angle brackets, or shell metacharacters.
- **Stash from main doesn't transfer** — `git stash` on main is invisible to worktrees.
  If you stashed changes on main, apply them in the worktree with `git stash pop` after
  checking out the worktree branch.
- **Dependent PRs need merge, not rebase** — when PR 2 depends on PR 1's branch, use
  `git merge <pr1-branch>` so the history is clean. Rebase would replay commits that
  will also appear in PR 1's squash merge.

## Concurrent-session merge races (multi-agent repos)

When OTHER sessions push to origin/main while your PR sequence is in flight (verified
2026-08-06 on ai-raccoon — framework bumps, tool fixes, and docs commits landed mid-task
three times), a multi-PR sequence needs merge-race discipline or gates lie to you:

1. **Fetch before every gate run.** `git fetch origin && git log origin/main --oneline -3`
   before the full-suite gate; if main moved, `git rebase origin/main` (stash WIP first:
   `git stash push -u`, rebase, `git stash pop`). A gate run on a stale base is not evidence.
2. **Per-PR rebase, then force-push with `--force-with-lease`.** Each new PR starts from the
   CURRENT origin/main (`git checkout -b pr-N origin/main`), not from your earlier PR branch
   tip — unless the plan deliberately stacks (PRs touching the same dispatcher file DO
   stack; independent docs/test PRs do not). Once main moves past your branch base, rebase
   and `git push --force-with-lease` (a plain push is rejected non-fast-forward).
3. **PRs merge under you mid-review** (user merges fast, often while you're still pushing
   follow-ups): a merged PR ORPHANS commits you push afterward. Before pushing anything,
   `gh pr view <n> --json state,isDraft` — if it flipped to MERGED, `git reset --hard
   origin/main` on the branch and re-apply, don't stack on the dead tip.
4. **Another session's commit can break origin/main itself** (half-fix: renamed consts but
   updated only the test's filter, not its assertion — full suite RED at main's own head).
   Your merged branch inherits the RED. Diagnose with `git show origin/main:src/...` vs the
   test expectation; fix as a tiny separate PR (one line), merge that fix INTO your branch,
   then re-run the gate. Do NOT bundle the repair into your feature PR (scope), and do NOT
   re-diagnose it as your regression. (Full walkthrough in the dotnet-flaky-test-diagnosis
   skill's "concurrent-session half-fix" section.)
5. **Uncommitted WIP in the SHARED checkout is another session's, not yours.** If
   `git status` shows edits you never made (e.g. a docs file or a Tools file), leave them;
   verify your branch against the COMMITTED state. Only revert files you know you touched.
6. **Check merge state before recording evidence.** `gh pr view <n> --json mergedAt` +
   `git branch -r --contains <sha>` — a PR "MERGED" on GitHub may not be in your local
   origin/main until you fetch, and a squash-merge commit SHA differs from the branch tip.
