---
name: code-review
description: >-
  Use when performing a code review in this repository — reviewing a pull request, a branch diff,
  or a requested re-review. This is the review entry point GitHub Copilot code review loads from
  .github/skills/ (a review-focused skill name and description, as GitHub's docs recommend); it
  routes to the deeper review skills this project ships — risk ranking, pass/fail gates,
  test-quality judgement, whole-project review — adjusted per project stack. Trigger phrases:
  "review this PR", "review these changes", "code review", "is this safe to merge".
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [code-review, github, copilot, routing, entry-point]
    related_skills: [code-review-checklist, review-changes, review-tests, design-tests, complete-project-scope-code-review]
---

# code-review

The review entry point for this repository. GitHub Copilot code review reads agent skills from
`.github/skills/` on the head branch, and is more likely to use a skill whose directory name and
description target review — this file is that entry point. Any other agent asked to review a diff
in this repository follows the same route.

This file routes; it does not review. The review work lives in sibling skills under
`.ai-badger/skills/` — open the ones the route names, in the order the change warrants.

## The route

Work top-down. Stop expanding when the verdict for the change is clear; do not stop before it is.

1. **Rank the diff by blast radius** — open `.ai-badger/skills/review-changes/SKILL.md` when the
   project has it. A 3-line change to shared auth outranks a 200-line change to one leaf
   component; rank before reading, so the deepest checks land on the riskiest units.
2. **Run the pass/fail gates** — open `.ai-badger/skills/code-review-checklist/SKILL.md` for
   every review. Sequential phases: build/tests/lint/secrets first, then layering, security,
   contracts. Report every FAIL in the finding shape the checklist defines — item, severity,
   location, evidence, impact, fix.
3. **Judge changed tests** — open `.ai-badger/skills/review-tests/SKILL.md` when the diff adds or
   changes test files. A vacuous gate or an unproven red is a finding, not a style note.
4. **Name the missing coverage** — open `.ai-badger/skills/design-tests/SKILL.md` when a
   high-risk unit has no covering test. Name the specific missing case (input, expected
   behaviour) — never "consider adding tests".
5. **Whole-project review** — open
   `.ai-badger/skills/complete-project-scope-code-review/SKILL.md` only when the request is the
   entire codebase rather than a diff; do not stretch a PR review into one.

**Baseline when a named sibling is not installed** (opt-in skills ship only when the project asked
for them): do that step inline at its cheapest — diff stat plus `git log` for ranking, the
checklist's Phase 1 gates verbatim, and every high-risk untested unit named individually with its
missing test case. Say which siblings were absent; the review is thinner for it, not silent.

## Reading Copilot's review (when Copilot ran, not you)

- Copilot always leaves a "Comment" review, never "Approve" or "Request changes" — its verdict
  does not block merge and does not count toward required approvals. Weigh its comments through
  the checklist; a green Copilot review does not substitute for Phase 1.
- Attributions at the bottom of a review comment name the agent skill or MCP server that produced
  it — follow them when a comment cites this project's skills.
- Re-review after a push is manual unless automatic reviews are configured; resolved
  conversations may repeat on re-review.
- Suggested changes can be accepted in bulk or handed to the Copilot cloud agent via
  "Fix with Copilot" — a suggested change is still a finding; check it against the diff before
  committing it.

## Review output

Findings in one shape, severity-ordered — item, severity, location, evidence, impact, fix — then
the merge recommendation: safe to merge / merge with tracked test debt / block. A review that
ends without a recommendation is incomplete; so is one whose recommendation does not follow from
its own findings.

## Stack adjustments

The sections below are scaffold-time merges: each ships only when the project's config detects
that stack, so what you read here is the adjustment for THIS project's stacks.


## dotnet review adjustments

Apply alongside the generic route when the diff touches C#.

- The checklist's dotnet phases are already in the gates you run: they merge into
  `.ai-badger/skills/code-review-checklist/SKILL.md` at scaffold time, so follow them there
  rather than hunting for a separate dotnet doc.
- Read `.ai-badger/skills/review-tests/references/stack-dotnet.md` **when** judging changed
  xUnit/NUnit tests — it carries the .NET-specific rule bodies (async-assertion traps,
  static-dependency and complexity-vs-coverage tooling pointers) the generic passes point at.
- Treat missing nullability annotations on new public API surface, `ConfigureAwait(false)` absent
  in library code, and `CancellationToken` not plumbed end-to-end as layering findings, not nits.
- Run the project's own `dotnet build` and `dotnet test` from config `commands` **before**
  approving Phase 1 — a green Copilot review is not a build.

## python review adjustments

Apply alongside the generic route when the diff touches Python.

- Read `.ai-badger/skills/review-tests/references/stack-python.md` **when** judging changed
  pytest tests and the file exists — it is a stub in some catalogs and says so; when it is a
  stub, fall back to the generic passes rather than inventing stack rules.
- Weight fixture isolation and shared-state teardown as correctness findings: a test suite that
  passes only in file order is a non-determinism finding, not a hygiene note.
- Treat unseeded randomness, wall-clock reads, and ambient environment variables in production
  paths as testability findings when no seam exists to control them.
- Run the project's own lint and typecheck from config `commands` **before** approving Phase 1 —
  Python typing drift is invisible to a review that only reads the diff.

## Gotchas

- Skills and instructions are read from the head branch, so guidance changed in this same PR
  governs its own review — editing review rules and expecting the old rules is the mistake.
- The route is order-sensitive: ranking after reading wastes the reading; judging tests before
  ranking spends the test-quality pass on low-risk files.
- Dismissing a Copilot conversation is not an instruction to Copilot — on re-review the same
  comment can return; fix the code or refute the finding in the thread instead.
