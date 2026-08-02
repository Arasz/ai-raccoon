---
name: call-behaviorist
description: >-
  Use when ai-badger's own machinery needs to be observed — "did that hook even run?", "enable
  debug logging", "why is the drift notice silent?", "turn on the audit log", "what did the
  hooks do?" — or to check, tail, or switch off that logging. Records which hook ran, in which
  project, under which version, to an append-only log.
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
| `on [DURATION]` | Enable for **every project** (default 4h). Grammar: `4h`, `90m`, `1h30m`, or a bare number of hours. Capped at 24h. |
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

On disk they are compact, one JSON object per line:

```json
{"t":"2026-07-27T09:22:56+00:00","c":"ai_badger_hooks/session_start","e":"skip","v":"0.30.0","p":"/repo"}
```

| Key | Meaning |
|---|---|
| `t` | timestamp, UTC, seconds |
| `c` | component — which hook or script |
| `e` | event — `start`, `skip`, or a domain outcome |
| `v` | version of the copy of the code that ran |
| `p` | project directory, when determinable |
| `n` | project name from `.ai-badger/config.json`, read once per process |
| `s` | session id, when the host supplies one |

The single-letter keys are a budget, not cosmetics: a record must stay under `PIPE_BUF`
(4096 bytes) for concurrent appends to be atomic, and the fixed keys repeat on every line.
Fields a caller adds usually keep their full names — they are the payload, and they do not
repeat. The MCP-retrieval fields below are the exception: that event can fire on every turn, so
they are compacted the same way the fixed keys are.

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

| Event | Means |
|---|---|
| `hit` | At least one candidate cleared the match threshold — something was recommended. |
| `gate` | Candidates were scored and **all** fell below the threshold. A correct, frequent outcome, not a failure — but previously indistinguishable from `absent`. |
| `no_terms` | The tokenizer read nothing scoreable from the query, so **no candidate was ever scored against the threshold**. `_score_all_tools` short-circuits to `[]` on the same empty-tokenize condition that fires this event, so `o` (top candidates) is always empty and `h` (threshold) is absent — there is no "suppressed top scorer" to look at, only the index's tool count (`d`), which is unaffected. Distinct from `gate`, where scoring did happen and lost. |
| `absent` | No `.ai-badger/mcp-tools.json` **and** no `.ai-badger/mcp-tools.yaml` — there is nothing to migrate, nothing to search. |
| `legacy` | A `.ai-badger/mcp-tools.yaml` exists but hasn't been migrated to `.json` yet (issue #145) — the JSON-only hook reader can't read it, but it is not the same absence as `absent`: run `mcp-index migrate` (or any write command) to fix it. |
| `known` / `unknown` | A tool call was checked against the index after the fact — was it a tool the index knows about? `unknown` means the tool's server *is* indexed (including with `status: empty`/`unknown`, which is still present) and this tool is not in it: run `mcp-index update`. |
| `server_unindexed` | The called tool's server is named by no source in the index at all (issue #170) — added after the index was built, or never indexed. The strongest "the index is stale" signal, and until 0.51.1 the one that emitted nothing. Remedy is indexing the server, not updating a source. Only server-qualified tool names (`server:tool`) are checked; a built-in like `write_file` is not an MCP tool and is not recorded. |

A "no match" that reads identically to "no index" is a bug that hides itself; that is why these
are separate events rather than one silent no-op. `legacy` exists for the same reason: without
it, "how many projects are stuck on the legacy format" collapses into "how many have no index
at all", and the migration this event exists to help track becomes unmeasurable. `server_unindexed`
is the same rule applied to a silence that had no name: the check simply fell off the end of its
loop, so "the index has never heard of this server" was indistinguishable from "the hook never ran".

| Key | Meaning |
|---|---|
| `q` | the query — the user's message that drove retrieval |
| `g` | terms/tags extracted from the query, comma-joined |
| `d` | how many tools in the index were considered |
| `o` | the top 3 scored candidates as `name:score`, comma-joined (empty on `no_terms`: nothing was ever scored) |
| `r` | what was actually returned (empty on `gate`; absent on `absent` and `legacy`) |
| `h` | the match threshold in force, so a later threshold change is attributable |
| `l` | the tool name, for the `known`/`unknown`/`server_unindexed` check |

### The query field, and redacting it

`q` is the one field carrying user content — indispensable for diagnosing a miss or turning a
record into an eval fixture, and the one thing someone may not want recorded. It is recorded by
default. Setting `AI_BADGER_DEBUG_REDACT` in the environment drops that field only from every
record written afterward, leaving every other field intact so the log stays useful for counting.
The drop happens inside `debug_log.log_event` itself, at the point of writing — a redacted
record never contains the text, so there is nothing to scrub after the fact.

## Where things live

| Path | Purpose |
|---|---|
| `~/.ai-badger/debug/state.json` | Whether logging is on, its scope and expiry |
| `~/.ai-badger/debug/audit.jsonl` | The records, one JSON object per line |

Both are user-level and `0600` — the log says where you work and what ran, so it never lands in
a project directory or in git. It is capped at 5000 records, oldest trimmed first.

## Reading the log

`tail` is for a quick look. For anything more, the file is one JSON object per line:

```bash
jq -r 'select(.event=="drift")' ~/.ai-badger/debug/audit.jsonl
jq -r '[.component, .version] | @tsv' ~/.ai-badger/debug/audit.jsonl | sort | uniq -c
```

The second is the useful one when several copies of ai-badger are installed: it shows which
version each component actually ran at.

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

### What the findings mean

| `kind` | Severity | Means |
|---|---|---|
| `never_observed` | high | Registered **and** instrumented, but produced no record while the log holds records from elsewhere. It may never load, or never fire. This is the failure the tool exists to catch. |
| `not_instrumented` | low | Registered but calls no debug logger, so it *cannot* produce records. Its silence says nothing about health — do not report it as broken. |
| `version_skew` | high | Two versions' observed time ranges **overlap**: two copies were live at once — typically a plugin cache against a `.ai-badger/` scaffold. The finding names each version with the range it was seen in. |
| `always_skipped` | medium | Fired every time and exited early every time. Live, but doing nothing. |
| `unexpected_component` | low | Produced records but is not registered by this project. Often legitimate (a plugin-side hook); worth a glance. |
| `version_unresolvable` | low | Records carry the `unknown` sentinel: the copy that ran has no VERSION and no manifest above it, so it predates 0.35.4 and needs re-scaffolding. |
| `version_progression` | info | Ran at several versions whose ranges are **disjoint** — an upgrade in sequence. Context, not a fault. |

`health` is `ok`, `warn`, `degraded`, or **`unknown`**. Treat `unknown` as *nobody looked* — it
means there is no evidence, not that everything is fine. Say so plainly in the report rather
than implying health. An `info` finding never moves the verdict: during a release train every
component legitimately runs at several versions in turn, and a severity that fires on every
ordinary upgrade teaches the reader to skip the one instance that is real.

**Evidence is not the same as lines in the log.** This tool records its own `enabled`,
`disabled` and `cleared` events; those prove the log exists and nothing more. They are excluded
from the record count, from `observed`, and from the health verdict. With no evidence,
`never_observed` is withheld too — when nothing at all was observed, every component is
trivially silent, and reporting that as a high-severity failure would be crying wolf.

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

## Turn it off when you are done

The window expires on wall-clock time, checked on every event — no timer and no cron. Debug
logging that never switches itself off is a slow disk leak and a standing privacy exposure,
which is why `on` always takes an expiry and caps it at 24 hours.
