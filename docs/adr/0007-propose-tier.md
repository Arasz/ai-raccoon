# 0007 — Propose tier: waiting-for-promotion queue with fair-share capacity

Date: 2026-08-06

Status: Accepted

## Context

`memory_share_extract` propose mode returned ranked candidates ephemerally — the agent
could review them once, in that response, and nothing waited for a decision. Sharing was
therefore opportunistic: whatever the agent happened to scan at the moment, promoted;
candidates the agent did not act on vanished. The shared tier itself is curated and
sweep-exempt by design (spec-issue-1 FR-MEM-1.15/1.21) — that stays. The gap was the
review surface between "worth sharing" and "shared".

The owner's direction (2026-08-06): proposals should **wait** for the agent, visibly —
every tool response should say how many promotions are waiting and for how long — and the
waiting surface must be capacity-managed fairly across projects: a total cap split into
per-project reservations, borrowing of unused space, and eviction of the weakest candidate
of the biggest occupier when the queue is over the cap.

## Decision

Introduce a **propose tier**: a persisted per-project queue of ranked candidates, and a
common response envelope that surfaces what is waiting.

- **Persistence.** A new `promotion_queue` table (id, project_id, hash, path, value,
  source_file, score, reasons, created_at, updated_at; `UNIQUE(project_id, hash)`,
  indexes on project and score). Queue rows are not entries: never searchable, never
  counted by `memory_stats`, never swept. `memory_share_extract` (propose) upserts
  candidates into it; re-propose refreshes score/value but keeps the first `created_at`.
- **Promote from the queue.** `memory_share_extract` (promote) and the extraction loop in
  promote mode share the top queued candidates and drain them; already-shared values are
  skipped and drained too (dedup normalizes whitespace on both sides). The old flow —
  promote re-extracting fresh candidates — is gone.
- **Capacity.** Total cap `extract.queue-capacity.global` (default 1000, guarded parse).
  `PromotionCapacityPolicy` splits it into per-project reservations (cap ÷ project
  count); `IEvictionPolicy`/`UniformCountEvictionPolicy` pick the victim project (the
  greatest item count; ordinal-smallest id breaks ties) and the store evicts its lowest
  score, oldest first. A crash between upsert and eviction may leave the queue one over
  cap; the next propose loop self-heals.
- **Review surface.** `memory_promotion_list` shows the queue; `memory_promotion_discard`
  drops rows or a whole project's queue. The background extraction loop (propose mode)
  fills the queue on its schedule.
- **Envelope.** Every tool response is `ApiEnvelope<T>(data, meta, result)`; `meta`
  carries `waitingPromotionsCount`, `promotionsWaitTimeSeconds` and the per-project
  breakdown — the "something is waiting" signal. `result` is an in-band
  `OperationStatus` (HTTP code; `OperationStatus.Ok` = 200/"ok"); protocol errors stay
  `McpException`. Breaking change for clients, hence 1.1.0.
- **Observability.** `IPromotionQueueMetrics` port, implemented as
  `PromotionQueueMetrics` (Meter "AiRaccoon.PromotionQueue"): queued deltas, evictions,
  promoted/discarded counts, wait-time and evicted-score histograms, capacity
  utilization; `PromotionQueueService` logs each lifecycle event
  (`[LoggerMessage]`, EventIds 600+).

## Consequences

- Agents see the queue in every response and can act on it; sharing is a two-step
  ritual (propose → review → promote) with a durable intermediate state.
- Fair-share eviction is deliberately simple: the biggest occupier loses its weakest
  row, even if that is the inserter (owner-confirmed uniform rule). The capacity key is
  the only tuning knob; a stale-queue TTL sweep was deliberately not built (the cap
  bounds growth).
- The shared tier's curation promise is untouched — eviction never touches it.
- Existing banks gain the table idempotently on open (CREATE TABLE IF NOT EXISTS); no
  migration beyond DDL, consistent with the schema's established pattern.
