# Spec elicitation playbook (create-task-spec in practice)

Session-tested adaptation (2026-08-04, file-watcher spec: 20 rules → 62 scenarios → emit
→ two-branch implementation).

## Modes and pacing

- This user answers terse confirmations ("yes", "1-5 default", "a") to BATCHED questions
  and rules every card inline via `f:` markers. Batch 4-6 questions per round.
- A wall of 15 scenario-title prompts confused more than it elicited ("1. yes 2. yes...").
  The approved mode is (a): the agent DRAFTS all scenario titles and steps as a proposal;
  the user corrects. Offer modes explicitly ("(a) I draft, you correct, or (b) you
  dictate") — this user picks (a) every time.
- Re-ask pacing: wait ≥1 minute before re-asking an unanswered question (user f:).
  Fill the gap with useful prep (read the manifest schema, verify, check the source)
  instead of nagging.
- `f:` corrections are rulings, not noise: apply immediately, record with provenance.

## Recording rulings

- Every ruling goes into the .feature as a dated comment under the affected Rule:
  `# 2026-08-04 (f): <ruling>`. The feature file is the decision record; the spec.json
  `cards` array mirrors it for the task skill.
- When the user renames a rule (e.g. drops the framework name from a title), keep the
  mechanics comment — provenance lives in the earlier docs, not the spec title.

## spec_holes.py semantics (read the script before trusting the gate)

- A Rule is satisfied by ONE scenario; the rest are step-less holes
  (`example-without-steps`) — the Stage-05 queue.
- `@deferred` holes are reported but do not gate (exit code counts non-deferred only).
- The checker strips indentation, so a user's editor re-indent (2→4 spaces) is harmless.
- Mid-elicitation the gate is RED BY DESIGN; green is unreachable until every rule has a
  stepped scenario. "Ad-hoc PASS" ≠ suite green.

## Emit mechanics

- Keep TWO copies: working draft under `docs/work/features-<name>/` (provenance) and the
  shipped contract under `docs/features/<name>/`; spec.json's `specFile` points at the
  shipped path. Verify BOTH feature copies (spec_holes exit 0) and both manifests
  (json.load + required keys) after any edit.
- Copy the shipped feature/spec into the task worktree and commit as the task's first
  commit (untracked files don't travel).
- Re-verification of an unchanged file: one stat/sha is proof enough; re-running the
  identical check on identical bytes is theater.
- Post-emit scenario edits (new rulings after emit): patch BOTH feature copies and both
  spec.json copies in one multi-file patch; re-run both gates.

## Scenarios that resist black-box Gherkin

- Concurrency limits, hash-skip internals, watcher-loop containment: keep as unit-level
  implementation tests (D5-style ruling), or the Then-steps become internal-state
  assertions the skill forbids.
- Time-based rules (1s tick): FakeTimeProvider + `Advance(1s)` in the test context;
  real-FS scenarios need bounded polling (≤5s) for OS event delivery.
- "Exactly one entry" vs chunking: pin the interpretation in the plan's step-author note
  ("one content set", not row-count-1).
