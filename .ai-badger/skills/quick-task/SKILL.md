---
name: quick-task
description: >-
  Use when a change is small enough to skip the full task pipeline — one focused fix or
  small feature that fits a single commit on a branch cut from fresh main and merged via
  PR with auto-merge: a minimal plan, touched-surface tests only, the project's fast
  gates (lint, docs), one quick focused review, one commit per PR. Escalate to `task`
  the moment the change outgrows that shape.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [workflow, fast-path, single-commit, tests, review]
    related_skills: [task, test-economy, code-review-checklist, status-report, multi-agent-communication]
---

# quick-task

The fast lane between "just edit it" and the full `task` pipeline. One focused change,
planned in minutes, verified on the surface it touches, reviewed once, shipped as a single
commit on a branch cut from fresh `main` and merged via PR with auto-merge. No worktree,
no plan document, no multi-lane review, no waiting for review.

## The shape (all of it, or escalate)

A change qualifies for quick-task only when every answer is yes:

- **One sentence.** You can state the whole change in one sentence a reviewer could act on.
- **One surface.** It touches one area — one module, one component, one workflow file, one
  doc. Cross-layer work (API + client, schema + code, code + generated manifests) does not
  fit; that is `task` work.
- **One commit.** The finished work is exactly one commit in the PR. If you can already see
  it needs two (code + separate migration, feature + follow-up cleanup), it is not a
  quick-task.
- **Reversible.** Reverting that single commit (or the merge) restores `main` exactly.
  Anything stateful (infrastructure, data migrations, secrets) disqualifies.
- **No rulings needed.** The owner's intent is already known; no design decision is being
  made, only executed. New decisions are `task` (or `create-task-spec`) work.

Chores count: a dependency bump, a doc fix, a lint cleanup, a config tweak — as long as the
shape above holds.

## Flow

1. **Scope check.** State the one sentence and the touched surface. Run the five shape
   questions; any "no" means stop and escalate to `task`. Say out loud that you are in
   quick-task mode so the reduced ceremony is a decision, not an accident. When other
   sessions share the project, announce the start on the bus — read
   `multi-agent-communication` when parallel work is active.

2. **Minimal plan.** Three to six bullets in the working notes — what changes, which files,
   which tests cover it, which docs need updating. No plan file, no ledger record, no
   tracking artifacts. If the plan needs more than six bullets, escalate.

3. **Branch from fresh main. Never on main.** `git pull`, then
   `git checkout -b <short-slug>`. No worktree isolation — a quick-task owns one branch
   in the current checkout, and the branch owns exactly one commit. Working on `main`
   directly disqualifies the run; stop and cut the branch first.

4. **Implement with tests.** The change ships together with its tests: new tests for new
   behavior, updated tests for changed behavior, and a run of the tests that cover the
   touched surface. Follow the repo's own test discipline (which runner, which directory
   tests run from). Do not fix unrelated failures you discover — note them and move on;
   fixing the world is how quick-tasks become incidents.

5. **Fast gate.** Run exactly what the change can affect, and nothing more:
   - the touched surface's tests (only those), once per change — a fix makes the next run new,
     and a re-run to watch it pass again is the theater the next bullet bans,
   - the repo's lint for the languages touched,
   - the repo's docs checks IF governed docs were touched — and record/update them per the
     repo's own mechanism, inside the same commit,
   - typecheck/build only if the repo's gate requires it for the touched paths.
   A full suite run, e2e, or infrastructure validation is NOT part of quick-task — if you
   believe one is needed, the change is bigger than the shape.
   The CI variable decides what proves the push (test-run-economy invariant): CI alive —
   public repos always — skip every slow gate, push, and treat CI's run as the verdict; read
   it. CI dead or absent: run the project's full local gates once before the push; nothing
   hooked up, one manual full-suite run first. Lint and docs gates sit on no skip side of
   this trade — they run in both branches.

6. **Quick focused review.** One pass, before pushing: re-read the diff against the plan
   bullets — does the diff do exactly what was planned, do the tests assert behavior rather
   than implementation, are there stray files, debug prints, or unrelated edits, does the
   commit message describe it. For anything touching auth, money, data loss, or generated
   contracts, make this pass a delegated second pair of eyes instead of self-review.

7. **Version bump — only when the repo has no auto-bump.** If an auto-bump chain owns
   `VERSION` (release-please, semantic-release, a CI bump step), skip the bump: a hand bump
   collides with the chain. Otherwise bump per the repo's own release rule, riding in the
   same single commit — never as a second commit.

8. **Docs update in-branch, same PR.** If the change invalidates or lands in governed
   documentation, the doc update AND its ledger/record step happen in the branch, inside the
   same single commit — never as a follow-up PR.

9. **Single commit, push, open PR.** Everything in one commit on the branch — code, tests,
   docs, version bump (when step 7 applies), and any regenerated artifacts together.
   Conventional message (`fix:`, `feat:`, `chore:`, `docs:` + scope); subject says what
   changed, body says why when it is not obvious. Amend before pushing if you find a gap;
   after pushing, amend and force-push with lease to keep the branch at exactly one commit —
   never stack fixup commits. Push with `-u`, then open the PR immediately. When other
   sessions are active, announce the push and the PR on the bus per
   `multi-agent-communication` (start, PR, review, merge; ack once).

10. **Auto-merge on all-green, rules bypassed. Never wait for review.** Enable auto-merge
    on the PR at open time so green CI merges without a reviewer. A quick-task PR is never
    blocked waiting for eyes: the pre-push review (step 6) was the review. If CI goes red,
    fix in-branch per step 9 and re-read the CI verdict; do not merge red, and do not ask
    for review of green.

11. **Close the loop.** Report: what changed, what was tested and what was NOT (say the
    untested surfaces out loud), which gates ran, the PR number and merge, and — when CI is
    alive — the CI run that proves the change. No post-merge ritual; if the change needs one,
    it was not a quick-task.

## Model tier contract

Quick-task uses the same delegation contract as `task`: `effort` picks the lane, `level` picks the model tier — they are independent.

The `level` field is optional (`low`, `medium`, or `high`), resolved to that model tier's preferred registry entry.
An explicit `model` always wins over `level`.
With neither `level` nor `model`, the dispatch inherits the session (or parent) default model.
The registry lives at `.ai-badger/model-groups.json`; it holds the IDs and prices, so this skill names neither.
See `.ai-badger/delegation.md` (reasoning-model dispatch) for lane routing.

## Gotchas

- **Never on main means the branch is the safety net.** Cut it from freshly pulled `main`
  before the first edit; a quick-task committed to `main` directly has no PR for CI to gate
  and no revert boundary.
- **The single-commit-per-PR rule is the rollback story.** A revert must restore `main`
  exactly; that is why everything rides in one commit and why fixup commits on the branch
  are folded (amend + force-with-lease), never stacked.
- **A hand version bump collides with an auto-bump chain.** When the chain owns `VERSION`,
  the bump is the chain's job — skip it and say so in the PR body.
- **Auto-merge is not merging red.** All-green merges itself; red stops the line until the
  branch is fixed. Bypassing rules means no reviewer wait, never a red override.
- **Discovered breakage is a fork in the road.** If the touched-surface run fails on code
  you did not touch, or the fix clearly spans two surfaces: stop, escalate to `task`, and
  say what you found. Quick-task has no room for scope creep, and a PR is not the place
  to discover a second problem.
- **Docs are not optional.** If the change invalidates or lands in governed documentation,
  the doc update AND its ledger/record step happen inside the same commit; a quick-task
  that leaves the docs gate red fails the next push from the checkout.
- **No verification theater.** Running the full suite to feel safe defeats the point and
  hides the touched-surface signal; if the full suite is genuinely required, escalate. The
  same theater in miniature is re-running a touched suite that already passed to watch it
  pass again — repetition is diagnosis work, not verification.

## Verification checklist

- [ ] The five shape questions all answered yes (one sentence, one surface, one commit,
      reversible, no rulings)
- [ ] Plan was three to six bullets; no tracking artifacts created
- [ ] Work happened on a branch cut from freshly pulled main — never on main, no worktree
- [ ] Touched-surface tests ran green — once per change, no stability re-runs
- [ ] CI verdict read and reported (CI alive), or local gates / manual full suite ran once before push (CI dead)
- [ ] Lint + docs gates ran for the touched paths; governed docs recorded if touched
- [ ] One focused review happened BEFORE the push (delegated for auth/money/data/contracts)
- [ ] Version bumped in the same commit iff the repo has no auto-bump chain; skipped and said so otherwise
- [ ] Exactly one commit on the branch; PR opened, auto-merge enabled, merged all-green with no review wait
- [ ] Report names the PR, the merge, and the untested surfaces explicitly
