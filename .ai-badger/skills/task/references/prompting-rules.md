# Prompting rules and dispatch lore (task skill reference)

Extracted from SKILL.md to keep the always-loaded body lean. Read this file when
composing subagent briefs or judging delegation quality — the body carries the one-line
versions; this file carries the reasoning.

## The ten prompting rules (research: docs/research/prompt-eng/)

1. **One-turn specification** — objective, constraints, known unknowns, output contract,
   stop condition, in the first turn; final ask last. Highest-value lever (Laban et al.
   multi-turn loss; IFEval constraint compounding).
2. **Consolidated restart** — after two failed revisions, restart with one merged prompt
   (~95% recovery vs ~15–20% for continued correction).
3. **Grounded feedback** — corrections cite failing checks, compiler/validator output, or
   source evidence. Unassisted self-critique degrades accuracy.
4. **Tool schema + success criteria beat persona** — spend effort on parameters, stop
   conditions, outcome predicates; role text is one line at most.
5. **Critical instruction placement** — first and last blocks carry weight; buried
   requirements get skipped.
6. **Reasoning scaffolding minimization** — no "think step by step" or prescriptive CoT
   plans on reasoning models unless the task is genuinely symbolic.
7. **Final output schema separation** — free-form reasoning first, machine-checkable
   schema last, so output stays auditable.
8. **Positive constraints + machine validation** — validators cost zero compliance
   budget; long negative lists compound against you.
9. **Few-shot only for format** — examples teach output shape, not capability.
10. **Prompt length / rule count is a real cost center** — every added rule compounds.

## Agent isolation, in full

"Isolated" has two axes, both mandatory per agent at every depth:

- **A worktree of its own** — one per agent, not one per session. Sessions sharing a
  checkout collide when either switches branches mid-run.
- **A workspace id of its own** in every shared store (memory bank, notes tier) —
  partial findings from one lane must never arrive as another's context.

Disjoint files are not isolation: agents sharing a tree share build output and
dependency caches, so one compiles against another's half-applied edit and no green run
says anything about its own change. Depth does not exempt anyone — an agent that
dispatches further owes its children the same two things.

Dispatch using your agent tool's native isolation rather than hand-made worktrees. Arm
per-directory approval modes for each new path, and re-run the gate on the merged
result because each per-agent run measured a different tree. Serialising removes the
collision by removing the parallelism — fall back to it only when work cannot split.

The specialized skill `worktree-agent-isolation` owns the worked cases: setup order,
draft-PR-per-lane, sequential wave merges, detached-HEAD traps, stray-commit repair.

## Cache-aware dispatch

Every subagent request prefix includes the project's always-loaded context. Keep those
files byte-stable within a task — rewrite only between tasks — so they serve as cache
reads (~10× cheaper). Prefer one multi-turn subagent over many one-shot dispatches for
related steps; use rewind-style backtracking over compaction inside a task; compact only
at phase boundaries.

## Slow suites belong to CI

The pre-push hook runs the seconds-cost checks. Full test suite, lint pass, end-to-end
journeys run in CI on every push, on the project's declared interpreter floor. Local
green is fast feedback; CI is the pass condition. Run a slow lane locally only as the
sole active session — two concurrent suites measure each other.

Historical note: the pre-push `--risk` switch was removed in 0.123.0 once slow lanes
moved to CI — it dropped nothing while announcing a trade no longer made.
