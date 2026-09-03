---
name: git-work
description: "Use when a git push fails for a reason the quality gate did not cause, CI goes red on a pushed branch, or a PR moves through review and merge outside the tracked-task flow: non-fast-forward recovery with force-with-lease, CI log triage and flake attribution, draft-to-squash PR lifecycle, squash-merge conventions, and join-time conflict resolution on plain branches."
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [git, pr, ci, push]
    related_skills: [task, pre-push-gate-debugging, worktree-agent-isolation]
---

# Git work: push, CI, and PR lifecycle

Covers pushes, CI runs, PRs, and merges on any branch. A blocked pre-push gate belongs to
pre-push-gate-debugging; multi-agent worktree setup belongs to worktree-agent-isolation. When
the `task` skill's github extension is active, its PR flow is authoritative and this skill fills
the gaps around it.

## Push failures without a gate cause

**Non-fast-forward** (`! [rejected] … fetch first`) resolves through this sequence:

1. `git fetch origin <branch>` — stale refs manufacture most rejections.
2. Assess divergence: `git log --oneline HEAD..origin/<branch>` and the reverse.
3. Small real difference → rebase onto the new base (`git stash push -u` first when WIP is
   present). Divergent histories worth keeping → merge. Rebase keeps the PR diff readable.
4. Push rebased work as `git push --force-with-lease origin <branch>` — the lease rejects when
   someone pushed between fetch and push; a plain `--force` overwrites unseen work.

**Branch protection** rejections name the missing checks or statuses; satisfy the named
requirement on the branch rather than seeking a bypass.

**Crashed hook replay**: a hook killed mid-run leaves the lock file named in the error; remove
that file, then replay the operation once.

**Auth** failures (403/404 on a repo you can see) mean expired credentials; refresh via the
platform CLI (`gh auth status`, `gh auth login`) before retrying.

**Frozen remote-tracking refs** (`git fetch` reports success but `origin/<branch>` never moves)
means `.git/config` lost `remote.origin.fetch` or a `[branch]` section. Read
`references/git-config-repair.md` when that happens, before editing the file by hand.

## CI failure triage

- Read only what failed: `gh run view <id> --log-failed`.
- **Reproduce vs rerun**: a failure naming your diff (assertion, lint finding) reproduces
  locally first; infra-shaped output (timeout, connection reset, runner lost) earns one rerun
  of the single failing job before investigation starts — one, then stop. Re-running a job or
  suite to see whether it stays green is not triage: flake attribution works from the runs
  that already happened (which failed, which passed, what differed) and files the evidence;
  manufacturing extra runs is the repetition the test-run-economy invariant bans.
- **Env-only failures** (green locally, red in CI): compare runtime versions across the matrix,
  working directory, and secret/env presence before suspecting the code.
- **Flake attribution**: record which run failed, which passed, and what differed; a test that
  alternates on identical code is filed as flaky with that evidence.
- **Red CI after a force push** is the expected fresh run on rewritten history; read the new
  run's results, since the pre-rebase run describes commits that no longer exist.

## PR lifecycle outside a tracked task

Open a draft PR at the first commit, mark ready when complete, then loop:

1. Poll for review; when the configured reviewer's quota is exhausted, fall back to whatever
   alternate reviewer mechanism the project has (bot mention, human request).
2. Batch-triage findings together — they interact — then verify each still applies to the
   current head before acting.
3. Reply on every addressed thread and resolve it; resolved-in-code threads stay open otherwise.
4. Squash-merge once a round returns zero new findings since the last pushed commit.

## Merge and squash conventions

Squash is the default merge shape: subject reads `<type>: <summary> (#N)` and carries the
detail in the body, because squashing flattens per-commit messages away. Delete the branch as
part of merging (`gh pr merge <n> --squash --delete-branch`). Admin bypass exists for genuine
emergencies; treating it as an owner decision keeps required checks meaningful — the project's
contributing docs hold the exact policy.

## Join-time conflicts on plain branches

Sequential merges guarantee that later branches conflict with earlier ones. Update onto the new
base first (`git fetch && git rebase origin/main`); a local merge fits when history must stay
intact. Derived/generated files regenerate from source after resolving arbitrarily — hand-merging
them produces marker damage. Before committing any resolution, run
`git grep -n '<<<<<<<\|>>>>>>>' -- <changed files>`; staged markers commit silently.
Two worked cases from this repo's history live in `references/case-playbooks.md`, read when a
multi-conflict day or a stuck-queued PR appears.

## Gotchas

- **A squash-merged base orphans everything built on it.** The merge-base predates the
  flattened commit, so `git merge origin/main` replays the whole merged branch — 187 hunks
  over 19 files on one occasion here. Check `git diff origin/main..<branch> --stat` first:
  a small stat means rebuild the change from a fresh `origin/main` worktree instead of merging.
- **An unmergeable PR looks like slow CI.** A branch conflicting with its base dispatches no
  merge-ref run, so one workflow reports normally while another stays queued forever;
  `gh pr view <n> --json mergeable` showing `CONFLICTING` is the tell.
- **Config-as-source conflicts drop entries with no marker.** Taking either side of a conflict
  in `.ai-badger/config.json` silently loses the other side's `include.skills`; diff it against
  main after every resolution.
- **`gh pr merge --squash --delete-branch` can fail after the merge landed** (fatal git error
  during cleanup) — verify PR state before retrying, since a second call targets a closed PR.
- **Force-pushing re-triggers remote CI while the local gate ran on your working tree**, so a
  green local lane plus a pending remote run is normal mid-rebase state, not a contradiction.
