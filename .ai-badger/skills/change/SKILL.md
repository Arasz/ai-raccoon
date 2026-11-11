---
name: change
description: >-
  Use when making an ad-hoc change with no issue or task behind it — fix a failing unit
  test, refactor a method, rename something, tweak a config value, adjust one behavior,
  "quick one", "just change X", "small update" — any edit to the project's state that has a
  distinct, small, limited scope and is not a tracked backlog task. Applies even when the
  request says "no ceremony" or "just do it".
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [workflow, small-change, tdd, review, docs, version]
    related_skills: [task, quick-task, test-economy, code-review-checklist]
---

# change

The smallest lane: one bounded change, made by the main agent alone, still carrying the core
of `task` — plan, review the plan, implement test-first, review the diff. Small scope shrinks
each phase; it never removes one. "No ceremony" from the requester means keep it short, not
skip a phase: the four phases together cost a few minutes on a two-line change.

## Fits, or escalate

Use `change` only while all of these hold; the first "no" means stop and escalate to `task`:

- The whole change fits one sentence a reviewer could act on.
- It touches one surface — one module, one test file, one config, one doc — plus the docs and
  release files its impact sweep finds.
- No design decision is being made; the intent is already settled.
- You can name the test that proves it before you write any code.

**No delegation.** The main agent does every phase itself — no subagents, no review lanes.
If the change needs another agent's eyes (auth, money, data loss, a public contract), it is
not a `change`; escalate to `task`.

## 1. Plan

State four lines in your reply before touching a file:

1. **Goal** — the one sentence.
2. **Files** — every file the change edits.
3. **Proof** — the test that will fail now and pass after, and the reason it fails now.
4. **Impact sweep** — search for the changed name or behavior (`git grep`) across docs,
   README, examples, config schemas, and changelog. List each hit that the change makes
   wrong, plus whether the repo's release rule requires a VERSION bump and changelog entry
   for this kind of change (read CONTRIBUTING, CLAUDE.md/AGENTS.md, RELEASING — not memory).

## 2. Review the plan

Before the first edit, check the plan against these questions and fix it where one fails:

- Does the proof test fail for the reason stated, or would it pass by accident?
- Did the sweep search, or guess? A hit list with no search behind it is a guess.
- Is there a simpler shape — fewer files, an existing helper, no new abstraction?
- Is it still one surface? A sweep that grew past one surface means escalate to `task`.

Then cut a branch from the freshly pulled default branch (`main`, `master`, whatever HEAD
tracks) and never commit to the default branch itself.

## 3. Implement test-first

1. Write the proof test. Run it and watch it fail with the reason from the plan. A test that
   passes before the fix, or fails for another reason, is fixed first.
2. Make the smallest code change that turns it green.
3. Run the touched surface's tests once after the last edit; an edit after a run makes the
   next run new, a repeat with no edit between is not verification.
4. Apply every impact-sweep item in the same change: docs and README text that is now wrong,
   VERSION bump and changelog entry when the release rule asks for them, regenerated files.

## 4. Review the diff

1. Read every hunk of the full diff (`git diff`, plus `git status` for new files). When the
   output is truncated or compressed, read each changed file's diff on its own until every
   hunk has been seen — an unread hunk is an unreviewed hunk.
2. Check the diff against the plan: every planned file changed, no unplanned file changed,
   no debug output, no unrelated reformatting.
3. Confirm each impact-sweep item landed (doc text, VERSION, changelog).
4. Run the repo's lint for the touched languages — the command its instruction files or CI
   name. When the repo defines none, say so in the report instead of inventing one.
5. Commit with a message that says what changed and why; ship it through the repo's PR rule.

## Gotchas

- **The release rule decides the bump, not the size of the diff.** A two-line change that
  rejects input callers used to send is a breaking change under semver. When the repo's rule
  does not name the level, pick it from semver and say which in the commit message.
- **Compressed tool output hides hunks.** Command-output proxies and pagers elide parts of a
  long diff without failing; that is why step 4 reads file by file.
- **Missing scaffolding is not a waiver.** No remote means the change ends at a committed
  branch; no RELEASING file means read what exists. Report each gap, skip no phase.

## Red flags

| Thought | Reality |
|---|---|
| "They said no ceremony" | That limits length, not phases. Each phase is minutes here. |
| "It's two lines, no plan needed" | The plan is four lines; the sweep in it is what catches the stale README. |
| "The test obviously fails" | Run it. A test never seen red proves nothing. |
| "I'll update the docs if someone notices" | The sweep's hits ship in this change or the change is not done. |
| "The diff output was cut, but I know what I wrote" | Read the hunk. Memory of an edit is not a review of it. |
| "A subagent could review this quickly" | Then it is not a `change`; escalate to `task`. |
| "No remote, so committing on main is fine" | Cut the branch anyway; it is the revert boundary. |

## Done when

- [ ] Plan written: goal, files, proof, impact sweep — then reviewed and fixed
- [ ] Proof test seen failing for the planned reason, then passing
- [ ] Touched-surface tests and lint green
- [ ] Every impact-sweep hit applied: docs, README, VERSION and changelog as the repo requires
- [ ] Every hunk of the diff read and matched to the plan
- [ ] Work on a branch, not the default branch; done by the main agent alone
