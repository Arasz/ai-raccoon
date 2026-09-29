# The decomposition method

The five rules in `SKILL.md` are stated as contract; this file is the worked method behind
them — how to decide a split, what a good step looks like next to a bad one, and who does the
work under each loop.

## Trace before you split

Start from the inputs, never from a blank list. Build a traceability table first: one row per
request point and per research finding, with the step id that will prove it or the word
`retired` and the reason. A missing row is discovered here, not after a lane has run.

Inputs that commonly hide rows:

- the request's parentheticals ("also update the docs") — they are deliverables too;
- a research finding labelled `unverified` — it maps to a verification step or it is retired,
  never silently absorbed;
- a non-deferred scenario in `spec.json` — each one needs at least one AC, and the AC's
  `check` must be the scenario's observable outcome.

## Granularity: the split and merge tests

One step is one lane: one dispatch, one mergable change, one verifier run.

**Split a candidate when:**

- it names two deliverables that a reviewer could accept independently ("add the endpoint"
  *and* "wire the UI") — each gets its own AC set, so each gets its own step;
- one part needs a different persona or stack than the other;
- it needs two worktrees because the file groups are disjoint and both can run at once;
- a gate finding on one deliverable would block an unrelated one.

**Merge two candidates when:**

- one is only "run the gate" over the other's output — a gate is an acceptance criterion on
  the producing step, never a step;
- the two always dispatch together, always share files, and always pass or fail together;
- one restates another's AC.

**Worked examples.**

| Candidate | Verdict | Why |
|---|---|---|
| "Implement the parser and its tests" | one step | tests are the AC of the same change |
| "Implement the parser" + "Run the parser tests" | merge | the second is a check, not a deliverable |
| "Add the API endpoint" + "Add the CLI verb" | split | independent ACs and independently reviewable; a shared schema file serialises by edge |
| "Migrate the schema" + "Update the docs" | split (usually) | different verification, different reviewer; merge only when the docs are the schema change's own contract text |
| "Refactor A and refactor B" sharing one module | split with `depends_on` | shared file ⇒ serialise by an edge, not by one oversized lane |
| "Do the whole feature" | split | no single dispatch fills the lane brief without naming independent deliverables |

## Actionability: the lane-brief fill test

Write each step's `instructions` as if the lane brief were the only context it gets. It must
name files owned, approach, the rejected alternative worth remembering, and the report-back
shape. If the brief cannot be filled, the step is underspecified.

Every step carries at least one acceptance criterion. The `check` is the command, test id or
artifact comparison that decides it — and it must be able to fail. Mirror the
`prove-the-check-fails` invariant: an AC whose comparison can only ever answer "yes", or whose
check is deferred to "see the tests", is decoration. Prefer a check a verifier runs, and say
which failure it is watching for when the command alone is ambiguous.

`depends_on` is the only ordering source. Add an edge for a real ordering or a shared file;
never for "it comes next in the narrative".

Effort is a dispatch hint, not a pipeline selector: `low`|`medium`|`high` resolves to a model
tier through the registry precedence (explicit `model` > `level` > the effort used as the
level > the session default). The task loop's `low`/`high` chooses the overall pipeline.

## Error propagation: states and escapes

The graph's states are `pending`, `in_progress`, `complete`, `failed`, `skipped`; `failed` is
retryable, `complete` and `skipped` are terminal. A failed step blocks its descendants, not
its siblings, because the graph computes readiness from dependency status alone.

- `step_complete` refuses while a dependency is incomplete — do not schedule around it.
- `force` is an orchestrator-only escape: it records the override with `forced:true` and a
  reason, and it does not change any criterion's status. Use it when the dependency's failure
  is genuinely irrelevant to this step, never to keep a wave packed.
- A failed step is re-planned as a new step with new ACs. Editing the failed step's goal to
  match what was actually done destroys the record of what was asked.
- A plan is not complete while any criterion is `unchecked`; a gate run that nobody recorded
  is a gate that did not happen.

## Completeness and the over-decomposition guard

Completeness is a two-sided constraint.

- Under: every request point and every research finding maps to a step or is recorded retired;
  every non-deferred spec scenario maps to at least one AC.
- Over: no "run the tests" step, no step restating another step's AC, no step whose only
  content is a summary of the plan. A step count is not a quality signal.

## The stop checklist

Stop decomposing when all five stop rules in `SKILL.md` hold. Practically: clear the
traceability table, verify the DAG (non-empty, acyclic, unique ids, known dependencies),
verify the join rule, and verify every step's check can go red. Then stop — an extra
revision pass after that costs more than it finds.

## The optional Jev advisory (off by default)

After `steps_ready` returns the frontier and its waves, an opt-in advisory can ask a classifier
model two questions: which model tier a step really needs, and whether two ready steps may
share a wave. It is **off unless `AI_BADGER_JEV=1`**, with per-capability switches
`AI_BADGER_JEV_TIER=1` and `AI_BADGER_JEV_WAVES=1`.

```bash
uv run --script .ai-badger/skills/task-decomposition/scripts/jev_choice.py <plan-file|-> --both --json
```

The input is a plan document (the `document` a `plan_export` returns, or the plan itself),
optionally carrying the `ready`, `waves`, `done` and `edges` lists copied from a `steps_ready`
payload. The output is `{"status": ..., "tier_proposals": ..., "wave_hints": ...}`.

Polarity is fail-safe, and the advisory is never authoritative:

- a tier proposal is an **upgrade only** — a `high` answer at confidence 0.6 or above, with no
declared `level` or `model` — and can never demote a step;
- a wave hint can only **add** serialization: any failure, a `serialize` answer, or confidence
below 0.7 serializes the pair. A pair the advisory would admit is still subject to the
deterministic file/resource rule — the hint never removes a wave the derivation packed;
- flags off, a missing key, or a failed call means **no proposal**, and the deterministic
result stands. Nothing reaches the network while the flags are off.

The advisory is an input to the plan review, not a substitute for it: record what it changed
and why, or leave it off.

## The MoE handoff

Both loops end at the same place: a plan validated by `plan_create`.

- **Low effort — one architect agent runs the method.** It reads the research record and the
  optional `spec.json`, builds the traceability table, applies the five rules, and calls
  `plan_create` itself. One author, one pass.
- **High effort — a panel proposes, a synthesizer merges.** The panel members each propose a
  complete decomposition from the same inputs; they do not call the tool. The synthesizer
  diffs the proposals, keeps the strongest split for each region, resolves disagreements by
  the five rules, and produces one plan, which it records with `plan_create`. The plan review
  that follows is a separate panel (at least one expert different from the proposing panel)
  and its MUST/SHOULD findings fold back through `plan_replace` — never a fresh `plan_create`
  under the same `task_id`.

Parallelism is designed, not discovered: name the steps that share files and serialise them
with an edge (or merge them), and let `steps_ready` pack the rest into waves.
