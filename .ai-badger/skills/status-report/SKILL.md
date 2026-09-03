---
name: status-report
description: >-
  Use when the user asks where things stand mid-task — "status", "status report", "where are
  we", "what's the current task", "task progress", "what's next", "subagent status", "is the
  delegation done" — or wants a progress snapshot while work is still running. Answers NOW
  from the /task tracking files: current task, progress as a checklist, what is next, and
  sub-agent/delegation status. Important by default: never deferred to task end, never
  delegated, never turned into analysis.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [task, status, delegation, tracking]
    related_skills: [task, auto-wm, prompt-markers]
---

# Status report

A fast, honest snapshot of where the task pipeline stands, answerable while work is still in
flight. It reads the tracking state the task skill already writes — no new bookkeeping, no
long analysis, no waiting.

## Default behavior: important, interrupt-grade

A status request is IMPORTANT by default. Handle it at the first natural boundary — the end
of the current command, nothing more. Nobody asking "where are we?" wants to wait twenty
minutes for the task to end; answering late is answering wrong.

- Do not defer it to task end, queue it behind subagents, or fold it into the running task's
  plan. Answer now.
- Do not delegate the report anywhere. One script run, then answer in your own words.
- An explicit `!` importance marker (`status!:` or any `!`-marker) overrides everything else —
  preempt immediately per the prompt-markers contract.
- If you genuinely cannot answer for seconds (a command is mid-flight), say so and answer the
  moment it returns.

## Procedure

1. Run the script from the project root:

   ```
   python3 .ai-badger/skills/status-report/scripts/status_report.py
   ```

   Add `--target <project-root>` to report on another checkout; `--json` for the machine
   form. It exits 0 on every reporting path — missing or corrupt tracking files render
   their section's placeholder ("(no task in progress)", "(no plan file)", "(not found)",
   "(no live lanes)") instead of failing the report.

2. Answer with the four sections, in this order, using the script's output as the source of
   truth: **Current task** (plus any other open tasks), **Progress checklist** (plan
   packages and checkbox counts), **What's next** (quote `state.json`'s `next` field
   verbatim), **Sub-agents & delegation** (recorded subagent entries, live lane worktrees,
   live sessions).

3. Label inference as inference. The script prints the loop's step order
   (prepare > analyze > plan > … > merge) as a reference line; locating "we are at step X"
   from signals — branch exists, plan exists, subagents recorded, PR state — is YOUR
   inference, so phrase it as one ("plan exists and P1 is delegated — you're in
   implementation"). Never present a guessed phase as recorded fact.

4. Add one line of live truth the files cannot hold: what THIS session is doing right now
   (current phase, delegations in flight, what you are about to do next). Keep it visually
   apart from the script-derived sections — records say what happened, you say what is
   happening.

5. Nothing in progress: say so, show the last finished task and the `next` field, and offer
   to start the next task — nothing more.

## Where each section draws from

| Section | Source |
|---|---|
| Current task | latest IN_PROGRESS row in the `tasks` table of `.ai-badger/task-tracking/tracking.db` |
| Progress checklist | `task-tracking/plans/*.md` — package headings + `- [x]` counts |
| What's next | `state.json` `next` field, verbatim |
| Sub-agents | `token_usage` subagent records + `worktrees/` lanes + the `sessions` table |

## Gotchas

- An empty recorded-subagent list does NOT mean no delegation is running — records land only
  on completion (`task_tracker.py subagent`), so mid-flight delegations are invisible there.
  Live evidence is lane worktrees, the `sessions` table, and your own session context.
- Stale IN_PROGRESS entries from dead sessions show up as current (latest-started wins).
  Report them honestly — do not silently pick "the one that looks active" and do not start
  finishing or parking them unprompted.
- When no plan filename carries the task id, the report falls back to the newest plan and
  says so — verify it is actually this task's plan before quoting its checklist as progress.
- The checklist counts only `- [ ]`/`- [x]` checkbox lines. Plans written without checkboxes
  report "no checkbox items — read the plan file"; that is a plan-format gap, not zero
  progress.
- The script reads only files under the target root; run it with `--target` when your cwd is
  elsewhere, or every section reads as "(not found)".

## Red flags — STOP

- Do not turn a status request into work: no new task, no delegation, no refactoring of the
  tracking files it read.
- Do not poll or wait on running subagents to "complete the picture" — report what is known
  now and what is in flight.
- Do not fix what the report surfaces (stale tasks, missing plans) unless asked; offer it.
- Do not fabricate progress. "(not found)" is a valid answer; a plausible guess is not.

## Verification Checklist

- [ ] Script ran once, exited 0, output carried all four section headers
- [ ] Current task named by id; other open tasks listed, not hidden
- [ ] Any loop-step positioning phrased as inference, not recorded fact
- [ ] `next` quoted verbatim, not paraphrased
- [ ] One live-session line added from this session's own context, kept apart from records

## Files

- `SKILL.md` — this file
- `scripts/status_report.py` — the snapshot script (stdlib-only, exit 0 on reporting paths)
