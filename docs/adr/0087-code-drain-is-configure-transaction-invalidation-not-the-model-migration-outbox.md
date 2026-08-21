# 0087. Code re-embed drains through `code-reindex`, not the `model_migration` outbox

Date: 2026-08-21

Status: Accepted

Plan: `docs/work/2026-08-21-code-search-implementation-plan.md` (rev 3, §3.3, §3.8, §7 join
disposition 2).

## Context

Activating or changing the code embedding engine (`model set code local`) needs the same
property ADR-0076 already built for the memory bank: the settings change and the re-embed it
owes must never be observable apart from each other — no instant where the engine changed but
nothing records the debt, and none where a debt is owed but the engine did not change. Three
designs competed for how the code corpus gets this property, and the plan's own history records
one of them (H4, a `v11b` ladder step) as tried and deleted before this ADR was written.

**Reusing the `model_migration` outbox wholesale was rejected for three concrete reasons, not
a preference:**

1. **The outbox is single-row** (`MemorySchema.cs:372-382`) — one open migration at a time,
   full stop. A code engine change and a memory engine change could not be in flight
   independently without adding a corpus column and reasoning about two half-open migrations
   sharing one row, which is exactly the "shared machinery, new hazard" trap ADR-0076 avoided
   for the memory corpus in the first place.
2. **`ModelMigrationJob`'s relay hard-codes the memory query** — `DrainMigrationAsync` reads and
   re-embeds `entries`, not a corpus-parameterized query. Making it corpus-aware means changing
   code proven correct by ADR-0076's own kill-mid-drain test, for a code path that has entirely
   different lifecycle rules (no sweep, no TTL, no promotion — ADR-0085).
3. **`ToolGate` closes ALL tools while `model_migration` has an open row** (ADR-0076, "the lock
   and the completion guarantee are the same mechanism"). A code corpus re-embed blocking every
   memory tool call — `memory_search`, `memory_write`, everything — for the sake of a corpus
   that is an explicit re-derivable cache with no degradation story, is a cost the design has no
   reason to pay. Memory tools must never be blocked by a code engine change.

A second design (a corpus column added to the existing outbox, `v11b`) was drafted and
abandoned once (1) and (3) above were traced through it: the column fixes the "single row"
problem but not the hard-coded relay or the all-tools gate, so it bought half a fix at the cost
of touching machinery ADR-0076 already closed out.

## Decision

**`model set code local` writes the settings rows and invalidates pending code work in ONE
transaction; a new `code-reindex` maintenance job drains it independently, with no outbox and
no `ToolGate` interaction:**

```
BEGIN
  UPDATE settings       SET embedding.codeModel = X, embedding.codeEngine = fingerprint(X)
  UPDATE code_entries    SET embed_state = 'pending' WHERE embed_state = 'embedded'
COMMIT
-- the vec_code_pending trigger empties vec_code for those rows at commit — no
-- stale-vector window, the same property the outbox transaction buys for memory
-- the endpoint returns here; no re-embed runs inline
```

- **No `model_migration` row is written for a code engine change.** The debt is recorded the
  same way `PendingEmbedJob` already tracks ordinary pending-embed work for memory rows:
  `embed_state = 'pending'` IS the durable record, in the same table the row already lives in.
  There is no separate "started" marker to reconcile, because the invalidation and the settings
  change are the same transaction — the property ADR-0076's outbox exists to buy is already
  true here without inventing a second mechanism to buy it.
- **`code-reindex`** is a new `IMaintenanceJob` (ADR-0070's "maintenance is a list of jobs with
  a ledger" — reused, not re-invented) that drains `code_entries` rows with `embed_state =
  'pending'` on the existing maintenance poll cadence, the same on-demand-versus-cadence split
  ADR-0076 built for `ModelMigrationJob`.
- **No `ToolGate` interaction.** Memory tools are never blocked by a code engine change, in
  either direction: a code re-embed in progress never refuses `memory_search`/`memory_write`,
  and a memory model migration never blocks code search.
- **While code vectors drain, `kind=code` search degrades to FTS5-only** — `code_fts` rows
  exist from ingest time regardless of embed state, so the corpus is never empty during a
  drain, only vector-ranking-degraded (ADR-0088 covers the search surface itself).
- Non-768 manifests are refused before this transaction ever runs (configure-time refusal,
  covered by ADR-0088) — this ADR's transaction only ever invalidates rows for an engine
  already known to match `vec_code`'s fixed dimension.

## Consequences

- **Positive**: memory tools have zero exposure to a code engine change — no shared lock, no
  shared gate, no shared relay code to reason about for a corpus with entirely different
  lifecycle rules.
- **Positive**: the durable-debt property (no crash can separate "engine changed" from "re-embed
  owed") falls out of one ordinary transaction, the same way ADR-0076 observed the outbox
  "degenerates to one transaction" when both writes land in the same SQLite file — no new
  pattern was needed here either, just the transaction itself.
- **Negative**: `code-reindex` is new machinery next to `ModelMigrationJob`, not a reuse of it —
  two similar-shaped jobs exist in the maintenance list where a shared abstraction might
  eventually be justified, traded here for keeping ADR-0076's proven code untouched.
- **Not addressed**: drain throughput and batch-size tuning for a large repo's initial re-embed
  are documented as an operational cost (plan §3.3), not solved by this ADR — a batch-size
  lever is the named extension point if it needs to change.

Extends ADR-0070 (maintenance job ledger) and ADR-0085 (code lifecycle exclusions). Deliberately
does NOT extend ADR-0076 — the three reasons above are why this is a sibling design, not a
generalization of it.
