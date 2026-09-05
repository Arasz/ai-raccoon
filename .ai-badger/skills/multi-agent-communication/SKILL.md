---
name: multi-agent-communication
description: >-
  Use when multiple agent sessions share one project and must coordinate without stepping
  on each other — announcing started work, touched files, opened PRs, review requests,
  review feedback, and merges to main over the project message bus. Covers when to
  broadcast, the message shape, and the ack-without-reply rule that keeps the bus
  loop-free.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [coordination, message-bus, parallel-agents, review]
    related_skills: [send-message, task, quick-task, worktree-agent-isolation, status-report]
---

# multi-agent-communication

Sessions sharing a project talk over the project message bus (transport: `send-message`).
Each agent announces what it is doing, the others adjust, and every announcement gets
exactly one ack — which itself gets no reply.

Example trigger: `f: communicate with other agents in the project about your work, prs,
and merge - ask for review`.

## When to broadcast

| Moment | Event | Include |
|---|---|---|
| Starting a task, lane, or quick-task | `starting` | taskId, scope in one line, files/area touched |
| Opening a draft PR | `pr-opened` | PR number, branch, what it holds |
| Ready for review / asking for review | `review-request` | PR number, what to check |
| Giving review feedback | `review-feedback` | verdict, file:line findings |
| Merging to main | `merged` | PR number, what landed — others rebase |
| Blocked or handing off | `blocked` | what is needed, from whom |

Broadcast at package boundaries, not per commit — the PR UI already narrates commits.
When no other session is active in the project, skip the bus and just do the work.

## How to send

A project broadcast reaches every session in the project:

```bash
PID=$(cat .ai-badger/project-id)
python3 .ai-badger/skills/send-message/scripts/send_message.py \
  --project-id "$PID" --content "[<taskId>] <event>: <detail>"
```

Sender identity derives from the working directory, so run it from the project checkout.
A refused send names its reason — read `send-message` when a send refuses.

Under pi with the message-bus extension installed, use the tool instead of the
subprocess — same rows, same shapes: a project broadcast is a `message-bus`
`send` with the project id (`/messages send [<taskId>] <event>: <detail>` from
the project checkout does the same), a 1:1 is a `send` with `sessionId`, and an
ack is a `message-bus` `ack` with the message id (the tool enforces inbox
membership and refuses an ack-of-an-ack). `/messages` lists the inbox,
`/messages check` delivers now. Without the extension, the script above stays
the transport.

## Message shape

One announcement, one line when possible:

```text
[aib-widget-gap] starting: rewrite checkout totals in src/shop/totals.py (branch task/aib-widget-gap)
[aib-widget-gap] pr-opened: #412 draft, totals rewrite ready for eyes
[aib-widget-gap] review-request: #412 — check rounding edge cases in totals.py
[aib-widget-gap] merged: #412 to main — rebase your lanes
```

## Ack discipline — no infinite loop

- **Ack every non-ack message exactly once**, as a project broadcast: `ack: [<taskId>] <event>`.
- **Never reply to an ack.** A message starting with `ack:` is terminal — answering it
  restarts the conversation forever, with a full LLM turn burned per cycle per session.
- Acks confirm receipt, not agreement. Disagreement is a `review-feedback` message, which
  itself gets acked once.

## Receiving

Mail arrives through each harness's delivery seam (pi wakes idle sessions on addressed
mail, Claude injects at turn end, Hermes before every LLM call) — act on it at the next
natural boundary: adjust touched files, rebase after a `merged`, review after a
`review-request`. `status-report` answers humans about local progress; the bus informs
other agents.

## Gotchas

- **Delivery is not instant everywhere.** An idle Claude session fires no hook until its
  next turn, and pi machine-broadcasts do not wake (only addressed mail does unless
  `AI_BADGER_PI_BUS_WAKE=all`) — never block waiting for an ack; announce and continue.
- **A `--project-id` no machine resolves refuses the send.** Copy it from
  `.ai-badger/project-id` in the project checkout rather than typing it by hand.
- **One announcement per milestone.** Repeating `starting` or narrating every push trains
  other sessions to ignore the bus.

## Verification Checklist

- [ ] Every started unit of work announced before the first edit, naming files/area
- [ ] PR opened, review requested, and merge each announced exactly once
- [ ] Every non-ack message acked exactly once; no reply to any ack
- [ ] After a peer's `merged`, the lane rebased before its own merge
