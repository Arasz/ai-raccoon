---
name: call-behaviorist
description: >-
  Use when ai-badger's own machinery needs to be observed — "did that hook even run?", "enable
  debug logging", "why is the drift notice silent?", "turn on the audit log", "what did the
  hooks do?" — or to check, tail, or switch off that logging. Records which hook ran, in which
  project, under which version, to an append-only log.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [observability, hooks, audit, logging]
    related_skills: [auto-wm, commit-reminder]
---

# call-behaviorist

Named for observing behaviour rather than asserting it. ai-badger is normally silent about its
own machinery, so "did that hook fire?" has no answer short of adding print statements and
re-scaffolding. This turns the machinery's own behaviour into a record.

**Off by default.** Nothing is written, and no directory is created, until you switch it on.

## Commands

All via `python3 .ai-badger/skills/call-behaviorist/scripts/behaviorist.py`:

| Command | Effect |
|---|---|
| `on [DURATION]` | Enable for **every project** (default 4h). Grammar: `4h`, `90m`, `1h30m`, or a bare number of hours — capped at 24h. `forever` (or `never`/`always`) means no expiry: on until `off`. |
| `on [DURATION] --project` | Enable for the current directory only |
| `off` | Disable |
| `status` | Mode, scope, expiry, record count |
| `tail [N]` | Last N records, one line each (default 20) |
| `analyze [--project DIR] [--json]` | Health state and findings for a project |
| `clear` | Truncate the log, recording the truncation |

`AI_BADGER_DEBUG=1` in the environment forces logging on regardless of stored state, for a
one-off run where editing state is inconvenient.

## What a record holds

`tail` renders records readably:

```
2026-07-27T09:22:56+00:00  ai_badger_hooks/session_start  skip  v0.30.0  project=/repo scaffold_version=0.30.0 framework_version=0.30.0
```

> Field-by-field record and event semantics: read references/record-format.md when interpreting `tail` or `analyze` output.

- **`version` is on every record** — it is the VERSION of the *copy of the code that ran*. This
  is what makes a stale plugin running against a newer scaffold visible rather than something
  you have to deduce.
- `event` distinguishes `start` from `skip`, so a hook that fired and exited early is
  distinguishable from one that never fired. That distinction is the whole point.
- `project` is recorded whenever it can be determined.

No tool input or file content is ever recorded. The one exception is the MCP-retrieval `query`
field below, which is recorded by default and has its own opt-out — see "Retrieval telemetry".

## Retrieval telemetry

The MCP tool index's retrieval path (`_find_relevant_tools` / `_extract_query_tags`, consumed by
`pre_llm_inject_context`) and its post-call tool-index check both log under the
`ai_badger_hooks/mcp_retrieval` component:

### The query field, and redacting it

`q` is the one field carrying user content — indispensable for diagnosing a miss or turning a
record into an eval fixture, and the one thing someone may not want recorded. It is recorded by
default. Setting `AI_BADGER_DEBUG_REDACT` in the environment drops that field only from every
record written afterward, leaving every other field intact so the log stays useful for counting.
The drop happens inside `debug_log.log_event` itself, at the point of writing — a redacted
record never contains the text, so there is nothing to scrub after the fact.

## Where things live

| Store | Purpose |
|---|---|
| `hook_state` KV in `~/.ai-badger/debug/audit.db` | Whether logging is on, its scope and expiry |
| `hook_audit` table in the same DB | The records, one JSON payload per row |

The audit DB is user-level and `0600` — the log says where you work and what ran, so it never
lands in a project directory or in git. `AI_BADGER_DEBUG_DIR` moves the whole sink. Records
older than 60 days are pruned, so disk stays bounded whatever the duration.

## Reading the log

`tail [N]` is for a quick look. For anything more, each `hook_audit` payload is one JSON
object:

```bash
python3 .ai-badger/skills/call-behaviorist/scripts/behaviorist.py tail 20
sqlite3 ~/.ai-badger/debug/audit.db "SELECT payload FROM hook_audit ORDER BY id DESC LIMIT 20"
```

The `sqlite3` query is the useful one when several copies of ai-badger are installed: it shows
which version each component actually ran at.

## Producing a health report

`analyze` compares what a project **registers** against what was **observed**, and hands you
findings rather than a verdict. Run it with `--json` and write the report yourself.

```bash
python3 .ai-badger/skills/call-behaviorist/scripts/behaviorist.py analyze --json
```

### Where the expected components come from

Hooks run from what is **registered** with the agent, so that is what is audited — in order,
`.claude/settings.json`, `.claude/settings.local.json`, then `.ai-badger/hooks/hooks.json`.
The last is ai-badger's own declaration and the only project-level record in a deployment that
registers hooks elsewhere (Hermes, Copilot), so it never stops counting. A script registered in
more than one of them is one component.

Components are named by their **project-relative path**, not their filename: several skills
ship a `user_prompt_hook.py`, and merging them lets one hook's silence hide behind another's
excuse. A hook ai-badger did not wire is still listed — someone else's hook is information, and
it lands in `not_instrumented` because it cannot report on itself. A hook whose command runs no
`.py` script (an installed binary, a shell one-liner) has nothing to inspect and is not listed.

> Finding meanings and health verdict rules: read references/findings.md when interpreting `analyze` output.

### What the log says about a *skill*

`hook_activity(project)` (a library call, not a subcommand) rolls the same records up per skill,
for the reader deciding what to prune: `{skill: {hooks, instrumented, records}}` over the
hook-shipping skills only, plus the project's total record count. den-refresh consumes it
(#172). The asymmetry is the point — records prove a skill is doing work here, and silence
proves nothing at all, because a skill that wires no hook can never appear in this log. Anything
built on it must never read absence as disuse.

**A record that names no project belongs to no project.** The log is user-wide, and a hook that
could not determine its project emits a record no analysis can place. Those are excluded from
`observed`, from the record count and from the verdict, and reported as `window.unattributed`
with the components they came from in `window.unattributed_components`. They are set aside, not
dropped — a non-zero count means a hook somewhere is still not attributing its records.

### Writing it up

1. Run `analyze --json` and read the findings. **Do not restate them.** For each one, check the
   actual file before claiming a cause — `not_instrumented` and `never_observed` look identical
   in a summary and mean opposite things.
2. Lead with what is *wrong*, not with counts. "Two wired hooks never fire" beats "5 findings".
3. Include the observation window and record count, so a reader knows how much evidence there
   is. A `degraded` verdict from three records deserves that caveat. If `window.unattributed`
   is non-zero, say so and name the components — evidence was set aside, and the reader is
   entitled to know how much.
4. Name the versions involved for any `version_skew`, **with the ranges they were observed in**
   — that is the actionable part, and it is what says which copy to remove. Do not report a
   `version_progression` as a fault; mention it only as the release train it is.

### Filing it

**Read the project's `CONTRIBUTING.md` first and follow it.** How issues are filed is a
project's own decision — the tracker, the required template, the labels, whether an issue is
even the right channel. This skill ships into repositories it knows nothing about, so it does
not prescribe a command.

If the project has no `CONTRIBUTING.md`, or it is silent on issues, ask before filing.

Two things regardless of process:

- **Do not paste raw JSON as the issue body.** The written report is the deliverable; the
  `--json` output is your evidence for it.
- Title it so the headline is legible in a list: `ai-badger health: <project> — <what is
  wrong>`, not `health report`.

## Gotchas

No environment-specific gotchas known.

## Turn it off when you are done

A bounded window expires on wall-clock time, checked on every event — no timer and no cron.
Durations are capped at 24 hours, so a window opened for an investigation closes itself.

`forever` opts out of that, for standing instrumentation: drift that only shows over weeks
cannot be caught by a window that closes overnight. What you give up is the automatic close,
so two things are worth knowing before choosing it:

- **It is a standing privacy exposure.** The log records where you work, which hooks ran and
  the MCP retrieval `q` field. Set `AI_BADGER_DEBUG_REDACT` to drop `q`, and remember the log
  spans every project on the machine, not just this one.
- **Disk is bounded, not unbounded.** Records older than 60 days are pruned from `hook_audit`
  whatever the duration — so `forever` costs a bounded slice of disk rather than a growing
  one. The real cost is that a busy component's recent records crowd a quiet one's evidence,
  which is why a hook that logs on every tool call is a bug rather than a preference.

Switch it off with `off` when the question is answered.

## Verification Checklist

- [ ] `status` shows logging enabled, with an expiry or `never`
- [ ] `tail` renders records one line each
- [ ] `analyze --json` exits 0 and names findings
- [ ] Report leads with what is wrong, includes the window and record count
- [ ] `version_skew` findings name the versions and the ranges they were observed in
- [ ] `window.unattributed` reported if non-zero
- [ ] Logging expired or switched off when done
