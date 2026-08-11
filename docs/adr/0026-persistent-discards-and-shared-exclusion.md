# 0026 — Persistent discards and shared-value exclusion in the propose tier

Date: 2026-08-11

Status: Accepted.

## Context

The propose tier (`promotion_queue`, ADR-0007) re-queued content that should never be there.
The 2026-08-11 diagnostic (`docs/work/2026-08-11-ai-raccoon-diagnostic.md`) measured, on the
live bank:

- **38 of 1,000 queue rows carried a value already present in the shared tier — 19 of the
  top-50 by score.** Root cause: `SharedExtractionService.IsDuplicate` normalized the
  candidate's whitespace but compared it against the RAW shared index values
  (`GetSharedIndexAsync` returns `value` as stored), so a multi-word value never matched. The
  promote path (`PromotionQueueService.PromoteAsync`) had normalized both sides since 1.6.3;
  propose was the half-normalized outlier. The 08-10 curation pass ("score is a decent first
  filter… wrong in ~30 % of cases") was being silently undone as extraction re-queued the very
  rows it had promoted or rejected.
- **Discards were not persisted.** `memory_promotion_discard` deleted the queue row and forgot
  it; the next propose pass re-queued it. The 08-10 pass discarded 27 candidates; re-enabling
  extraction re-queued them.

## Decision 1 — the shared-value dedup normalizes both sides

`RankAll` now builds the shared-value set whitespace-stripped
(`sharedValues.Select(NormalizeWhitespace)`), exactly mirroring `PromoteAsync`. An exact twin
or a whitespace twin of a shared value is never proposed.

## Decision 2 — the discard is permanent data

New table, additive DDL in the unconditional `MemorySchema.Ddl` (no schema-version bump —
same precedent as `watches`/`watch_files` and the ADR-0023 trigger):

```sql
CREATE TABLE IF NOT EXISTS promotion_discards (
    project_id   TEXT NOT NULL,
    hash         TEXT NOT NULL,
    discarded_at INTEGER NOT NULL,
    PRIMARY KEY (project_id, hash)
);
```

Semantics:

- A discard is the agent's "no" for that content identity (`hash` = ContentHash.Of(path,
  content)). It is **permanent** — there is no un-discard tool in v1. A changed content
  produces a new hash and is re-eligible; re-proposing identical content is refused.
- **Never synced** — like queue rows, discards are per-machine curation intent, not data.
- **Never swept** — the reaper (ADR-0025) degrades `entries` only; `promotion_discards` is
  outside its reach by design.
- **Only the tool path writes it.** `PromotionQueueService.DiscardAsync` (the
  `memory_promotion_discard` path) remembers every removed hash. The promote claim path shares
  the store's `DiscardAsync` (DELETE…RETURNING) and must never be recorded as a rejection;
  capacity eviction and scorer-version clears are not rejections either. Whole-queue clear
  (`hash` omitted) remembers every removed row — that is the "this queue is junk" semantic.

## Decision 3 — the persistence layer refuses, the pass start sweeps

Two complementary mechanisms, both in `SqlitePromotionQueueStore`:

1. **Upsert refusal.** Each candidate insert is
   `INSERT … SELECT … WHERE NOT EXISTS (discarded hash) AND NOT EXISTS (shared exact-value
   twin)`. `RankAll` cannot see discards (its signature stays unchanged, and reading them
   would ripple `IMemoryStore`'s fakes), so the queue's single write chokepoint enforces the
   contract: the queue never holds rejected content or shared content. Refused rows are not
   counted as genuinely new (`UpsertAsync`'s honest-count contract, 1.6.3). Exact-value match
   only: a whitespace twin still queues at propose and is skipped by promote's normalized twin
   check — the layered contract, pinned by
   `PromotionQueueServiceTests.Promote_SkipsAlreadySharedValues_AndDrainsThemToo`.
2. **Prune at pass start.** `PruneRejectedAsync` deletes queued rows that are already shared
   (exact value twin) or discarded — the pre-fix residue (38 shared twins + re-queued discards
   on the live bank). It runs at the top of `ProposeAsync` (single chokepoint: the 30-minute
   loop and `memory_share_extract propose` both route through it) and at the top of
   `PromoteAsync` (a pre-fix discarded row must not be promotable the first time the mode
   flips).

Consequence, accepted: `memory_share_extract propose`'s advisory candidate list may still show
a discarded row (it is re-ranked every pass), but the persisted queue — the review surface
`memory_promotion_list` audits — never receives it.

## Decision 4 — the watch-replace round trip is guarded too

`RestoreQueueRowsStillBacked` re-inserts captured queue rows across a replace; it now excludes
discarded hashes, so a rejected candidate cannot reappear through the ingest path either.

## Costs and trade-offs

- Each candidate insert pays two `EXISTS` checks: a discard-table PK probe and a scan of the
  shared tier's values (~103 rows today). No index on `entries(scope, value)` was added — an
  index over the large `value` column would bloat the bank more than the per-pass scan costs.
- `promotion_discards` grows without bound in v1 (each discard is ~100 bytes; the 08-10 pass
  discarded 27). No cap, no expiry, no un-discard — noted as future work if curation volume
  ever makes it matter.

## Gates

- TDD RED→GREEN, witnessed per commit: G1/G2 (both-sides normalization), G3/G4/G7
  (persistence + prune + honest count), G5/G6 (promote/eviction never write discards — pins),
  G8 (tool-equivalent discard→propose round trip), G9 (replace round trip does not restore a
  discard). All in `SharedExtractionServiceTests`, `PromotionQueueDiscardTests`,
  `PromotionQueueServiceTests`.
- Live gate (2026-08-11, manual): propose passes on the real bank, top-50 audit shows ~0
  already-shared and ~0 re-discarded; a live discard is not re-queued.
