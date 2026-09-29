---
name: task
description: >-
  Use when the user wants to start, continue, or finish a backlog task — "/task <id>", "start
  task X", "work on the next task", "finish this task". Runs it end-to-end as a
  token-tracked unit of work with low/high effort, `task-decomposition` into a `task-plan`
  whose `workflow` is a DAG of `step`s, MoE panels for high-effort, and automated task-ID
  derivation ({repo-alias}-{key}). Delegates planning/review to high-reasoning models
  and implementation to persona-routed agents. Project specifics from
  .ai-badger/config.json; source-control and PR behaviour from config-gated extensions.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos]
scope: default
metadata:
  hermes:
    tags: [task, orchestration, delegation, worktree]
    related_skills: [create-task-spec, commit-reminder, test-economy, multi-agent-communication]
---

# task orchestration skill

Runs one backlog task as a separated, token-tracked unit of work. High-leverage thinking —
planning and the final quality gate — is delegated to a high-reasoning model; implementation
models do the hands-on work; the orchestrating session integrates and tracks everything so a
dead session can be resumed.

**All project specifics come from `.ai-badger/config.json`** — never hardcode a build command,
a persona name, or a repository. Tracking data lives in `.ai-badger/task-tracking/` (gitignored).
Scripts live in this skill's `scripts/`. Before hand-writing any tracking store, read `references/file-schemas.md`.

## When NOT to Use

- A single-file typo fix or one-off question — no tracking, worktree, or delegation needed
- Work the user wants done inline in this session
- Anything where the token-tracked pipeline's overhead exceeds the task — use the plain workflow

## Default Loop

Every task follows a low- or high-effort loop. The loop is the spine; the phases below
(Phase 0–6) detail its steps.

Before starting, **ask the user whether the task runs the low or high `loop`**. When autonomous,
derive the loop after analyze — a best-effort match of loop to scope.

### Low-effort loop

prepare → analyze → plan → plan review → implementation → review implementation →
apply fixes → pr → gates → close task → reflect → merge

### High-effort loop

prepare → analyze → plan (MoE) → plan review (MoE) → implementation →
review implementation (MoE) → apply fixes → QA: test quality & coverage → pr →
gates → close task → reflect → merge

Integration step: required in the high-effort variant. The plan carries a join step when the
`workflow` has more than one sink; it depends on every sink and carries the cross-step tests.

### Step definitions

**prepare** — Create isolated worktree from fresh `main`, push branch, create draft PR.

**analyze** — Derive `taskId` (`{repo-alias}-{key}`). Extract scope, constraints, criteria.

**plan** — Run `task-decomposition` into a `task-plan` whose `workflow` is a DAG of
`step`s. Every step has ACs; the plan AC: all steps' ACs checked+met.

**plan review (MoE)** — High-effort: MoE panel (3 experts, subject-matched). At least one expert
different from plan authoring.

**implementation** — Code phase per persona routing. TDD mandatory.

**review implementation** — Low-effort: single subagent. High-effort: MoE panel (3 experts, at
least one different from plan MoE).

**QA: test quality & coverage** — High-effort only. Assess test honesty and coverage gaps.

**apply fixes** — Fold review findings into code. Trivial directly, larger via subagent. Re-run
build/test.

**pr** — Prepare PR for review, ensure CI runs.

**gates** — Per-repo gates from `config.json`. CI decides the local budget: alive →
touched surface locally, once, CI is the gate; CI dead → gates once, else one manual
full-suite run.

**close task** — Close tracking, remove clean worktree, update state files.

**reflect** — Check memory, semantica, session history. Distil durable facts. Promote to shared
context.

**merge** — Final PR merge.

### Task-ID derivation

`taskId` is **derived during the analyze step**. Formula:

    {repo-alias}-{key}

- **repo-alias:** Constant per repo. Examples: `jsaa`, `aib`, `air`, `ahp`. Determined by checking
  `config.json`'s `sourceControl.repoAlias`, memory, or deriving from repo name.
- **key:** 5 words conveying purpose. Hyphenated lowercase slug.

Example: `aib-default-loop`.

## Config contract (read first)

From `.ai-badger/config.json`:
- `commands.build` / `commands.test` / `commands.lint` — the verification commands for Phase 4.
- `personaRouting` — maps kinds of work to the scaffolded personas; drives Phase 3 dispatch.
- `sourceControl` — platform + repo/project URLs; **gates the source-control extension** (PR
  flow, review loop, issue/board integration). If `sourceControl.platform == "github"` and a
  `repoUrl` is present, this skill's `extensions/github/` fragment is active — follow it for the
  PR/review-loop steps below. Otherwise commit locally and integrate per your platform.

## Model & delegation policy

Spend high-reasoning capacity on plans and reviews, not implementations. Obtain reasoning via
delegation, not by assuming your own model.

- **Delegate to a high-reasoning agent** for planning (Phase 2) and the quality gate (Phase 4).
- **Delegate to implementation agents** per `config.json`'s `personaRouting`. TDD mandatory.
- **Delegate trivial mechanical work** (doc updates, rote refactors, test backfills) to a cheap model.
- **The orchestrating session does directly:** fetch the task, read docs, record token usage,
  per-subagent completion checks, build/test, and tiny surgical fixes.
- The ten prompting rules govern every brief. Read `references/prompting-rules.md` before composing briefs.

Roles, not models. Which concrete model fills each is bound by the agent-specific extension.

### Model tier contract

The task `loop` (low|high) chooses the orchestration loop; a step's `effort` (low|medium|high) drives model-tier selection.

The `level` field is optional (`low`, `medium`, or `high`), resolved to that model tier's preferred registry entry.
Precedence: explicit `model` > `level` > the step's `effort` used as the level > the session (or parent) default model.
An explicit `model` always wins over `level`; with neither, the dispatch inherits the session (or parent) default model.
Registry: `.ai-badger/model-groups.json`; see `.ai-badger/delegation.md` (reasoning-model dispatch).

Subagent prompts must be self-contained: scope, ACs, files, TDD rules, report-back shape. Parallelise
independent subagents. **Split work so it *can* run in parallel** — name shared-file steps, which
serialise by a `depends_on` edge or a merge, versus disjoint steps, which parallelise.

**Isolate every agent, at every depth: its own worktree and its own workspace id.** Two levels max.

**Write the brief so the lane can improve on it.** Before dispatching an end-to-end lane, read
`references/lane-dispatch-brief.md`.

**Reach for what exists.** A code graph, MCP server, or existing skill beats writing from scratch.

**If you cannot spawn subagents**, work directly in-session. Note reduced rigor in your summary.

## Phase 0 — Context hygiene

1. `python3 .ai-badger/skills/task/scripts/task_tracker.py status`. If a previous task is unfinished, finish or park it.
2. Confirm `.ai-badger/state.json` reflects the last finished task; repair if not.
3. If this session carries heavy history, tell the user to `/compact` (or start fresh) and
   re-invoke `/task <id>` on a clean context, then stop — unless autonomous.

## Phase 1 — Start

Entry: previous task finished or parked; clean-enough context.
Exit: loop chosen, tracker STARTED, worktree exists, five preflight blocks present,
research record gathered, taskId derived.

1. **Determine the loop.** Ask user low or high. When autonomous, derive after analyze.
2. **Analyze the task.** Resolve the task (issue URL or freeform scope/title). Read referenced docs.

   **Derive the taskId** per the derivation formula. Determine repo alias
   (check `config.json`'s `sourceControl.repoAlias`, memory, or derive from repo name).
   Compose the key from 5 words conveying purpose. Validate uniqueness vs existing entries.

   **If the argument is a path to a `spec.json` written by `create-task-spec`,** read it and its
   companion `.feature` file instead of treating the path as a title: the manifest supplies the
   scope, out-of-scope, constraints and deferred decisions, and the spec supplies the acceptance
   criteria. Feed both to the planning agent in Phase 2, and hold the non-deferred scenarios as
   Phase 4's pass condition.

   **Preflight checklist** (Rule 1): confirm the brief has objective, constraints, known unknowns,
   output contract, and stop condition. Fill missing blocks from review or ask the user.
3. Register: `python3 .ai-badger/skills/task/scripts/task_tracker.py start <taskId> --title "<title>" --branch task/<taskId>-<slug>`.
   `start` must exit 0 (when it does not, follow `references/tracking-visibility.md`).
4. Ask the user to rename the session to match the task (skip if autonomous).
5. **Work in the worktree `start` just created** — it prints the path, and it is
   `.ai-badger/worktrees/<taskId>` on the branch you passed to `--branch`. Every command for
   the rest of the task runs there, not in the main checkout.

   `start` records the branch name without creating anything (`--no-worktree` reverts to the
   old in-place behaviour). Work in the worktree: sharing one checkout lets another session
   switch branches mid-run. When more than one session shares the project, announce
   milestones on the bus — read `multi-agent-communication` when parallel work is active.
5. **Research before you plan, and plan the review first** (`evidence-first-research`
   formalises the method for non-trivial tasks; dispatch it rather than re-describing it).
   List what must be checked — every point in the request, marking which need research over
   guess. Gather the evidence into a research record where every finding cites its source
   path and every unverified claim is labelled a hypothesis. A plan before this record is a
   guess with a table around it. Run independent angles as parallel read-only lanes and
   consolidate per `multi-lane-report-assembly`.

## Phase 2 — PLANNING

Entry: research record exists with sources cited.
Exit: reviewed `task-plan` recorded; every step carries criteria and a gate; shared-file steps
serialised by an edge; join step present when >1 sink.

1. **Plan from what the research found.** Delegate decomposition (the `architect` persona),
   feeding it the task body, the research record and doc excerpts.

   **Split the plan into steps.** Run `task-decomposition`; the result is one validated
   `task-plan` whose `workflow` is a DAG of `step`s, one lane per step. Record the plan with
   `plan_create`; with no server available, write the plan file by hand in the frozen shape and
   say the graph is off. `depends_on` is the only ordering source; the top-level criterion:
   *all steps' ACs are checked and met*. A task with no plan row keeps its legacy plan file and
   manual checkboxes — never `plan_create` over an in-flight task.

   In the **low-effort** variant, a single high-reasoning agent creates the plan.
   In the **high-effort** variant, delegate to an MoE panel (default 3 experts) matching the
   task's subject area.

   The server derives the ready set and waves via `steps_ready` — say which steps run together;
   parallelism is designed in via `depends_on` edges, never assumed.

   **Every step carries acceptance criteria and a quality gate** — what must be true, and the run
   that proves it. A step without them is a wish. Where a step needs a specification or design
   first, produce one — prefer an installed skill. Run `design-tests` on
   the acceptance criteria before the first failing test — the test list is part of the plan,
   not of the implementation. When a step changes architecture or flow, present it with
   `archify` (Mermaid only when its runtime is missing). When status cannot match the plan,
   follow `references/tracking-visibility.md`.
2. **Plan review before dispatch.** In the **low-effort** variant, hand the drafted plan to a
   second high-reasoning agent for review. In the **high-effort** variant, delegate to an MoE
   panel (default 3 experts, at least one different from the plan-authoring experts) and have it
   attack structure, feasibility, budget arithmetic, and testability. Fold MUST/SHOULD findings
   back into the plan before any implementation dispatch. Same join discipline as Phase 4,
   applied early where defects cost least. When consolidating reviewed
   plan steps into lane briefs, follow `references/lane-dispatch-brief.md` — steps sharing
   a file serialise (add a `depends_on` edge or merge them), the rest parallelise.

## Phase 3 — Execute

1. Dispatch implementation subagents per `personaRouting`. Instruct every code subagent to write
   the failing test first (TDD). Name lanes per `references/tracking-visibility.md` when status
   must see them.

   **Operator contract** (Rule 4): each agent brief must include tool names, abort criteria,
   success predicate, and handoff conditions. Persona prose is optional, one short line only.
2. Record the lane's `total_tokens`:
   `python3 .ai-badger/skills/task/scripts/task_tracker.py subagent <taskId> <n>` or `--delegation <id>`.
   Interactive pi delegations return receipts by default; the `delegation-result` followUp carries
   `details.usage` (input+output); `background:false` blocks. Record on EVERY delegation
   completion; when harness receipts differ, see `references/tracking-visibility.md`.
3. Review each result at the seams (matches plan? acceptance criteria?). Send follow-ups back
   rather than rewriting, unless the fix is a few lines.
4. Commit and push per step (small commits). If the source-control extension is active,
   open a draft PR early per `extensions/github/`.

## Phase 4 — Quality gate

Entry: every step complete, all ACs checked and committed in the worktree.
Exit: CI green (or documented local-gate equivalent); review findings fixed or filed; QA
test quality reviewed (high-effort variant).

1. Run the modified surface's tests once.
2. **Review implementation.** In the **low-effort** variant, delegate review to a
   high-reasoning agent (the `code-reviewer` persona) with the diff, acceptance criteria,
   relevant architecture docs, and the build/test output. In the **high-effort** variant,
   delegate to an MoE panel (default 3 experts, at least one different from the plan MoE
   and plan review MoE). Ask it to judge implementation correctness (logic, edge cases, test
   honesty) and architecture (layer purity, consistency with docs).
3. **QA: test quality & coverage** (high-effort variant only). After the implementation review,
   delegate a dedicated quality assessment of test coverage and test honesty — can the tests
   actually fail? Are there gaps in coverage? See `review-tests` skill.
4. **Apply fixes.** Fix findings (trivial yourself, substantial via a subagent), re-run the
   touched suite, then proceed. If the diff adds or changes test files, also delegate
   `review-tests` on those files to `qa` (or the stack's `qa-backend`/`qa-frontend`) and treat
   a `blocker` finding the same as a red build. Docs-only tasks with no test changes skip
   `review-tests`; projects without CI fall back to the full local lane set as the pass
   condition. When push, CI, or PR trouble arises during this phase, follow the `git-work`
   skill before improvising.

### Review every join, not just every part

Each time work is combined — review findings into a plan, steps into one change, branches
into one PR — check the combination. Parts that pass alone fail together: two branches pick
the same version, one renames what another calls, a guard passes on each half and fails whole.

Run checks against the combined result, not the pieces.

**Then stop checking.** Execute the plan rather than re-reading it; a third self-pass costs as
much and finds less. Re-verify after integration only when
something changed, a claim is load-bearing, or a check has never been seen to fail. **Facts
are the exception**: anything from docs, an earlier run, or another's research gets re-checked
every time — that is what goes stale while your reasoning stays put.

### The slow suites

The pre-push hook runs the cheap checks; the slow ones belong to CI. CI's
result is the pass condition, not the green pre-push. Run a slow lane yourself only as the
sole active session with CI dead or absent. One run per suite per change; repetition is
diagnosis work. Docs, comments, and lint are never skipped.

## Phase 5 — Finish protocol

Entry: Phase 4 exit held.
Exit: merged, state updated, tracking closed.

1. If the source-control extension is active, follow `extensions/github/` for PR-ready, the
   review-round loop, and squash-merge. Otherwise integrate per your platform.
2. **Update state files:** prepend the finished task's lean entry to `.ai-badger/state.json`'s
   `completedTasks`, refresh `next`/`lastUpdated`; write verbose notes/decisions to the
   project's notes file.
3. Compaction check on CLAUDE.md if the project tracks one.
4. Close tracking: `python3 .ai-badger/skills/task/scripts/task_tracker.py finish <taskId>`. This
   also removes the task's worktree — **unless it still holds work that exists nowhere else** (see
   `references/tracking-visibility.md` when it refuses).
5. **Reflect.** Examine what was learned during this task. Check AiRaccoon memory for the
   current workspace, query semantica entries if available, and review the session history.
   Distil: what should be remembered as durable facts? Write any cross-task learnings to
   memory. Promote high-value entries to shared context if applicable. Record decisions in
   semantica.
6. Ask the user to grade the skill 0–5: `python3 .ai-badger/skills/task/scripts/task_tracker.py grade <taskId> <0-5>`
   (skip/leave unset if autonomous).
7. Report the task's token cost and recommend `/compact` or a fresh session before the next
   task — this is the default ending. **Authorized auto-continue** (only when the `auto-wm`
   skill's autonomic/partner mode is active, or the invocation explicitly said to continue):
   after Phase 6, compact per Phase 0, read the next task from `.ai-badger/state.json`'s `next`
   field (or the next unclaimed backlog item), and invoke this skill again. Otherwise start fresh
   and tell the user to re-invoke for the next task.

## Phase 6 — Documentation-gap audit

After integration, delegate a doc-audit agent (worktree-isolated) to check CLAUDE.md and the
project's docs against the merged code, fix small drift, and report gaps needing a decision.

## Gotchas

- **`start` with `--no-worktree` records a branch name nothing creates.** `status` then reports a
  branch that does not exist.
- **Interactive pi delegation = receipt, not answer.** Answers land as
  `delegation-result` followUps; review seams as they land.
- **Never rewrite always-loaded context files (`CLAUDE.md`, `.ai-badger/state.json`) mid-task.** Cache
  reads depend on byte-stable prefix (~10× cost); rewrite only between tasks.
- **Two levels of dispatch, no deeper.** A widening agent tree starves the machine.
- **Isolated = per-agent worktree + per-agent workspace id.** Shared build output defeats isolation.

## Recovery

`task_tracker.py` records the task's session id and resume command. Pass `--cron` to `start` to
install a resume cron. After resume, run `task_tracker.py reattach <taskId>` first, then continue.

> **Extensions:** PR/review/issue behavior in `extensions/github/`, model lanes in
> `extensions/claude/extension.md` — embedded by `welcome-ai-badger` per `config.json`.

## Verification Checklist

Each phase's Entry/Exit lines above are the checklist; this list carries only the
machine-run gates that close the task.

- [ ] `python3 .ai-badger/skills/task/scripts/task_tracker.py status` shows the task finished and `.ai-badger/state.json` reflects it
- [ ] All work lives in the worktree `start` created — no stray commits on the main checkout's branch
- [ ] Every step's acceptance gate ran; all step ACs checked; join step carried the cross-step tests
- [ ] Task-ID derived per the `{repo-alias}-{key}` formula and is unique
- [ ] Task `loop` (low or high) was determined before implementation began
- [ ] High-effort tasks ran QA test quality & coverage step
- [ ] `reflect` step examined memory, semantica, and session history for learnings
- [ ] `finish` left no worktree with unmerged or uncommitted work — `keptBecause` empty or resolved
- [ ] Token cost reported and compact/fresh-session advice given (or the auto-continue condition held)