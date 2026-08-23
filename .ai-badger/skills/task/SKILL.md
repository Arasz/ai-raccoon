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
- `commands.build` / `commands.test` / `commands.lint` — the verification commands for Phase 3.
- `personaRouting` — maps kinds of work to the scaffolded personas; drives Phase 2 dispatch.
- `sourceControl` — platform + repo/project URLs; **gates the source-control extension** (PR
  flow, review loop, issue/board integration). If `sourceControl.platform == "github"` and a
  `repoUrl` is present, this skill's `extensions/github/` fragment is active — follow it for the
  PR/review-loop steps below. Otherwise commit locally and integrate per your platform.

## Model & delegation policy

Spend high-reasoning capacity on plans, decomposition, and review — not on typing
implementations. The orchestrating session obtains that reasoning by explicit delegation, not by
assuming its own model.

- **Delegate to a high-reasoning agent** (planning/decomposition in Phase 1; the final
  correctness + architecture gate in Phase 3). Prefix such calls' description to keep the model
  visible at a glance.
- **Delegate to implementation agents** matched to the work, using the personas from
  `config.json`'s `personaRouting`. TDD is mandatory for code.
- **One-turn specification** — State the objective, constraints, data sources, and success criteria
  in the first turn; keep the final ask last.
- **Consolidated restart** — After two failed revision turns, restart with one merged prompt instead
  of continuing the same thread and compounding drift.
- **Grounded feedback** — Every correction must cite the failing check, validator output, compiler
  error, or source evidence behind the change before proposing the next patch.
- **Critical instruction placement** — Put the highest-priority requirements in the first or last
  block of the prompt, never buried in the middle.
- **Reasoning scaffolding minimization** — Avoid prescriptive CoT plans on modern reasoning models
  unless the task is genuine symbolic reasoning; give the goal and constraints instead.
- **Reasoning model policy** (Rule 6): when the target model is a reasoning-capable model, do not
  add "think step by step," prescriptive CoT plans, or few-shot chain-of-thought. State the goal,
  constraints, and success criteria; let the model reason internally. Use CoT scaffolding only on
  standard models for math/symbolic tasks where it has verified benefit.
- **Final output schema separation** — Keep free-form reasoning separate from the final schema and
  emit the final schema last.
- **Delegate trivial mechanical work** (doc/comment updates, rote refactors, test backfills) to a
  cheap model.
- **The orchestrating session does directly:** fetch the task, read docs, record token usage, the
  lightweight per-subagent completion check, run the configured build/test, and tiny surgical
  fixes found during the quality gate.

These are roles, not models. Which concrete model fills each role — and why the subscription's
metering makes that the cheap choice rather than merely the fast one — is bound by the
agent-specific extension for your coding agent (`extensions/claude/` for Claude).

Subagent prompts must be self-contained: scope, acceptance criteria, files/docs to read, the
project's TDD + code-style rules (point them at CLAUDE.md), and what to report back. Run
independent subagents in parallel.

**Split work so it *can* run in parallel.** A large item that one agent works through in sequence
is usually several items that could have run at once. Do the split while planning, and name which
sections share a file — those serialise, the rest do not.

**Isolate every agent, at every depth.** "Isolated" is exact, and it has two axes:

- **A worktree of its own** — the agent's workspace on disk. One per agent, not one per session.
- **A workspace id of its own** in every shared store that supports one — the memory bank, the
  scratch/notes tier, anywhere in-progress state accumulates. Its notes stay in that workspace and
  are consolidated or discarded when the agent finishes.

This applies to **every** agent in the tree, not just the ones you dispatch directly: an agent that
dispatches its own agent owes each of them the same two things. Depth does not exempt anyone; a
sub-agent sharing its parent's tree is the same collision one level down, and harder to see.

**Disjoint files are not isolation.** Agents sharing one tree share its build output, dependency
cache, and whatever state a build writes beside the source — so one agent compiles against another's
half-applied edit, and a green or red run then says nothing about its own change. Sharing one
workspace id has the same shape in the notes store: partial findings from one agent are read as
another's context. Both failures are quiet — nothing is lost, but agents block on each other and no
per-agent result can be cited.

Dispatch using the isolation your agent tool provides rather than creating worktrees by hand — a
manual step before each dispatch is the one that gets skipped when the work feels urgent. Two things
travel with the worktree and are easy to forget: any per-directory permission or auto-approval mode
must be armed for the new path too, or the agent stalls waiting for an answer nobody is there to
give; and **the gate is still re-run on the merged result**, because each per-agent run measured a
different tree.

Serialising the dispatches also removes the collision — by removing the parallelism. Prefer
isolation; fall back to sequential only when the work genuinely cannot be split.

**Two levels of dispatch, no more.** You dispatch; those agents may dispatch once; nothing
deeper. The cap is about the machine rather than the design: every live agent costs memory and a
share of the CPU, and a tree that widens without bound starves the work already running.

**Write the brief so the lane can improve on it.** Before dispatching an agent that owns a
unit of work end to end, read `references/lane-dispatch-brief.md` — it carries the prompt
shape, and the reason each part of it is there.

**Reach for whatever tool makes the work smaller.** A code graph, an MCP server, an existing
skill, a script the repo already has — check what is installed before writing something that
already exists. This is not permission to add tooling mid-task; it is a reminder that the
expensive path is often the one nobody checked for a shortcut.

**Cache-aware dispatch:** every agent's request prefix includes your project's always-loaded
context (CLAUDE.md/AGENTS.md-equivalent instructions, `.ai-badger/state.json`, and any other
files your project loads on every turn) — keep them byte-stable within a task (never rewrite them
mid-task; the finish protocol writes state *between* tasks) so they serve as cache reads at
roughly a tenth of the cost instead of a fresh write. Subagent caches are independent cold starts
on a ~5-minute TTL, so: prefer one multi-turn subagent over many one-shot dispatches for a
cluster of related steps (amortises the cold start), and use `/rewind` rather than `/compact` to
backtrack within a task (rewind reuses the cached prefix; compact pays for a fresh summary
write). Compact only at task boundaries (Phase 0).

**How a finished task is judged.** `token-usage.json` records `cacheEfficiency`, `modelMix`,
`outputByModel` and `dispatches`; `python3 .ai-badger/skills/task/scripts/task_tracker.py status` summarises them. Judge a run by its
**model mix** — the share of output produced by the mid and cheap tiers, over the main transcript
*and* its subagents together — not by cache efficiency, which does not discriminate. A run whose
dispatches are mostly `general-purpose` is not routing to this project's personas, whatever
`personaRouting` says.

Subagent transcripts are written beside the session's, not inside it, so a per-dispatch split is
available without chasing `parentUuid`. Where those files live, the numbers behind the model-mix
rule, and why the agent panel's `model` field can disagree with the transcript, are in
`extensions/claude/extension.md` — read it when interpreting the numbers, not on every dispatch.

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

1. Resolve the task (an issue URL, or freeform text used as scope/title; cross-check the project
   board via the source-control extension if active). Read the referenced docs.
   **If the argument is a path to a `spec.json` written by `create-task-spec`,** read it and its
   companion `.feature` file instead of treating the path as a title: the manifest supplies the
   scope, out-of-scope, constraints and deferred decisions, and the spec supplies the acceptance
   criteria. Feed both to the planning agent in step 6, and hold the non-deferred scenarios as
   Phase 3's pass condition.

   **Preflight checklist** (Rule 1 — one-turn specification): before dispatching any agent or
   writing any code, confirm the task brief contains all five blocks. If any are missing, fill
   them in from the review or ask the user — an incomplete brief is the most expensive way to
   start a task:
   - **Objective**: what the task must produce.
   - **Constraints**: what the task must not break or change.
   - **Known unknowns**: what needs research before implementation.
   - **Output contract**: the shape of the deliverable (file, PR, test suite, doc).
   - **Stop condition**: when the task is done — the gate that proves it.
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
5. **Review before you plan, and plan the review first.** Write down what has to be checked to
   answer the task — every point in the request, and which of them need research rather than a
   guess. Then run that review and gather the evidence. A plan written before the review is a
   guess with a table around it.
6. **Plan from what the review found.** Delegate decomposition to a high-reasoning agent (the
   `architect` persona), feeding it the task body, the review findings and doc excerpts.

   Split the plan into sections that can be worked independently, and say which may run at the
   same time. Parallelism has to be designed in; it does not arrive on its own.

   **Every point carries acceptance criteria and a quality gate** — what must be true, and the run
   that proves it. A point without them is a wish. Where a point needs a specification or a design
   before it can be built, produce one, and look for an installed skill that formalises that shape
   before writing a bespoke document. Before the first failing test, run `design-tests` on the
   acceptance criteria — the test list is part of the plan, not of the implementation.

## Phase 2 — Execute

1. Dispatch implementation subagents per `personaRouting`. Instruct every code subagent to write
   the failing test first (TDD).

   **Operator contract** (Rule 4 — tool schema over persona): every dispatched agent's brief must
   include these four blocks before any role text or context. Persona prose is optional and should
   be one short line only:
   - **Tool names and when-to-use**: which tools the agent should reach for first.
   - **When-not-to-use / abort criteria**: when to stop trying and escalate.
   - **Success predicate**: the concrete check that proves the agent's work is done.
   - **Handoff conditions**: what the agent reports back and in what shape.
2. Record each subagent's `total_tokens` on completion:
   `python3 .ai-badger/skills/task/scripts/task_tracker.py subagent <taskId> <total_tokens> --description "<what it did>"`.
   To record a delegation by id instead of a manual count, pass `--delegation <id>`; the
   session source that owns the task decides how the delegation's tokens are read. The two
   are mutually exclusive.
3. Review each result at the seams (matches plan? acceptance criteria?). Send follow-ups back
   rather than rewriting, unless the fix is a few lines.
4. Commit and push per work package (small commits). If the source-control extension is active,
   open a draft PR early per `extensions/github/`.

## Phase 3 — Quality gate

Run the configured `commands.build` and `commands.test` yourself and capture output. Then
delegate a review to a high-reasoning agent (the `code-reviewer` persona) with the diff,
acceptance criteria, relevant architecture docs, and the build/test output. Ask it to judge
implementation correctness (logic, edge cases, test honesty) and architecture (layer purity,
consistency with docs). Fix findings (trivial yourself, substantial via a subagent), re-run
build/test, then proceed. If the diff adds or changes test files, also delegate `review-tests`
on those files to `qa` (or the stack's `qa-backend`/`qa-frontend`) and treat a `blocker` finding
the same as a red build.

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

The pre-push hook runs the checks that cost seconds. The slow ones — the full test suite, the
lint pass, any end-to-end or integration journey — belong to CI, which runs them on every push
to every branch on the project's declared floor rather than on whatever the developer's machine
happens to have.

That makes the local hook fast feedback, not the pass condition:

- **CI is the gate.** Treat its result as this phase's pass condition, not the green pre-push.
- Run a slow lane yourself when you want it before pushing — the runner takes a lane by name.
- Run it as the only session working at that moment — two full suites at once measure each other.

> There used to be a `--risk` switch here that put the automated gates into a limited mode. It
> was removed in 0.123.0: once the slow lanes moved to CI it dropped nothing, while the push
> still announced a trade it was no longer making.

## Phase 4 — Finish protocol

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
   user's original invocation explicitly said to continue to the next task): after Phase 5
   completes, compact per Phase 0 guidance, read the next task from `.ai-badger/state.json`'s
   `next` field (or the next unclaimed item on your configured backlog source), and invoke this
   skill again for that task. If neither condition holds and no user is available, start a fresh
   session and tell the user to re-invoke the skill so the next task starts on a clean context.

## Phase 5 — Documentation-gap audit

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
- **"Isolated" means per agent, at every depth, on two axes: its own worktree and its own workspace
  id.** Being *in* a worktree is not the same as each agent having *its own*, and an agent that
  dispatches further owes its children the same. Disjoint files still share build output and
  dependency state, so an agent can block on another's half-applied edit and no per-agent gate
  result can be trusted; a shared workspace id does the same to in-progress notes. Arm any
  per-directory approval mode for each new path, and re-run the gate on the merged result.

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

- [ ] `python3 .ai-badger/skills/task/scripts/task_tracker.py status` shows the task finished and `.ai-badger/state.json` reflects it
- [ ] All work lives in the worktree `start` created — no stray commits on the main checkout's branch
- [ ] Every plan point's acceptance gate ran
- [ ] `finish` left no worktree with unmerged or uncommitted work — `keptBecause` empty or resolved
- [ ] Token cost reported and compact/fresh-session advice given (or the auto-continue condition held)
