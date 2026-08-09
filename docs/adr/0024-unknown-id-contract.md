# 0024 — Unknown ids: idempotent removal reports a count, a state transition refuses

Date: 2026-08-09

Status: Accepted.

## Context

Two inconsistent-looking contracts exist for "you gave me an id that does not resolve":

`memory_delete`, `memory_delete_context`, `memory_promotion_discard` and `memory_watch_remove`
return a `0`/no-op count for an unknown hash, context or watch path. `memory_share` and the
workspace family (`memory_write` with `workspaceId`, `memory_workspace_status`,
`_consolidate`, `_discard`) throw a typed refusal (`UnknownHashException`, `unknown-workspace:`)
for the same shape of input.

Three separate sweeps rediscovered this asymmetry and each time filed it as an open product
question, without changing anything, because nothing had ever written down whether it was a
defect or two different verbs behaving correctly. That repeated rediscovery — not the asymmetry
itself — is the actual defect this ADR closes.

## Decision

**A removal verb is idempotent and reports a count; a state transition refuses an id it cannot
act on.** No behaviour changes; this ADR records the rule both existing contracts already satisfy.

- `memory_delete`, `memory_delete_context`, `memory_promotion_discard` and `memory_watch_remove`
  ask for a thing to be gone. If it is already gone, the request is already satisfied — returning
  `0` (or a silent no-op for `memory_watch_remove`) is the correct answer, not a failure to
  report. Repeating the call is always safe.
- `memory_share` and the workspace family ask to *transition into* a state keyed by that id
  (promote this hash into the shared tier; operate inside this workspace). There is no version of
  "already satisfied" for a transition targeting an id that was never live — the caller's premise
  is wrong, and a typed exception (`UnknownHashException`, `unknown-workspace:`) says so instead
  of manufacturing a fake zero-effort success.

Both sides were already correct under this rule before this ADR; the existing docs
(`docs/reference/agent-memory-server.md`), spec (`docs/work/features-agent-memory/spec-issue-1.md`)
and tests (`SqliteMemoryStoreTests.Delete_ForAnotherProject_DoesNotRemoveTheRow`,
`SqliteMemoryStoreTests.ShareAsync_WithUnknownHash_ThrowsUnknownHashException`) all assert it. This
is the smallest change that closes the question permanently: state the rule, put it on the four
silent-zero tools' `[Description]` so an agent reads it instead of discovering it empirically, and
add the two contract tests that were missing (`DeleteContext` with an unknown context,
`PromotionQueueService.DiscardAsync` with an unknown hash).

## Consequences

- **Positive.** The asymmetry has a name and a citable decision. A fourth sweep finds this ADR
  instead of re-opening the question.
- **Positive.** All four silent-zero tools declare the contract in their MCP description, so an
  agent does not have to call `memory_delete` on a made-up hash to learn what happens.
- **Neutral.** No code path changes; only descriptions and test coverage.

## Alternatives considered

### Make every unknown id throw

Rejected. `memory_delete`/`memory_delete_context`/`memory_promotion_discard`/`memory_watch_remove`
are idempotent by nature — an agent that deletes the same hash twice (its own retry, or two agents
racing) should not get an error on the second call for asking for something already true.

### Make every unknown id return a count

Rejected for `memory_share` and the workspace family — there is no partial-success count for "I
promoted 0 things" when the caller asked to promote one specific, named hash that never existed;
silently returning `{shared: false}`-shaped data would hide a broken caller-side reference (e.g. a
typo'd hash, or a workspace the caller thinks is still open) behind a result that looks like
success.
