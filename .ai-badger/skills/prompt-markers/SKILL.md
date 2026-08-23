---
name: prompt-markers
description: >-
  Use when a prompt starts with a marker prefix — `h:`/`hint:` (a lead to validate before
  acting), `f:`/`feedback:` (a correction to apply immediately), `e:`/`extension:` (a request to
  widen scope), `q:`/`queue:` (queued task for after current work), or `i!:`/`important!:` (immediate
  emergency interrupt) — or when the user asks to add, change, or inspect those markers. The
  UserPromptSubmit hook detects them and injects the matching behaviour.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [prompts, markers, hooks, context]
    related_skills: [auto-wm, call-behaviorist]
---

# Prompt markers

A small set of one- or two-word prefixes a user can put at the very start of a prompt to give an
agent an explicit, machine-detectable signal about how to treat what follows — instead of relying
on the model to infer intent from phrasing alone, which is inconsistent under long or compacted
context.

## The markers

| Prefix | Meaning | Required behavior |
|---|---|---|
| `h:` / `hint:` | A potential insight or lead, not a command | Validate first — do a quick research pass (search the project, check relevant files/docs) before acting on it, and report what you found |
| `f:` / `feedback:` | Direct critique or correction on previous work | High priority — address it before other work, referring back to the specific point in session history, and cite the failing output, validator result, or source evidence behind the correction |
| `e:` / `extension:` | A request to expand the current task's scope | Analyze the new requirement; fold it into the current unit of work if it fits, or flag it for a follow-up task if it's too large |
| `q:` / `queue:` | A queued instruction to run after active work finishes | Finish active work first. Once complete, analyze and execute this queued instruction, incorporating context from all prior work |
| `i!:` / `important!:` | Immediate emergency interrupt | STOP IMMEDIATELY — pause or cancel running commands/subtasks, read the message, and react instantly before doing anything else |

Marker definitions (prefixes + the exact instruction text injected for each) live in
`markers-context.json`, next to this file — edit that file to add a marker or change its wording;
no code changes needed for that.

If a task receives two failed `f:` feedback rounds in a row, treat the thread as drifted and restart
with one merged prompt that includes the accepted constraints and the failing evidence instead of
layering another round of commentary on top of the same stale request.

## How detection works

A `UserPromptSubmit` hook (`scripts/user_prompt_hook.py`) reads the hook's JSON payload from
stdin, checks whether the prompt (after stripping leading whitespace) starts with one of the
configured prefixes case-insensitively, and — if so — emits the matching marker's instruction
text via the hook's `additionalContext` field. Claude Code merges `additionalContext` into what
the agent sees for that turn.

**Why `additionalContext` (append), never prepend or replace:** appending preserves the prefix of
the conversation exactly as it was, which is what makes prompt caching effective — a cached
prefix is only reusable if it stays byte-identical across turns. Prepending, or rewriting the
prompt outright, would invalidate the cache for that turn and every subsequent one. This
trade-off (and the alternatives considered — native system-prompt instructions, silently
rewriting the prompt) is recorded in ADR-0017 "Prompt markers for agent context injection" in the
project this skill was ported from; if the current project keeps ADRs, mirror that rationale
there instead of re-deriving it.

The hook is stdlib-only Python, resolves `markers-context.json` relative to its own location (so
it works regardless of where the skill is installed), and never touches the original prompt text
— the user's exact input is preserved; only extra context is added alongside it.

## Auditing

Every detected marker is recorded to a small history file so the record of what was injected
survives later compaction or summarization, even though the injected context itself doesn't
persist verbatim in a compacted transcript. This is best-effort and opt-in by convention: the
hook looks for an already-existing `.ai-badger` directory (walking up from the prompt's `cwd`) and,
only if one is found, writes/updates `.ai-badger/prompt-markers/marker-state.json` (capped at the
most recent 100 entries). If no such directory exists, the hook still injects context but skips
the audit write silently — it never creates project-tracking structure on its own.

## Gotchas

- **The hook *appends* via `additionalContext` and never rewrites the prompt.** Prepending or
  rewriting invalidates prompt caching for that turn and every subsequent one (rationale recorded
  in ADR-0017; mirror it in the project's ADRs instead of re-deriving).
- **Registration merges into existing arrays.** If the project already runs a
  `UserPromptSubmit` hook (e.g. task's session tracker), add an entry, never replace it — the
  host runs all registered hooks.
- **The audit write is best-effort by design.** It only fires when an `.ai-badger` directory
  already exists; a missing `marker-state.json` is not a hook failure.
- **Marker definitions live in `markers-context.json`.** Edit that file to add or change a
  marker, not the hook.

## Installation

Register the hook in the project's Claude Code settings (`.claude/settings.json` or
`.ai-badger`-scaffolded equivalent) under `UserPromptSubmit`:

```json
{
  "hooks": {
    "UserPromptSubmit": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "python3 <path-to-this-skill>/scripts/user_prompt_hook.py",
            "timeout": 10
          }
        ]
      }
    ]
  }
}
```

If the project already runs its own `UserPromptSubmit` hook (e.g. the `task` skill's session
tracker), add this as an additional entry in the same array rather than replacing it — Claude
Code runs all registered hooks for an event.

## A mid-turn marker never reaches the hook

`UserPromptSubmit` fires when a message **starts a turn**. A message sent **mid-turn** —
queued by the user while the agent is already working — is delivered to the model as an
attachment instead, and never passes through the hook. Its marker is therefore never
expanded, and nothing reports a failure: the hook did not error, it was never called.

Measured 2026-08-14 against a real session transcript
(`~/.claude/projects/<project>/<session>.jsonl`):

| Message | Record type | Hook ran |
|---|---|---|
| Sent at turn start | `type: "user"` with `promptSource`, `origin`, `entrypoint` | yes |
| Sent mid-turn | `type: "queue-operation"` + `type: "attachment"` | **no** |

Two mid-turn `f:` messages produced no `type: "user"` record at all — and no marker context,
no other `UserPromptSubmit` output either, which is what distinguishes this from a fault in
this skill. The hook itself is fine: fed the same text directly it returns the correct
`additionalContext` and exit 0.

**This is not fixable from a hook**, because there is no hook event for a queued message. The
mitigation is the standing list below, which is why that list has to be complete.

## Agent-facing contract

Whichever agent instruction file the project maintains (`CLAUDE.md`, …)
should tell agents that these markers exist and name the required behavior for each — the hook
delivers the instruction text at the moment a marker is used, but a standing mention in the
always-loaded instructions makes the behavior legible to a human reading the file, and is the
**only** thing that carries the behavior when the hook does not fire.

That list is generated from `features/common/templates/CLAUDE.md.tmpl` and
`HERMES.md.tmpl`, where it is written out longhand rather than derived from
`markers-context.json` — so adding a marker to the catalog does not add it to the templates,
and nothing compared the two. It drifted exactly that way: `q:` and `i!:` reached the catalog
and `HERMES.md.tmpl`, while `CLAUDE.md.tmpl` kept listing three for a release, taking both
Copilot files with it.

**Fix the template, not the agent file.** An agent file edited directly looks correct until the
next `scaffold.py` run silently reverts it.
`tests/test_prompt_marker_standing_list.py` compares both templates *and* every generated
agent file against `markers-context.json`, so the next marker added fails the build.
