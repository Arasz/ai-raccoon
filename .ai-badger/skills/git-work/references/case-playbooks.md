# Case playbooks — git-work

Two situations from this repository's history that the SKILL.md rules compress. Read the
matching playbook before acting when a live situation matches its pattern.

## Playbook 1: four merge conflicts in one day (2026-08-15)

Pattern: several open PRs while every release touches the same three derived files
(`VERSION`, `index.json`, `docs/changelog/README.md`).

What worked:

1. Resolve each conflict arbitrarily, then regenerate from source (`index_build.py` for the
   index; hand-edit `VERSION` to the correct next value; re-add the changelog row).
2. Grep staged files for markers before committing — one of the day's eight commits staged
   `<<<<<<<` inside a regenerated file.
3. Rebase onto `origin/main` between merges so later branches start from the newest base.

Traps recorded that day:

- `.ai-badger/config.json` conflicts are source conflicts: taking either side dropped the other
  side's `include.skills` entries silently.
- A squash-merged base produced 187 conflicting hunks over 19 files for a small real change;
  rebuilding from a fresh `origin/main` worktree took minutes where merging took an hour.

## Playbook 2: the PR that sat "queued" for 37 minutes (#341)

Pattern: one workflow reports green normally, another shows queued indefinitely.

Root cause: the branch conflicted with its base, so GitHub dispatched no merge-ref run at all —
the queue display is identical for "waiting" and "will never run".

Resolution path: check `gh pr view <n> --json mergeable`; on `CONFLICTING`, fix the conflict
first and expect CI to start only after the branch updates.
