---
name: send-message
description: >-
  Use when an agent session needs to reach another agent session, every session in a
  project, or every session on this machine without the human relaying between windows —
  1:1, project-broadcast and machine-broadcast sends through the machine-wide user-DB
  message bus.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [message-bus, coordination, agent-messaging]
    related_skills: [task, status-report, multi-agent-communication]
---

# send-message

Send one message through the user-DB message bus. The row lands in the machine-wide
user store; each receiving session's delivery hook injects the messages addressed to
it on its next delivery event.

## Usage

```bash
python3 .ai-badger/skills/send-message/scripts/send_message.py \
  --content "found it, see src/bus.py" \
  --session-id <their-session-id>
```

`--content` is always required and is delivered verbatim. The target flags decide the
shape of the send:

| Target | Flag | Shape |
| --- | --- | --- |
| one session | `--session-id <id>` | 1:1 |
| one project | `--project-id <id>` | project broadcast |
| none given | omit both | machine broadcast |

Give both `--session-id` and `--project-id` and **the session wins**: the row is
stored 1:1 and the project half is dropped — precedence is normalised at write, so no
reader can ever see a dual-target row.

On success the script prints `sent <row id>`. A refused send prints
`send refused: <reason>` on stderr and exits non-zero with nothing written.

## Sender identity is mandatory

Both halves of the sender identity are REQUIRED on every send. When either half cannot
be resolved the send is refused (non-zero exit, no row) — this is the bus's Rule 10,
not an implementation quirk:

- **sessionId** — `--sender-session <id>`, or derived in this order: the harness's
  session env var (`CLAUDE_CODE_SESSION_ID` for claude, `PI_SESSION_ID` for pi,
  `HERMES_SESSION_ID` for hermes — first set wins; each harness exports its live
  session id to the subprocesses it spawns, and that is the id its delivery consumes
  by, so own broadcasts are excluded), then a pid-ancestry match against the
  sessions store, then a unique cwd match (exactly one known session carrying this
  identity). Residual (api-review d-209): a stale foreign var inherited from an outer
  host (claude-in-shell-in-pi) beats the live one by list order — if your shell
  carries a `CLAUDE_CODE_SESSION_ID` that is not this session's, pass
  `--sender-session` explicitly.
  working directory). Ambiguous or absent → refused.
- **projectId** — `--sender-project <id>`, or `AI_BADGER_PROJECT_ID`, or the cwd
  resolver's upward walk to the nearest `.ai-badger/project-id` (minted at scaffold
  time, backfilled by den-refresh). A cwd with no `.ai-badger` in its ancestry — or
  one whose id file is absent — resolves no project: the send needs the explicit
  flag. This refuses rather than guessing.

The explicit flags exist for contexts with no derivable identity: a human running the
script by hand, a cron job, or a test.

Sender identity is asserted, not authenticated: it is derived from machine-local state
(environment variables, the sessions store, the working directory), so anything with
access to this machine can send as any session or project. Treat a sender as a claim
the local software made, not proof of authorship — the trust boundary is the machine,
not the bus.

## Targets are validated

A `--project-id` target is checked before anything is written: it must resolve on this
machine — the sender's own project resolution, the `AI_BADGER_PROJECT_ID` override, or
the stripped content of some `.ai-badger/project-id` file the machine scan finds — or
the send refuses with exit 1 and no row:

```
send refused: --project-id '<id>' does not resolve to any project on this machine — no .ai-badger/project-id carries it (ADR-0025); use a minted id or omit --project-id for a machine broadcast
```

When the scan finds ids but none matches, the refusal lists them. Named residual: the
scan is a bounded approximation — it walks at most four directory levels under the
store's home, skipping `Library`, `node_modules`, `.git`, `.cache` and similar noise
trees, and never follows directory symlinks — so a scaffolded project outside that
budget (deeper tree, another volume) is invisible and its id false-refuses. The escape
hatch is the minted-id contract above, not a bypass flag. Dual-flag sends
(`--session-id` + `--project-id`) skip validation: the session wins and the project
half is dropped at write, so there is nothing stored to validate.

## Coordinating with other agents

This script is the transport; the protocol lives in `multi-agent-communication` — read it
when two or more sessions share the project (what to announce, the message shape, and the
ack-without-reply rule).

## pi native transport: the message-bus extension

Under pi, do not shell out to the script above: use the native `message-bus`
tool. It speaks to the same user-DB bus (same rows, same session-wins
precedence, same refusal shapes as error results), with sender identity derived
automatically — the session id from the session manager, the project id from
`AI_BADGER_PROJECT_ID` or the project-id walk — so there are no `--sender-*`
flags to pass and no identity derivation to get wrong:

| Need | Tool call | Human equivalent |
| --- | --- | --- |
| 1:1 send | `message-bus` action `send`, `content` + `sessionId` | `/messages send-to <session-id> <text>` |
| project broadcast | `message-bus` action `send`, `content` + `projectId` | `/messages send <text>` (from the project checkout) |
| machine broadcast | `message-bus` action `send`, `content` only | — |
| read the inbox | action `list` (grouped, no cursor advance) | `/messages` |
| deliver now | action `check` (posts a card when there is mail) | `/messages check` |
| ack once | action `ack`, `id` (inbox membership + never-ack-an-ack enforced) | `/messages ack <id>` |

The extension ships with pi-badger-integration (`bun run publish` installs it
to `~/.pi/agent/extensions/message-bus/`). No `message-bus` tool in your
context means the extension is not installed — fall back to the script above,
which stays the transport for every other harness (plus cron jobs, humans
running sends by hand, and tests). `PI_BADGER_MESSAGE_BUS=0` disables the
delivery hooks; the tool stays. A broken bus is fail-open either way: an
error result, never a broken session.

## Gotchas

- No environment-specific gotchas known.

## pi push delivery (0.159.0, ADR-0026)

pi sessions receive mail the moment it is sent: the pi adapter polls the bus
store on a session-scoped timer and wakes idle sessions for their addressed
mail. Two environment variables tune it, read once per session at arm time:

- `AI_BADGER_PI_BUS_WAKE` — `off` | `addressed` (default) | `all`. `addressed`
  wakes an idle session on 1:1 and project mail; machine broadcasts are
  injected without waking (visible immediately, entering LLM context at the
  next turn). `all` wakes on broadcasts too (each wake is a full LLM turn per
  idle session — price it before enabling). `off` never arms the timer;
  delivery falls back to the turn-boundary seams.
- `AI_BADGER_PI_BUS_POLL_SECS` — poll interval, default `2`, floor `0.5`.
  Invalid values degrade to the default with a one-time notice.

The poll timer arms only in interactive (tui) and rpc sessions — print/json
sessions have no idle state to wake and deliver via their existing seams. A
broken bus never breaks a session: every failure path is fail-open
(ADR-0026).

## Claude turn-end delivery (Stop)

Claude sessions re-run delivery when the assistant finishes a turn (`Stop`,
`message-delivery-turn-end` row): mail that arrived mid-work is injected as
`additionalContext`, which continues the turn so Claude acts on it without
another user prompt. Exactly-once keeps the continuation loop-safe — a second
firing finds nothing new and answers `{}`. Hermes needs no equivalent arm
(`pre_llm_call` already delivers before every LLM call); Copilot's `agentStop`
injection semantics are unverified, so its delivery stays on the per-turn seam.

Residual, by host design: a fully idle session fires no hook until its next
prompt — Claude Code hooks cannot wake an idle session (no timer surface;
`FileChanged`/`Notification` discard context output per the Claude Code hooks
reference), so mail arriving while idle still waits for the next turn.
