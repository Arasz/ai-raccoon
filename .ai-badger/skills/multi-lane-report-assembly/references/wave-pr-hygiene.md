# Wave-PR hygiene under squash merges

Sequential wave delivery (one PR per wave, squash merges) on a shared worktree.
All mechanics below were exercised live in a multi-wave remediation task
(2026-08-05, the reference repo).

## Squash-merge + next-wave branch: rebase BEFORE finalizing the PR

When wave 1's PR is squash-merged, the worktree branch still carries wave 1's
commits. A wave-2 branch created from that worktree therefore contains the
already-merged content under DIFFERENT commit SHAs, and its PR diff (computed
vs the merge-base) shows the plan + wave-1 files as new changes.

Fix — rebase the wave branch onto the merged main, dropping the upstreamed
commits:

```bash
git fetch origin
git rebase --onto origin/main <wave1-branch-tip>~1 <wave2-branch>
# "dropping <sha> ... -- patch contents already upstream" = expected
git push --force-with-lease origin <wave2-branch>
```

Then verify the PR diff shows only the wave's own files:
`git diff origin/main...<branch> --stat`.

Notes:
- The pre-push gate (lefthook, multi-lane) runs on EVERY push and takes
  minutes (up to ~8 min full mode). Push in the background; a foreground push
  will hit the 180s tool timeout mid-gate and abort the transfer — the remote
  then still has the OLD sha. Retry in background.
- `--force-with-lease`, never bare `--force`, on a PR branch you own.

## gh pr merge --squash "fails" locally but merges remotely

`gh pr merge --squash --delete-branch` errors with
`failed to run git: fatal: 'main' is already used by worktree at ...` when the
default branch is checked out by ANOTHER worktree (e.g. the IDE's main
checkout). The remote merge still lands. Verify state, then clean up:

```bash
gh pr view <n> --json state,mergedAt,mergeCommit -q '.state, .mergedAt, .mergeCommit.oid'
# MERGED => nothing to retry; remote branch is usually already deleted
```

## Draft PRs and the required `test` check

A workflow with `types: [opened, synchronize, reopened, ready_for_review]` on
`pull_request` SKIPS its required `test` job while the PR is a draft and
re-fires it on `ready_for_review`. Consequences:

- `gh pr checks` can show a STALE duplicate run ("skipping") from the
  pre-ready event while the newer run already passed. Verify per-workflow:
  `gh run view <run-id> --json jobs -q '.jobs[] | "\(.name): \(.conclusion)"'`
  or `gh run list --workflow <wf> --branch <branch>`.
- Use draft status as a gate mechanism: keep a PR draft until owner-side
  prerequisites (secrets, app registrations) exist; draft skips `plan` via
  `test` skip-propagation.

## Owner-runbook split (code-side PRs vs owner-side actions)

For infra remediation, split deliverables into:
- **Code-side PRs** (terraform/workflow/docs changes with tftest pins + gates),
- **Owner runbook appendix** (exact `az` CLI commands + GitHub/Azure console
  steps for things the repo cannot change: secrets, Entra app registrations,
  federated credentials, portal token rotation).

Rules that keep this workable:
- Sequence identity changes: create the NEW identity + secret BEFORE the
  workflow switches to it; revoke the OLD federated subject only AFTER the
  merge. The intermediate window is NOT inert — `pull_request` runs the
  workflow from the merge ref, so a same-repo PR can still edit the workflow
  and mint tokens with any subject still registered.
- Never merge a wave PR before its listed owner prerequisites (gate in the PR
  description).
- Every owner action carries the exact command and the "what proves it"
  acceptance line (e.g. `az storage account show --query allowSharedKeyAccess`
  → `false`).

## Project-specific ledger quirks (the reference repo)

- `bun scripts/docs.ts record --path <p>` takes the path RELATIVE TO `docs/`
  (it prepends `docs/`; a full repo-relative path 404s).
- Registering a new document makes `docs.ts trust --check` exit 1 (stale
  index) — fix with `bun scripts/docs.ts trust` (regenerates), then re-check.
- Every governed directory's README must name its files (`docs.ts check`
  fails otherwise): add the table row + `docs.ts record` for the README too.
- Editing a registered file after `record` = content-drift failure; re-run
  `record` to bump the version and update the hash.
- Use a task worktree for edits, NOT the IDE's main checkout: the patch tool
  resolves paths against the session cwd, and edits can land in the wrong
  checkout — copy to the worktree, then `git restore` the main checkout.
