---
name: task-decomposition
description: >-
  Use when a researched task must become an executable plan — "decompose this", "split this
  into steps", "turn the research into a plan". Turns a brief into a `task-plan`: a DAG of
  steps with acceptance criteria, dependencies, effort and owned files; records it through
  the `task-graph` server (or its CLI twin), re-authors it when review finds defects, and
  names the join rule and the no-server fallback. `task` runs this in its plan phase;
  `quick-task` never does.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [task, planning, decomposition, dag, workflow]
    related_skills: [task, create-task-spec, status-report, design-tests, worktree-agent-isolation]
---

# task-decomposition

Turn a researched brief into one validated `task-plan`: a workflow DAG of `step`s, each one
lane's worth of work with acceptance criteria a verifier can fail. The plan is the contract
the `task` skill dispatches against; the graph is its single source of truth.

Before you split anything, read `references/decomposition-method.md` — it carries the worked
method, worked good and bad splits, and the MoE handoff. When a plan section, persona, or
another skill names a planning unit ("package", "subpackage", `WPn`), consult
`references/plan-vocabulary.md` for that surface's disposition. Before calling any tool, read
`references/mcp-plan-tools.md` for the exact input shape and its error code.

## When to Use

- A `task` run finished analyze and holds a research record; planning is the next phase.
- Someone asks to "decompose this", "plan this out", "split this into steps", "turn the
  research into a plan", "what is the work breakdown".
- Review findings changed the step set and the plan must be re-authored before execution
  (`plan_replace`), or a plan was rejected and needs a new shape.

## When NOT to Use

- **`quick-task`** — a quick task has no plan artifact and must not create graph state; a
  change that needs decomposition is `task` work (escalate instead).
- **Before the research record exists.** A plan written over a guess is a guess with a table
  around it; gather evidence first, then decompose.
- **To execute the plan.** This skill authors; `task` dispatches the lanes.
- **Over an in-flight task that has no plan row.** That is the legacy plan-file path:
  read and check the existing file, never create graph state underneath it.
- A single change with one acceptance criterion and no ordering — just do it.

## Inputs

1. **The analyze/research record** — findings with source paths, and the full request. Every
   request point is an input, including the ones the task key does not name.
2. **Optionally `create-task-spec`'s `spec.json` and its companion `.feature`**. The
   spec stays the requirements artifact; the plan is the executable decomposition that
   consumes it. Coverage rule: every non-deferred spec scenario maps to at least one step
   acceptance criterion; a deferred decision arrives as a constraint, never as a reopened
   unknown.
3. **The interface freeze** — the 12 frozen tool names, the plan-file shape and the CLI form.
   When a tool must be called, its exact input shape is in `references/mcp-plan-tools.md`.
4. **Availability** — the `task-graph` MCP server, the CLI twin, or neither. Availability
   selects the recording path, not the decomposition method.

## The decomposition contract

Five rules. They are the method; a step that violates one is a defect the plan review should
catch.

**Granularity — one step is one lane.** A step is a single agent dispatch that yields one
mergable change and one verifier run. It must fill the `lane-dispatch-brief` slots (Task,
Acceptance criteria, Files you own) without naming two independently verifiable deliverables.
Split when two deliverables have independent ACs, or when one step needs two worktrees (two
independent groups of files). Merge when a step's only content is running a gate — that is an
acceptance criterion, not a step — or when two steps always dispatch together.

**Actionability — a lane must be able to execute it from the step alone.** Every step carries
`id` (stable slug), `goal` (outcome sentence), `instructions` (files owned, approach, rejected
alternative, report-back), `effort` (`low`|`medium`|`high`), `depends_on` (only real
ordering), `files`, and at least one acceptance criterion with a `check` that can go red. An
AC whose comparison cannot fail is decoration; there is no step without a check.

`effort` is a dispatch hint: it resolves to a model tier through the registry's precedence —
explicit `model` > `level` > the step's `effort` used as the level > the session default. The
task loop's `low`/`high` is a different axis; it selects the pipeline, never the model.

**Error propagation — a failure blocks descendants, never siblings.** A failed step leaves its
dependents blocked until it is retried or skipped; unrelated branches keep running.
`step_complete` refuses while any dependency is incomplete, and `force` is an
orchestrator-only escape that requires evidence and a reason. Re-plan a failed step as a new
step with its own ACs rather than editing around it, and never call the plan complete while
any criterion is `unchecked`.

**Completeness — nothing in the input falls on the floor.** Every request point and every
research finding maps to at least one step, or is explicitly recorded as retired with a
reason. Every non-deferred spec scenario is covered by at least one AC. Over-decomposition is
a defect too: no "run the tests" step, and no step restating another step's AC.

**Stop rules — stop when, and only when:**
1. every request point maps to a step or is recorded retired;
2. every non-deferred spec scenario maps to at least one step AC;
3. the DAG is non-empty, acyclic, ids are unique, and every dependency names a known step;
4. the join rule below holds;
5. every step has at least one AC with a check that can go red.

## Steps and the join rule

The join is a property of the workflow, not a special kind of step.

- When the workflow has more than one sink (a step nothing depends on), it carries a **join
  step** that depends on every other sink. The join step's ACs are only the cross-step checks
  — the ones no single lane can prove.
- When the workflow has a single sink, that sink is the integration step: the cross-step ACs
  go on it and no extra step is added.
- `integration_ok` is derived from the graph and surfaced by `progress_checklist` and
  `plan_get(include:"state")`; it is never stored as a field on the plan or a step. A
  multi-sink workflow with no join step is a plan-quality finding, not a validation error.
- The `task` skill requires the join step for high-effort loops.

## Recording the plan

The plan lives in the graph. Author it, read it back as a review artifact, fold findings
through the server — never write the store by hand.

1. **`plan_create`** — `task_id`, `task_description_ref`, `task_context`, `loop`,
   `source_refs`, optional `research_ref`, and the `steps[]`. It validates the DAG, persists
   it at revision 0 and renders the plan file. Re-issuing identical content is idempotent
   (`created:false`); different content under an existing `task_id` is refused
   (`already-exists`) — revise through `plan_replace` instead.
2. **`steps_ready`** — the ready frontier and the waves packed around file and resource
   conflicts; this is the dispatch order, and it changes as statuses change. When the opt-in
   Jev advisory is enabled (`AI_BADGER_JEV=1` plus its per-capability switches), it may add
   serialization hints or tier upgrades on top of this output; it never removes a wave and
   never demotes a tier. When you enable it, `references/decomposition-method.md` carries the
   invocation and the fail-safe polarity.
3. **`plan_export`** — the stored document verbatim plus its schema URL. This is the review
   artifact: hand this to plan review, not a paraphrase.
4. **`plan_replace`** — re-author the whole plan under `expected_revision` when review
   findings change the step set. It refuses with `plan-in-progress` once any step left
   pending/skipped; re-plan before execution, not during it.

**Or the CLI.** With no MCP host, the CLI twin takes the same 12 names and the same arguments:

```bash
uv run --script .ai-badger/skills/task-decomposition/scripts/task_graph_cli.py <tool-name> --json <args>
```

`<tool-name>` is one of the frozen twelve and `<args>` is the JSON object the tool's input
model accepts. Exit 0 carries the payload, exit 1 the closed error envelope, exit 2 a usage
error.

**Degraded path.** When neither the server is listed nor the CLI runs, the graph is
off. Then write the plan file by hand to
`.ai-badger/task-tracking/plans/<YYYY-MM-DD>-<taskId>.md` in the frozen shape: one
`**S<N> …**` heading per step, one `- [ ]` per acceptance criterion, and a note in the
generated banner position saying `hand-written — graph off`. Say the graph is off out loud,
and never hand-write `tracking.db`. Manual checkboxes do not enforce failed/forced/blocked or
the join rule — those are graph-only semantics; checkbox state is the only status. Never call
`plan_create` over an in-flight task that has no plan row — that is the legacy plan-file path.

**First run is honest about `uv`.** On a cold `uv` cache the first `uv run --script` call
fetches `pydantic>=2.12,<3` before the tool starts, so the very first plan needs network; a
warm cache starts offline, and the hand-written file path above needs no network at all.

## Gotchas

- **`plan_replace` cannot run mid-flight.** Once a step left `pending`/`skipped`, the server
  refuses; finish or re-plan the run, never rewrite the history.
- **A produced plan file is not the source of truth.** The server renders it on every
  mutation; hand-editing it changes nothing the graph reads.
- **`plan_create` over a legacy task.** An in-flight task with a `**P<N>**` file and no graph
  row keeps its file and its manual checkboxes; there is no migration.
- **Waves are a snapshot.** `steps_ready` repacks after every status change; dispatch from
  the latest call, not a saved list.
- **CLI `--json` takes an object.** A JSON array, a bare string or a missing `--json` is a
  usage error (exit 2), not a tool error.
- **The join step is not bookkeeping to skip.** A multi-sink plan without one has no place
  for its cross-step checks, which is where integrations actually fail.

## Verification checklist

- [ ] Every step has at least one AC whose `check` can go red
- [ ] DAG: non-empty, acyclic, unique ids, every dependency known
- [ ] Join rule holds: multi-sink has a join step; single-sink carries the cross-step ACs
- [ ] Every request point maps to a step or is recorded retired
- [ ] Every non-deferred spec scenario maps to at least one step AC
- [ ] No "run the tests" step and no step restating another step's AC
- [ ] The plan was recorded with `plan_create` (or hand-written with the banner when the
      graph is off) — `tracking.db` was never hand-written
- [ ] `plan_export` was reviewed and review findings folded with `plan_replace` before any
      execution dispatch
