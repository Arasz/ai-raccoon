# Plan vocabulary

`step` is the unit of work everywhere in the task plan. A `task-plan` is a DAG of steps; each
step is one lane's dispatch with its own acceptance criteria. Other surfaces keep their own
words for a batch of work — this file maps them onto the plan vocabulary and keeps the
callable tool names straight.

## Where "package" still appears

| Surface | Disposition | Why |
|---|---|---|
| `task`'s plan-phase sentences | rewrite to step vocabulary | the loop consumes a `task-plan`; "package" is retired from it |
| `task/extensions/github/extension.md` | rewrite | "as each package lands" becomes "as each step lands" |
| `task/extensions/claude/extension.md` | rewrite | "two packages disagree about a contract" becomes "two steps" |
| `task/references/tracking-visibility.md` | rewrite | the plan is server-rendered; the hand-written fallback keeps the filename and review-exclusion rules; `**S<N>**` headings, legacy `**P<N>**` still read |
| `multi-agent-communication/SKILL.md` | rewrite | broadcast at step/join boundaries |
| `worktree-agent-isolation/{SKILL.md,references/shared-worktree-collisions.md}` | rewrite | "parallel packages" becomes "parallel steps"; "after a package lands" becomes "after a step lands" |
| `quick-task/SKILL.md` | exempt + one boundary sentence | a quick task has no plan artifact; the sentence says so and points to `task` |
| `status-report/SKILL.md` + `scripts/status_report.py` | rewrite | progress reads the graph (`progress_checklist`) first, the generated plan file second; the parser accepts `**P<N>**` and `**S<N>**` headings and labels them `[package]` vs `[step]` by prefix |
| `personas/delegator.md` | exempt + boundary note | a dispatch *package* there is a batch of steps; the task-plan unit is `step` |
| `complete-project-scope-code-review/**` | exempt + boundary note | a review *work package* becomes a `step` when the review plan is handed to `task` |
| `design-gate-audit` | exempt + boundary note | design-document vocabulary, not the task plan |
| `review-tests` (`references/plan-format.md`, `SKILL.md`) | exempt + mapping | `WPn` is a source label; the review rows map 1:1 to steps |
| `scripts-tooling-refactor` + `behavior-pinning-derivation.md` | exempt | refactor batching, not the plan unit |
| `invariants/small-commits-early-draft-pr.md` | exempt + note | commit sizing is not the plan unit; "one step = one commit" would be false |
| `ai-raccoon-memory/scripts/query_pipeline.py` | exempt, no touch | its embedded delegator contract has prompt bytes pinned to pi goldens by `tests/test_memory_context_jev.py`; editing the prose would break the pin |
| `.ai-badger/skills/**` mirrors | generated | never hand-edited; the scaffold owns them |

## Names the server never exposed

Some planning documents were written before the tool surface froze and mention a few tool
names the server does not expose. A lane rewriting any planning document normalises them to
the frozen twelve. The stale side of each row is spelled in two pieces on purpose: no file
under this skill may contain a callable-looking stale tool name, and the whole skill tree is
grep-checked for exactly that. An agent must only ever read names the server exposes.

| Stale name (never call it) | Frozen name to call instead |
|---|---|
| `plan_` + `build` — the old creation call | `plan_create` |
| `plan_` + `progress` — the old progress read | `progress_checklist` |
| `ac_` + `record` — the old per-criterion write | `ac_check` |
| `plan_` + `state` — the old state read | `progress_checklist` + `plan_get(include:"state")` |

`plan_id` does not exist anywhere in the I/O. Plan identity is `task_id`, and
`PlanRef = {task_id}` is the only identity shape the 12 tools accept.

## Reading rule

When any surface outside the map says "package", "subpackage", "work package" or `WPn`, read
the row above before editing; when a surface inside the map still says one of those words, it
is drift and the rewrite is expected. The frozen names live in `mcp-plan-tools.md` — when in
doubt, that list wins.
