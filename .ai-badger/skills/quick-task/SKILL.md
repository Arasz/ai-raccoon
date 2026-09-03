---
name: quick-task
description: >-
  Use when a change is small enough to skip the full task pipeline — one focused fix or
  small feature that fits a single commit pushed straight to main with no PR: a minimal
  plan, touched-surface tests only, the project's fast gates (lint, docs), one quick
  focused review, one commit. Escalate to `task` the moment the change outgrows that shape.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [workflow, fast-path, single-commit, tests, review]
    related_skills: [task, test-economy, code-review-checklist, status-report]
---

# quick-task

The fast lane between "just edit it" and the full `task` pipeline. One focused change,
planned in minutes, verified on the surface it touches, reviewed once, shipped as a single
commit on main. No branch, no PR, no plan document, no multi-lane review.

## The shape (all of it, or escalate)

A change qualifies for quick-task only when every answer is yes:

- **One sentence.** You can state the whole change in one sentence a reviewer could act on.
- **One surface.** It touches one area — one module, one component, one workflow file, one
  doc. Cross-layer work (API + client, schema + code, code + generated manifests) does not
  fit; that is `task` work.
- **One commit.** The finished work is exactly one commit. If you can already see it needs
  two (code + separate migration, feature + follow-up cleanup), it is not a quick-task.
- **Reversible.** A revert of that single commit restores main exactly. Anything stateful
  (infrastructure, data migrations, secrets) disqualifies.
- **No rulings needed.** The owner's intent is already known; no design decision is being
  made, only executed. New decisions are `task` (or `create-task-spec`) work.

Chores count: a dependency bump, a doc fix, a lint cleanup, a config tweak — as long as the
shape above holds.

## Flow

1. **Scope check.** State the one sentence and the touched surface. Run the five shape
   questions; any "no" means stop and escalate to `task`. Say out loud that you are in
   quick-task mode so the reduced ceremony is a decision, not an accident.

2. **Minimal plan.** Three to six bullets in the working notes — what changes, which files,
   which tests cover it, which docs need updating. No plan file, no ledger record, no
   tracking artifacts. If the plan needs more than six bullets, escalate.

3. **Implement with tests.** The change ships together with its tests: new tests for new
   behavior, updated tests for changed behavior, and a run of the tests that cover the
   touched surface. Follow the repo's own test discipline (which runner, which directory
   tests run from). Do not fix unrelated failures you discover — note them and move on;
   fixing the world is how quick-tasks become incidents.

4. **Fast gate.** Run exactly what the change can affect, and nothing more:
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

5. **Quick focused review.** One pass, before committing: re-read the diff against the plan
   bullets — does the diff do exactly what was planned, do the tests assert behavior rather
   than implementation, are there stray files, debug prints, or unrelated edits, does the
   commit message describe it. For anything touching auth, money, data loss, or generated
   contracts, make this pass a delegated second pair of eyes instead of self-review.

6. **Single commit, push.** Everything in one commit on main — code, tests, docs, and any
   regenerated artifacts together. Conventional message (`fix:`, `feat:`, `chore:`,
   `docs:` + scope); subject says what changed, body says why when it is not obvious.
   Amend before pushing if you find a gap; never stack fixup commits on main. Push, then
   verify the remote ref moved.

7. **Close the loop.** Report: what changed, what was tested and what was NOT (say the
   untested surfaces out loud), which gates ran, and — when CI is alive — the CI run that
   proves the change. No post-merge ritual; if the change needs one, it was not a quick-task.

## Gotchas

- **Direct-to-main means the review is the safety net.** Do it before the push, not
  after; a pushed quick-task is only as good as its one focused review.
- **The single-commit rule is the rollback story.** A revert must restore main exactly;
  that is why everything rides in one commit and why fixup commits on main are forbidden.
- **Discovered breakage is a fork in the road.** If the touched-surface run fails on code
  you did not touch, or the fix clearly spans two surfaces: stop, escalate to `task`, and
  say what you found. Quick-task has no room for scope creep, and main is not the place
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
- [ ] Touched-surface tests ran green — once per change, no stability re-runs
- [ ] CI verdict read and reported (CI alive), or local gates / manual full suite ran once before push (CI dead)
- [ ] Lint + docs gates ran for the touched paths; governed docs recorded if touched
- [ ] One focused review happened BEFORE the push (delegated for auth/money/data/contracts)
- [ ] Exactly one commit on main; remote ref verified moved
- [ ] Report names the untested surfaces explicitly
