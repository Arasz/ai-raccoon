# Runbook — #414 S6b: history rewrite (owner-executed)

**Date:** 2026-08-22 · **Issue:** #414 · **Related:** #455 (#455 must merge first — see
precondition 4) · **Prepared by:** dotnet-engineer lane, task `pd3-s6b-runbook`, WP5 of
`docs/work/2026-08-22-post-delta-3-plan.md`. **This lane executed nothing in this runbook** — no
rewrite, no force-push, no `git filter-repo` run against the real remote. Every command below is
for the owner to run by hand, in a plain terminal, outside any Claude Code session.

## What this removes and why

Three committed paths carry private prose extracted from the owner's job-search-ai-assistant
repo. Two are still reachable from `main`'s HEAD today; #455 replaces both (WP6 of the post-delta-3
plan) before this rewrite runs, so **the rewrite is the last step, not the next one** — see
precondition 4.

| # | Path | On HEAD today? | Commits touching it | Distinct blobs |
|---|---|---|---|---|
| 1 | `tests/AiRaccoon.Tests/Resources/jsaa-memory.db` | No — removed by #450 | 7 | 7 |
| 2 | `benchmarks/AiRaccoon.Benchmarks/Corpus/RealWorldCorpus.cs` | Yes — until #455 lands | 1 | 1 |
| 3 | `tests/AiRaccoon.Tests/Unit/Retrieval/assets/reference-topk.json` | Yes — until #455 lands | 3 (see below) | 2+ |

Numbers 1 and 2 are straightforward: one literal path, one history. **Number 3 is not** — this
file was created, then moved twice, under three different literal path spellings, and
`git log -- <path>` only follows the path spelling you give it:

- `tests/AiRaccoon.Tests/Retrieval/assets/reference-topk.json` (created here, `6a52f976`)
- `tests/AiRaccoon.Tests/unit/retrieval/assets/reference-topk.json` (moved here, `9a53d63e`)
- `tests/AiRaccoon.Tests/Unit/Retrieval/assets/reference-topk.json` (renamed here, current HEAD
  path, `5005a05b`)

All three commits carry the private prose in this file's content (`git rev-list --objects --all`
shows two distinct blob SHAs across the three path spellings, meaning at least one of the moves
also touched content, not just the path). **A `git filter-repo --path` invocation that only names
the current HEAD spelling leaves two of these three commits, and one of the two blobs, intact.**
The invocation in this runbook names all five paths — the three files' current paths plus these
two historical spellings of path 3 — for exactly this reason.

**Correction to the S6b brief handed to this lane:** the brief cited path 3's history as "two
commits (`2499ca59`, `155f281e`)". Neither commit touches this file — `git diff-tree --no-commit-id
--name-only -r 2499ca59` and the same for `155f281e` are both empty for this path; `2499ca59` is an
unrelated rebase commit and `155f281e` is the #404 embedding-model merge commit (also tagged
`v1.29.0`). The verified history is the three commits above. This table and the `--path` list
below are re-derived from the live repository, not copied from that brief.

## Preconditions — check every one immediately before starting

Run these from a plain terminal (not inside a Claude Code session) right before you begin, not
from a stale note — refs and PR state change under this repo constantly.

**1. Zero open PRs.**
```bash
gh pr list --repo Arasz/ai-raccoon --state open
```
Expected: empty. At the time this runbook was written, two were open (#471, #469) plus this
runbook's own PR — merge or close all three (and anything opened since) before proceeding.

**2. No worktree but main.**
```bash
git -C /Users/arasz/RiderProjects/ai-raccoon worktree list
```
Expected: exactly one line, the main checkout. Every `.ai-badger/worktrees/*` and
`.claude/worktrees/*` entry must be removed first (`git worktree remove <path>` from the main
checkout, or `git worktree prune` after deleting the directory).

**3. No Claude session running against this repo.** Confirm every peer session has stopped —
check for anything still active under `~/.claude/projects` for this repo, and that no scheduled
`resume_cron` job is mid-run. If in doubt, wait for an explicit stop confirmation from every
session before continuing.

**4. #455 is merged, so paths 2 and 3 are off HEAD.**
```bash
gh issue view 455 --repo Arasz/ai-raccoon --json state
git -C /Users/arasz/RiderProjects/ai-raccoon cat-file -e origin/main:benchmarks/AiRaccoon.Benchmarks/Corpus/RealWorldCorpus.cs && echo "STILL ON HEAD" || echo "off HEAD, good"
git -C /Users/arasz/RiderProjects/ai-raccoon cat-file -e origin/main:tests/AiRaccoon.Tests/Unit/Retrieval/assets/reference-topk.json && echo "STILL ON HEAD" || echo "off HEAD, good"
```
Expected: issue `state` is `CLOSED`, and both `cat-file -e` checks print "off HEAD, good" (exit
nonzero). **Do not proceed if either file is still reachable from `origin/main`'s tip** — this
rewrite is meant to run once, after #455's replacement content is what `main` carries, so the
rewritten history doesn't need a second pass.

## Rollback — take this before touching anything

```bash
git clone --mirror https://github.com/Arasz/ai-raccoon.git ~/ai-raccoon-rollback-$(date +%Y%m%dT%H%M%SZ).git
```
Keep this mirror until the verification step (below) prints PASS on a fresh clone of the
rewritten remote. If anything goes wrong before that, the rollback is:
```bash
cd ~/ai-raccoon-rollback-<timestamp>.git
git push --force --mirror https://github.com/Arasz/ai-raccoon.git
```

## Pre-rewrite record

Run these against the rollback mirror (or any current clone) and keep the output — this is the
proof of what was removed, and the proof the rewrite reached everything.

**Every blob SHA for the three paths, across every path spelling any of them ever had:**
```bash
git -C ~/ai-raccoon-rollback-<timestamp>.git rev-list --objects --all | grep -E 'tests/AiRaccoon\.Tests/Resources/jsaa-memory\.db|benchmarks/AiRaccoon\.Benchmarks/Corpus/RealWorldCorpus\.cs|tests/AiRaccoon\.Tests/(U|u)nit/(R|r)etrieval/assets/reference-topk\.json|tests/AiRaccoon\.Tests/Retrieval/assets/reference-topk\.json' > pre-rewrite-blob-shas.txt
cat pre-rewrite-blob-shas.txt
```

**Every branch and tag tip, so surviving refs can be checked against this list after the rewrite
(derive this list at execution time — do not use a snapshot from an earlier day, refs churn):**
```bash
git -C ~/ai-raccoon-rollback-<timestamp>.git for-each-ref refs/heads refs/tags > pre-rewrite-refs.txt
wc -l pre-rewrite-refs.txt
```
At the time this runbook was written the live remote carried 15 branches and 64 tags
(`git ls-remote --heads origin | wc -l`, `git ls-remote --tags origin | grep -v '\^{}' | wc -l`) —
re-run those two counts now; if precondition 1 held, the branch count should be at or near the
same 15 (main plus whatever release/task branches remain open-but-unmerged is exactly what
precondition 1 forbids, so ideally it is lower).

## The rewrite

**Install `git-filter-repo`** (not the deprecated `git filter-branch`):
```bash
pip install git-filter-repo
# or: brew install git-filter-repo
```

**`git-filter-repo` refuses to run on a repo that isn't a fresh clone** (it checks for exactly one
remote and no prior filter-repo run). Make a new, plain, non-mirror clone just for this — do not
reuse the mirror above (that one is the rollback and must stay untouched) and do not reuse the
main checkout:
```bash
cd ~
git clone https://github.com/Arasz/ai-raccoon.git ai-raccoon-rewrite
cd ai-raccoon-rewrite
```

**Run the rewrite** — five `--path` arguments: the three files' current paths, plus the two
historical spellings of path 3 documented above:
```bash
git filter-repo --invert-paths \
  --path tests/AiRaccoon.Tests/Resources/jsaa-memory.db \
  --path benchmarks/AiRaccoon.Benchmarks/Corpus/RealWorldCorpus.cs \
  --path tests/AiRaccoon.Tests/Unit/Retrieval/assets/reference-topk.json \
  --path tests/AiRaccoon.Tests/unit/retrieval/assets/reference-topk.json \
  --path tests/AiRaccoon.Tests/Retrieval/assets/reference-topk.json
```
`git filter-repo` rewrites every ref it can reach (all branches and tags in this fresh clone),
converts each remote-tracking branch (`refs/remotes/origin/<name>`) into a local branch
(`refs/heads/<name>`) ready to push, and runs its own aggressive gc as part of the operation — no
separate gc step is needed on this clone afterward.

## The hook lift the owner must make — and the undo

**This push must not go through Claude Code.** `~/.claude/settings.json`'s `autoMode.soft_deny`
list is `["$defaults", "Bash(git push:* --force)"]` — verified by reading that file directly. It
matches only the literal string `--force`; it does **not** match `--force-with-lease` or the `-f`
short form, so it is not a reliable backstop even if a session were used for this. Push from a
plain shell, outside any Claude Code session, for this one operation.

**If autonomous work mode (AWM) is armed in the directory you'd otherwise use:** check with the
`auto-wm` skill's status command and disarm it there before doing anything manually in that
directory, so no agent auto-approves a command mid-operation. There is nothing to "undo" in
`settings.json` itself — this runbook does not ask you to loosen `soft_deny`, and you should not
need to. If you did temporarily edit `soft_deny` or any permission setting to let an agent perform
part of this, revert that edit before resuming normal sessions; it should not persist past this
operation.

## The push — this is the owner's command, not a lane's

**OWNER-EXECUTED. Run from `~/ai-raccoon-rewrite`, in a plain terminal:**
```bash
git push --force --all
git push --force --tags
```
(These may be combined as `git push --force --all --tags` in one call.) This overwrites every
branch and tag on the remote with the rewritten history.

## Re-planting any surviving branches

If precondition 1 was not fully clean — a branch existed at rewrite time beyond `main` and the
already-merged/closed set — that branch's rewritten copy is already pushed by the command above
(it was in `ai-raccoon-rewrite` as a local branch and `--force --all` pushed it). Nothing further
is needed for a branch that was fully present in the fresh clone. The only branches this section
covers are ones created **after** the fresh clone was made and **before** the push — those did not
exist in `ai-raccoon-rewrite` and are not in the force-push. Re-plant each with:
```bash
git checkout <branch>
git rebase --onto main <old-branch-point> <branch>
git push --force-with-lease origin <branch>
```
then diff the branch's net changes before and after to confirm they're identical (`git diff
<old-tip> <new-tip> -- . ':!tests/AiRaccoon.Tests/Resources/jsaa-memory.db' ':!benchmarks/AiRaccoon.Benchmarks/Corpus/RealWorldCorpus.cs' ':!tests/AiRaccoon.Tests/Unit/Retrieval/assets/reference-topk.json'`
should be empty).

## After the push: every other clone and worktree must be re-cloned, not pulled

Every local clone and worktree of this repo — the main checkout, every `.ai-badger/worktrees/*`
and `.claude/worktrees/*` entry, and any clone on any other machine — now has diverged history from
the remote. **Delete each one and clone fresh; do not `git pull`, `git fetch && git reset --hard`,
or `git rebase` an existing clone onto the new history.** A `pull` under this repo's global
`pull.rebase=true` setting would attempt to replay the old (blob-carrying) commits on top of the
new history, and the WP7 investigation in the parent plan documents exactly this pattern causing
today's earlier force-pushes — reusing an old clone in place recreates the same failure mode.

If a clone absolutely cannot be deleted and must be reused in place (discouraged — prefer
re-cloning), it needs `git fetch origin && git reset --hard origin/<branch>` for every branch it
tracks, followed by:
```bash
git reflog expire --expire=now --all
git gc --prune=now --aggressive
```
to actually drop the now-unreachable old objects locally (otherwise the private blobs stay on
disk in that clone's object store even though no ref points at them, and merging a stale local
branch into the new history would resurrect them).

## Verification — the gate, and its expected PASS output

```bash
python3 scripts/verify-history-scrubbed.py
```
Expected output after a successful rewrite and push (clean exit, code 0):
```
tests/AiRaccoon.Tests/Resources/jsaa-memory.db: 0 commit(s)
benchmarks/AiRaccoon.Benchmarks/Corpus/RealWorldCorpus.cs: 0 commit(s)
tests/AiRaccoon.Tests/Unit/Retrieval/assets/reference-topk.json: 0 commit(s)
PASS: no history reachable for any tracked path
```
For full coverage of path 3's historical spellings too, also run:
```bash
python3 scripts/verify-history-scrubbed.py \
  --path "tests/AiRaccoon.Tests/unit/retrieval/assets/reference-topk.json" \
  --path "tests/AiRaccoon.Tests/Retrieval/assets/reference-topk.json"
```
same expected shape, five zero lines and `PASS`. The script always clones the remote fresh into a
temp directory by default (`--remote` overrides which remote; `--repo <dir>` points it at an
existing local clone instead of cloning, for local testing only — do not use `--repo` to verify
the real rewrite, since that skips the "does the *pushed* history look clean" question this gate
exists to answer). Before the rewrite, this same command is expected to **FAIL** — see the recorded
RED run in this WP's PR body, taken against today's origin, prior to any rewrite.

Once verification PASSes: close #414 referencing this runbook and the verification output, and
delete the rollback mirror once you're confident (or keep it indefinitely — it's cheap and it's
the only way back).
