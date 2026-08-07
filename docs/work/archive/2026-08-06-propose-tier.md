# Propose tier: waiting-for-promotion queue + fair-share capacity + response envelope (1.1.0)

**Date:** 2026-08-06 — the `e:` extension of the memory-first-gate task, designed by a
software-MoE pass (architect + engineer reports, read-only) and implemented TDD.

## What changed

**Propose tier (new).** `memory_share_extract propose` no longer returns ephemeral
candidates — it persists them into the `promotion_queue` table (UNIQUE(project_id, hash);
re-propose refreshes score, keeps the first `created_at`). Queue rows are deliberately
outside `entries`: never searchable, never counted by `memory_stats`, never swept.

**Promote from the queue.** `memory_share_extract promote`/`autoPromote` and the
background extraction loop (promote mode) share the top queued candidates and drain them;
already-shared values are skipped and drained too. The old promote-re-extracts-fresh flow
is gone.

**Fair-share capacity.** `extract.queue-capacity.global` (default 1000) split into
per-project reservations (cap ÷ project count); projects may borrow unused space; when
the queue is over the cap the weakest candidate (lowest score, oldest first) of the
biggest occupier is evicted (`UniformCountEvictionPolicy`). Shared tier untouched —
still curated, sweep-exempt, never swept.

**Review surface.** `memory_promotion_list` / `memory_promotion_discard`; the extraction
loop fills the queue on its schedule. The shared-extraction prompts now describe the
two-step ritual.

**Response envelope (breaking).** Every tool returns `ApiEnvelope<T>(data, meta)`;
`meta` carries `waitingPromotionsCount`, `promotionsWaitTimeSeconds`,
`waitingByProject` — the "something is waiting" signal the owner asked for. An in-band
`OperationStatus` result slot was designed (HTTP codes, `Ok` = (200, "ok")) and removed
before shipping — every call site only ever produced `Ok`, so it was dead schema weight;
domain failures stay on the `McpException` channel until a real consumer exists.

**Architecture parts (all new, injectable):** `IPromotionQueue` /
`PromotionQueueService` (Infrastructure), `IPromotionQueueStore` /
`SqlitePromotionQueueStore` + `PromotionQueueSql` (Infrastructure/Sqlite),
`IEvictionPolicy` / `UniformCountEvictionPolicy` (Core), `PromotionCapacityPolicy`
(Core, static pure), `IPromotionQueueMetrics` / `PromotionQueueMetrics` (server,
Meter "AiRaccoon.PromotionQueue"), `ApiEnvelope`/`ResponseMeta`/`OperationStatus`
(Core). Tool classes split from the 715-line MemoryTools monolith into six domain
files (<400 lines each). ADR-0007 records the decision.

## Verification

- Full suite: **1360 passed, 0 failed**, 4 pre-existing spec skips; build 0 warnings;
  scripts pytest 137 passed.
- New tests: queue store (10), capacity/eviction policy (16), queue service incl.
  eviction loop + sweep regression (9), access gating for the new tools, tool-surface
  inventory (22 tools, assembly-wide scan), E2E wire round-trips for the promotion
  tools and the envelope shape (closed-generic `ApiEnvelope<T>` schema derivation
  verified over the real HTTP server).
- Manual fresh-install gate: `scripts/manual-fresh-install-test.py` with
  `AI_RACCOON_VERSION=1.1.0` (see the finish gate record below).
- Manual propose-tier exercise: see the manual-test record below.

## Manual tests (owner f:)

Fresh-install gate: `AI_RACCOON_VERSION=1.1.0 python3 scripts/manual-fresh-install-test.py`.

Live propose-tier exercise (scratch bank):
1. `ai-raccoon serve` a scratch bank (`--data-root`, `--port`), then via the MCP client:
   write facts, run `memory_share_extract` (propose) → candidates persisted; every
   response carries `meta.waitingPromotionsCount`.
2. `memory_promotion_list` shows the queued rows ranked by score.
3. `memory_share_extract` (mode=promote) shares the top queued rows and drains them;
   `memory_promotion_list` reflects the drain.
4. Eviction: set `extract.queue-capacity.global` small (CLI `config set`), propose from
   two projects, observe the biggest occupier's lowest-scored row evicted
   (`meta`/list count and the eviction log line).
