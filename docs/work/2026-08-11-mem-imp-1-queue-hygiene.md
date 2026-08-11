# Queue hygiene — persistent discards + shared-value exclusion (2026-08-11)

Task: `mem-imp-1`. Implements recommendation 2 of
`docs/work/2026-08-11-ai-raccoon-diagnostic.md`: the propose tier must stop re-queueing
content that is already in the shared tier or that an agent explicitly discarded.
ADR: [0026](docs/adr/0026-persistent-discards-and-shared-exclusion.md).
Plan (reviewed rev 2): `docs/plans/2026-08-11-mem-imp-1-queue-hygiene-plan.md`. PR #258.

## What shipped (1.6.5)

1. **Both-sides normalization in propose dedup** (`SharedExtractionService.RankAll`): the
   shared-value set is whitespace-stripped exactly like `PromoteAsync` already did. Root cause
   of the measured 38/1000 queue rows duplicating shared values (19–38 of the top-50,
   depending on the queue's churn at audit time).
2. **Persistent discards**: new `promotion_discards (project_id, hash, discarded_at)` table
   (additive DDL, no schema-version bump), written ONLY by the `memory_promotion_discard` tool
   path. Promote claims, capacity evictions and scorer-version clears never write it — the
   claim-path trap (promote shares the store's DELETE…RETURNING) was verified in review and
   pinned by tests.
3. **Persistence-layer refusal**: the queue upsert refuses discarded hashes and exact
   shared-value twins; refused rows are not counted as genuinely new. A discarded row can
   therefore never re-enter the queue — the prune alone could not guarantee that, because
   `RankAll` re-ranks it every pass (review finding F11, deepened).
4. **Residue sweep**: `PruneRejectedAsync` runs at the top of every propose AND promote pass,
   deleting pre-fix residue (already-shared values, discarded hashes).
5. **Restore-path guard**: `RestoreQueueRowsStillBacked` (watch replace round trip) excludes
   discarded hashes.

## Gates (all witnessed)

- **TDD RED→GREEN**: G1/G2 (2 failing tests → 1-line fix, commit pair), G3/G4/G7 (3 failing
  tests against port stubs → real implementation, commit pair); G5/G6/G8/G9 shipped as pins +
  integration in the GREEN commit (they are vacuously green under stubs by construction).
- **Contract update**: `Promote_SkipsAlreadySharedValues_AndDrainsThemToo` reworked to a
  whitespace twin — with the upsert refusal, an EXACT twin never reaches the queue; the
  promote-level normalized skip accounting survives for twins, which is the layered contract.
- **Targeted suites**: extraction/promotion/storage cluster 263/263; Memory+Mcp+Integration+BDD
  659 pass / 6 fixture-gated skips; full suite <result below>.
- **Live gate (G11) on a faithful copy of the real bank** (WAL-safe backup of
  `~/.ai-raccoon/memory.db`, scratch serve of the 1.6.5 build on :7799):
  - BEFORE: queue 1000, **38 already-shared rows, all 38 inside the top-50**.
  - After one propose pass per project: **already-shared = 0 (bank-wide AND top-50)**; the
    prune swept the residue and the refusal kept it out.
  - Serve restart → propose cycle again: queue stays clean (0 already-shared) — the cleaned
    state and the discards table persist.
  - `memory_promotion_discard` of a live candidate → re-propose → **hash not re-queued**;
    `promotion_discards` holds the row across the restart.
  - Server identity confirmed 1.6.5.0.

## Observations (no defects)

- The propose tool's ADVISORY candidate list still shows a discarded row after re-propose
  (RankAll has no discard input by design); the persisted queue — the surface
  `memory_promotion_list` audits — never receives it. Documented in ADR-0026 as an accepted
  consequence.
- Capacity evictions fired during the live propose passes (the queue sits above the 200
  reserved per project); evictions are not recorded as discards (pinned).
