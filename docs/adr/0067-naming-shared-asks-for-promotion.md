# 0067. Naming `shared` asks for promotion; it does not perform one

Date: 2026-08-15

Status: Accepted

## Context

WP2 / H6. `memory_write(context: "shared")` wrote straight into the shared tier at the default `rw`
access mode: **138 rows across 5 projects on the live bank**, each crossing the project boundary with
no review, and `EntryBucket.For`'s own comment conceded it — *"a direct write to the shared tier is an
open owner decision"*.

The first draft of this package proposed **refusing** the write. The owner's steer replaced it, and it
is better: *an agent naming `shared` is asking for the row to be promoted, and that is the strongest
promotion signal available* — better than any scorer inferring it. Refusing throws that signal away;
writing it through skips the review the shared tier exists to have.

**The factual correction the design rests on.** `shared` is not "a label inside the project". A label
is `scope='custom'` with `context_label` set; `shared` is a distinct **scope**, `context_label` NULL on
every one of those 138 rows, each retaining its originating `project_id`. It is the one context string
that crosses the project boundary — which is exactly why it was reachable at `rw` without review.

## Decision

**A `shared` write becomes a project write plus a promotion request.**

```
memory_write(context: "shared")
  → row lands in the caller's own project scope   (searchable immediately, never lost)
  → one promotion candidate, reason `agent-requested-share`
  → the response says so: Stored = true, Reason = "queued-for-promotion: agent-requested-share"
```

`memory_share` and `memory_share_extract` still reach the shared tier directly. The review path is
unchanged; only the *unreviewed* path is closed.

**The enqueue is a Core service, and that is the whole reason WP2 waited for WP8.**
`PromotionQueueService` already takes `IMemoryStore`, so injecting `IPromotionQueue` into the store
would be **store → queue → store** — a genuine cycle at singleton scope, resolvable only by a lazy
service-locator closure, which is the smell the architecture lane filed as F15. A **third** service
composing both ports has no cycle. `MemoryWriteService` is that service.

**Score.** `AgentRequestedScore = 1.0`, above the scorer's range, so an explicit request outranks every
inference. Eviction is deliberately **untouched**: a request that cannot fit shows up in the queue's own
metrics rather than being silently dropped. That is reversible if it proves wrong; silently dropping an
agent's explicit request would be the exact failure mode this campaign exists to find.

## What was rejected

**The one-line version.** Mapping `shared` to the project scope inside `EntryBucket.For` closes the
boundary crossing with no cycle and no new service. It was rejected because it changes behaviour
**silently**: the agent's promotion request would be dropped with nothing in the response to say so.
Trading one silent behaviour for another to save a wave is not a saving.

## Consequences

- No new `scope='shared'` row can be created by a write. The 138 existing rows are untouched — this
  changes the write path, not history.
- `MemoryTools.Write` delegates to `IMemoryWriteService`; the tool holds no branch of its own.
- An agent-requested candidate is distinguishable in `memory_promotion_list` by its reason, which is
  what the acceptance criteria asked for.
- **Open, and deliberately not decided here:** whether an agent-requested candidate should bypass
  capacity eviction. Left as "score high, evict normally" because that is the reversible half.

## Evidence

`tests/AiRaccoon.Tests/Integration/Memory/SharedWriteIsAPromotionRequestTests.cs`, against a real bank.

Watched red by restoring the pre-fix path (every write straight through):

```
naming `shared` asks for promotion; it does not perform one
  Expected: 0 rows WHERE scope = 'shared'
```

The gate asserts all three halves — no shared row, one project row, one candidate carrying
`agent-requested-share` — plus that an ordinary write queues nothing, so the change cannot be
satisfied by queueing everything.

It uses a **recording** queue rather than the real graph: this gate is about what the *write path*
decides. That a proposed candidate persists is `PromotionQueueService`'s own contract and its own
tests.

`Speed=Fast` 2169 passed. One run failed `ServeRestartTests` and passed clean on the next — that class
is a gate **writer** and remains exposed to the mechanism ADR-0066 contains for readers.
