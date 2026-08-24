---
name: task
description: >-
  Use when the user wants to start, continue, or finish a backlog task — "/task <id>", "start
  task X", "work on the next task", "finish this task". Runs it end-to-end as a cleanly
  separated, token-tracked unit of work with model delegation: a high-reasoning model plans and
  reviews, implementation models do the hands-on work. Project specifics come from
  .ai-badger/config.json; source-control and PR behaviour from config-gated extensions.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos]
scope: default
metadata:
  hermes:
    tags: [task, orchestration, delegation, worktree]
    related_skills: [create-task-spec, commit-reminder]
---

# task orchestration skill

Runs one backlog task as a separated, token-tracked unit of work. High-leverage thinking —
planning and the final quality gate — is delegated to a high-reasoning model; implementation
models do the hands-on work; the orchestrating session integrates and tracks everything so a
dead session can be resumed.

**All project specifics come from `.ai-badger/config.json`** — never hardcode a build command,
a persona name, or a repository. Tracking data lives in `.ai-badger/task-tracking/` (gitignored).
Scripts live in this skill's `scripts/`. Read `references/file-schemas.md` before hand-writing or repairing any tracking file — it carries the exact shape of each one.

## When NOT to Use

- A single-file typo fix or one-off question — no tracking, worktree, or delegation needed
- Work the user wants done inline in this session
- Anything where the token-tracked pipeline's overhead exceeds the task — use the plain workflow

## Config contract (read first)

From `.ai-badger/config.json`:
- `commands.build` / `commands.test` / `commands.lint` — the verification commands for Phase 4.
- `personaRouting` — maps kinds of work to the scaffolded personas; drives Phase 3 dispatch.
- `sourceControl` — platform + repo/project URLs; **gates the source-control extension** (PR
  flow, review loop, issue/board integration). If `sourceControl.platform == "github"` and a
  `repoUrl` is present, this skill's `extensions/github/` fragment is active — follow it for the
  PR/review-loop steps below. Otherwise commit locally and integrate per your platform.

## Model & delegation policy

Spend high-reasoning capacity on plans, decomposition, and review — not on typing
implementations. The orchestrating session obtains that reasoning by explicit delegation, not by
assuming its own model.

- **Delegate to a high-reasoning agent** (planning/decomposition in Phase 2; the final
  correctness + architecture gate in Phase 4). Prefix such calls' description to keep the model
  visible at a glance.
- **Delegate to implementation agents** matched to the work, using the personas from
  `config.json`'s `personaRouting`. TDD is mandatory for code.
- The ten prompting rules (one-turn specification, consolidated restart, grounded feedback,
  schema-last output, positive constraints) govern every brief you write.
- **Delegate trivial mechanical work** (doc/comment updates, rote refactors, test backfills) to a
  cheap model.
- **The orchestrating session does directly:** fetch the task, read docs, record token usage, the
  lightweight per-subagent completion check, run the configured build/test, and tiny surgical
  fixes found during the quality gate.

Read `references/prompting-rules.md` before composing any subagent brief — it carries each
rule's rationale plus the full agent-isolation contract.

These are roles, not models. Which concrete model fills each role — and why the subscription's
metering makes that the cheap choice rather than merely the fast one — is bound by the
agent-specific extension for your coding agent (`extensions/claude/` for Claude).

Subagent prompts must be self-contained: scope, acceptance criteria, files/docs to read, the
project's TDD + code-style rules (point them at CLAUDE.md), and what to report back. Run
independent subagents in parallel.

**Split work so it *can* run in parallel.** A large item that one agent works through in sequence
is usually several items that could have run at once. Do the split while planning, and name which
sections share a file — those serialise, the rest do not.

**Isolate every agent, at every depth: its own worktree and its own workspace id** in shared
stores. Disjoint files are not isolation — shared build output means no green run proves anything
about its own change. Two dispatch levels maximum. Follow `worktree-agent-isolation` when
running parallel lanes; it owns the worked cases and the failure modes.

**Write the brief so the lane can improve on it.** Before dispatching an agent that owns a
unit of work end to end, read `references/lane-dispatch-brief.md` — it carries the prompt
shape, and the reason each part of it is there.

**Reach for whatever tool makes the work smaller.** A code graph, an MCP server, an existing
skill, a script the repo already has — check what is installed before writing something that
already exists. This is not permission to add tooling mid-task; it is a reminder that the
expensive path is often the one nobody checked for a shortcut.

**How a finished task is judged.** Judge by **model mix** — the share of output from mid/cheap
tiers — not cache efficiency. See `extensions/claude/extension.md` for numbers.

**If you cannot spawn subagents** (you are running as a subagent yourself, or the Agent tool is
unavailable), do the work directly in-session at whatever model is available — the workflow's
tracking and finish protocol still apply, but note in your summary that planning/review ran at
reduced rigor since high-reasoning delegation wasn't possible.

## Phase 0 — Context hygiene

1. `python3 .ai-badger/skills/task/scripts/task_tracker.py status`. If a previous task is unfinished, finish or park it.
2. Confirm `.ai-badger/state.json` reflects the last finished task; repair if not.
3. If this session carries heavy history, tell the user to `/compact` (or start fresh) and
   re-invoke `/task <id>` on a clean context, then stop — unless autonomous.

## Phase 1 — Start

Entry: previous task finished or parked; clean-enough context.
Exit: tracker STARTED, worktree exists, five preflight blocks present, research
record gathered.

1. Resolve the task (an issue URL, or freeform text used as scope/title; cross-check the project
   board via the source-control extension if active). Read the referenced docs.
   **If the argument is a path to a `spec.json` written by `create-task-spec`,** read it and its
   companion `.feature` file instead of treating the path as a title: the manifest supplies the
   scope, out-of-scope, constraints and deferred decisions, and the spec supplies the acceptance
   criteria. Feed both to the planning agent in Phase 2, and hold the non-deferred scenarios as
   Phase 4's pass condition.

   **Preflight checklist** (Rule 1): confirm the brief has objective, constraints, known unknowns,
   output contract, and stop condition. Fill missing blocks from review or ask the user.
2. Register: `python3 .ai-badger/skills/task/scripts/task_tracker.py start <taskId> --title "<title>" --branch task/<taskId>-<slug>`.
3. Ask the user to rename the session to match the task (skip if autonomous).
4. **Work in the worktree `start` just created** — it prints the path, and it is
   `.ai-badger/worktrees/<taskId>` on the branch you passed to `--branch`. Every command for
   the rest of the task runs there, not in the main checkout.

   This step used to read "create/switch to the task branch", and `start` recorded the branch name
   without creating anything. A recorded name that nothing creates is worse than no field: `status`
   reports the branch, so the tracker looks like it is managing something it never touched. On
   2026-08-01 that put two commits on `main` in one session. Pass `--no-worktree` if you genuinely
   want the old behaviour; the branch is still recorded either way.

   A worktree is also what makes concurrent sessions safe. Sessions share one checkout, so a second
   agent switching branches mid-run changes the files under the first one — measured the same day:
   a push failed because the tree moved to `main` while its tests were running.
5. **Research before you plan, and plan the review first** (`evidence-first-research`
   formalises the method for non-trivial tasks; dispatch it rather than re-describing it).
   Write down what has to be checked to answer the task — every point in the request, and
   which of them need research rather than a guess. Then run that review and gather the
   evidence into a research record where every finding cites its source path and every
   unverified claim is labelled a hypothesis. A plan written before this record exists is a
   guess with a table around it. When several independent angles need evidence, run them as
   parallel read-only lanes and consolidate per `multi-lane-report-assembly`.

## Phase 2 — PLANNING

Entry: research record exists with sources cited.
Exit: reviewed plan; every point carries criteria and a gate; parallelism named.

1. **Plan from what the research found.** Delegate decomposition to a high-reasoning agent (the
   `architect` persona), feeding it the task body, the research record and doc excerpts.

   Split the plan into sections that can be worked independently, and say which may run at the
   same time. Parallelism has to be designed in; it does not arrive on its own.

   **Every point carries acceptance criteria and a quality gate** — what must be true, and the run
   that proves it. A point without them is a wish. Where a point needs a specification or a design
   before it can be built, produce one, and look for an installed skill that formalises that shape
   before writing a bespoke document. Before the first failing test, run `design-tests` on the
   acceptance criteria — the test list is part of the plan, not of the implementation.
2. **Plan review before dispatch.** Hand the drafted plan to a second high-reasoning agent (or a
   small review MoE — different experts than wrote it) and have it attack structure, feasibility,
   budget arithmetic, and testability. Fold MUST/SHOULD findings back into the plan before any
   implementation dispatch. This is the same join discipline Phase 4 applies later, applied early
   where a defect costs least. When consolidating reviewed plan sections into lane briefs, follow
   `references/lane-dispatch-brief.md` — sections sharing a file serialise, the rest
   parallelise.

## Phase 3 — Execute

1. Dispatch implementation subagents per `personaRouting`. Instruct every code subagent to write
   the failing test first (TDD).

   **Operator contract** (Rule 4): each agent brief must include tool names, abort criteria,
   success predicate, and handoff conditions. Persona prose is optional, one short line only.
2. Record each subagent's `total_tokens` on completion:
   `python3 .ai-badger/skills/task/scripts/task_tracker.py subagent <taskId> <total_tokens> --description "<what it did>"`.
   To record a delegation by id instead of a manual count, pass `--delegation <id>`; the
   session source that owns the task decides how the delegation's tokens are read. The two
   are mutually exclusive.
3. Review each result at the seams (matches plan? acceptance criteria?). Send follow-ups back
   rather than rewriting, unless the fix is a few lines.
4. Commit and push per work package (small commits). If the source-control extension is active,
   open a draft PR early per `extensions/github/`.

## Phase 4 — Quality gate

Entry: all plan points implemented and committed in the worktree.
Exit: CI green (or documented local-gate equivalent); review findings fixed or filed.

Run the configured `commands.build` and `commands.test` yourself and capture output. Then
delegate a review to a high-reasoning agent (the `code-reviewer` persona) with the diff,
acceptance criteria, relevant architecture docs, and the build/test output. Ask it to judge
implementation correctness (logic, edge cases, test honesty) and architecture (layer purity,
consistency with docs). Fix findings (trivial yourself, substantial via a subagent), re-run
build/test, then proceed. If the diff adds or changes test files, also delegate `review-tests`
on those files to `qa` (or the stack's `qa-backend`/`qa-frontend`) and treat a `blocker` finding
the same as a red build. Docs-only tasks with no test changes skip `review-tests`; projects
without CI fall back to the full local lane set as the pass condition. When push, CI, or PR
trouble arises during this phase, follow the `git-work` skill before improvising.

### Review every join, not just every part

Each time separate work is combined — the review findings into a plan, several plan sections into
one change, several subagents' branches into one PR — check that the combination still works.
Parts that each passed alone routinely fail together: two branches pick the same version, one
renames what another calls, a guard passes on each half and fails on the whole.

Run the checks against the combined result, not against the pieces you already ran them on.

**Then stop checking.** Execute what the plan says rather than re-reading it for reassurance; a
third pass over your own reasoning finds much less than the first and costs the same. Re-verify
after an integration when there is a reason to — something changed underneath, a claim is load
bearing, a check has never actually been seen to fail. **Facts are the exception**: anything
taken from documentation, an earlier run, or someone else's research gets re-checked against its
source every time, because that is what goes stale while your reasoning stays put.

### The slow suites

The pre-push hook runs the checks that cost seconds; the slow ones belong to CI on every
push. **CI is the gate** — treat its result as this phase's pass condition, not the green
pre-push. Run a slow lane yourself before pushing only as the sole active session. See
`references/prompting-rules.md` for why slow lanes live in CI.

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
   also removes the task's worktree — **unless it still holds work that exists nowhere else**, in
   which case it refuses, says what it found, and leaves the directory alone. Read the
   `worktree.keptBecause` field in the output; a kept worktree means something is unmerged or
   uncommitted, not that cleanup failed. Resolve it and re-run, or pass `--keep-worktree` when you
   are deliberately leaving it in place.
5. Ask the user to grade the skill 0–5: `python3 .ai-badger/skills/task/scripts/task_tracker.py grade <taskId> <0-5>`
   (skip/leave unset if autonomous).
6. Report the task's token cost and recommend `/compact` or a fresh session before the next
   task — this is the default ending. **Authorized auto-continue** (alternative path, only when
   an observable condition holds: the `auto-wm` skill's autonomic/partner mode is active, or the
   user's original invocation explicitly said to continue to the next task): after Phase 6
   completes, compact per Phase 0 guidance, read the next task from `.ai-badger/state.json`'s
   `next` field (or the next unclaimed item on your configured backlog source), and invoke this
   skill again for that task. If neither condition holds and no user is available, start a fresh
   session and tell the user to re-invoke the skill so the next task starts on a clean context.

## Phase 6 — Documentation-gap audit

After integration, delegate a doc-audit agent (worktree-isolated) to check CLAUDE.md and the
project's docs against the merged code, fix small drift, and report gaps needing a decision.

## Gotchas

- **`start` with `--no-worktree` records a branch name nothing creates.** `status` then reports a
  branch that does not exist (2026-08-01: two commits landed on `main`).
- **`finish` refuses and keeps the worktree when it holds work that exists nowhere else.** Read the
  `worktree.keptBecause` field; a kept worktree is unmerged or uncommitted work, not failed cleanup.
- **Never rewrite always-loaded context files (`CLAUDE.md`, `.ai-badger/state.json`) mid-task.**
  Subagent cache reads depend on a byte-stable prefix (~10× cost); rewrite only between tasks.
- **Two levels of dispatch, no deeper.** A widening agent tree starves the machine.
- **"Isolated" means per agent, at every depth: its own worktree and its own workspace id.**
  Disjoint files still share build output; arm per-directory approval for each new path.

## Recovery

`task_tracker.py` records each task's session id and resume command. Pass `--cron` to `start` to
also install a resume cron that watches for stalled sessions — it is opt-in, since it writes to
your crontab. If you wake in a resumed session mid-task, run
`python3 .ai-badger/skills/task/scripts/task_tracker.py reattach <taskId>` first, then continue.

> **Extensions:** source-control PR/issue/review-loop behavior and agent-specific model lanes
> are defined in `extensions/<name>/` and are embedded by `welcome-ai-badger` only when
> `config.json` supplies the required data. The base skill above stays platform-, stack- and
> model-neutral.

## Verification Checklist

Each phase's Entry/Exit lines above are the checklist; this list carries only the
machine-run gates that close the task.

- [ ] `python3 .ai-badger/skills/task/scripts/task_tracker.py status` shows the task finished and `.ai-badger/state.json` reflects it
- [ ] All work lives in the worktree `start` created — no stray commits on the main checkout's branch
- [ ] Every plan point's acceptance gate ran
- [ ] `finish` left no worktree with unmerged or uncommitted work — `keptBecause` empty or resolved
- [ ] Token cost reported and compact/fresh-session advice given (or the auto-continue condition held)
