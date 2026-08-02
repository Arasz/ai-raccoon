---
name: review-changes
description: >-
  Use when reviewing a diff, PR, or a batch of changed files and you need to know where the risk
  concentrates — which changed units have the largest blast radius, whether the highest-risk ones
  are actually covered by tests, and whether the result is safe to merge. Trigger phrases: "review
  these changes", "how risky is this diff", "what's the blast radius", "did anything untested
  change", "rank these changes by risk". Not for a pass/fail preflight of style, security, and
  layering checks — that is `code-review-checklist`; run the checklist for the mechanical gates and
  reach for this skill to decide where its attention should concentrate. The two compose: checklist
  for gates, this skill for prioritization.
version: 1.0.0
author: ai-badger, after the code-review-graph skill templates
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [code-review, risk, blast-radius, testing]
    related_skills: [code-review-checklist, refactor-safely, debug-issue]
---

# Review changes

Rank changed units by blast radius, then check whether the riskiest ones are actually tested. This
skill answers "is this safe to merge" — not "does this follow style", which is
`code-review-checklist`'s job.

The workflow derives from the skill templates the `code-review-graph` project auto-installs
(MIT, © 2026 Tirth Kanani), rewritten here to be tool-agnostic: every step carries a baseline
that needs no graph server.

## Steps

1. **Establish what changed and against which base.**
   Accelerated: a code-graph change-detection call (e.g. `detect_changes`-shaped; discover the
   real tool name from the server's own listing) already carries risk scores. Baseline: `git diff
   <base>...HEAD --stat` plus `git log <base>..HEAD --oneline`; if the base is ambiguous, ask, or
   default to the merge base with the trunk branch.

2. **Rank changed units by blast radius**, not diff size — a 3-line change to a shared auth check
   outranks a 200-line change to one leaf component.
   Accelerated: an impact-radius / affected-flows call (e.g. `get_impact_radius`-shaped) returns
   callers, callees, and affected execution paths per unit in one query. Baseline: grep each
   changed symbol for callers (or the IDE's find-references) and note whether any sits on a
   critical path — auth, payment, data-write, public API surface.

3. **Check test coverage for every high-risk unit only** — not the whole diff.
   Accelerated: a tests-for query (e.g. `tests_for`-shaped, via a graph-query call). Baseline:
   search the test tree by the project's own naming convention (`.ai-badger/config.json`'s
   `commands.test` names the runner), then run that command and confirm the relevant test would
   fail if the change were reverted — a test file existing is not coverage.

4. **Report grouped by risk**, high to low. For any high-risk unit with no covering test, name the
   specific missing test case (input, expected behavior) — never write "consider adding tests".
   That sentence is the failure this skill exists to prevent: it reads as review but commits to
   nothing.

## Output format

For each risk tier (high / medium / low): what changed and why it's that tier (the caller/flow
that makes it risky, not "it's core"); test status — covered (name the test), partial (name the
gap), or untested with the specific missing case; overall recommendation — safe to merge / merge
with tracked test debt / block.

## Red flags — STOP

- Ranking by diff size instead of blast radius
- "Consider adding tests" without naming the missing case
- Reporting coverage from a test file's existence without having run it
- Skipping step 1's base — reviewing against the wrong diff is worse than not reviewing

Non-vacuous means every high-risk untested unit is named individually with its missing test case
attached — a report that names an untested high-risk item without one has not finished this skill.
