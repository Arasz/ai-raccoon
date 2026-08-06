# adr/

Architecture decision records: immutable, frozen. Each file records one decision in date
order (`NNNN-slug.md`); records are never edited after acceptance — a new decision gets a
new number. Add an ADR for any architecture-level decision via `create-task-spec` /
`owner-gate-review` workflow.

## Contents

| ADR | Decision |
|-----|----------|
| [0001 — FluentValidation in the core](0001-fluentvalidation-in-core.md) | Domain validation via FluentValidation |
| [0002 — OpenTelemetry observability](0002-opentelemetry-observability.md) | BCL-only Meter/ActivitySource on all MCP tools |
| [0003 — Source file as first-class citizen](0003-source-file-first-class-citizen.md) | `source_file` column + weighted FTS source index + source identity on results |
| [0004 — Dual-vector structure signal](0004-dual-vector-structure-signal.md) | heading-path storage + structure embeddings fused at fixed α for section-targeted retrieval |
| [0005 — Source-affinity ranking](0005-source-affinity-ranking.md) | adjacent-chunk boost (λ) + source consolidation + document-first tie-break over the fused list |
| [0006 — RRF parameter optimization](0006-rrf-parameter-optimization.md) | 96-point sweep re-confirms k=60, 1:1, minScore 0.0, window max(3×,100) as the grid optimum; window becomes a `SearchQuery` parameter |
| [0007 — Propose tier](0007-propose-tier.md) | Persisted per-project waiting-for-promotion queue (`promotion_queue`) with fair-share capacity (cap ÷ project count reservations, borrowing, eviction of the biggest occupier's weakest row); propose persists, promote consumes from the queue; every tool response enveloped as `ApiEnvelope(data, meta, result)` with waiting-promotion meta; metrics on a dedicated Meter | 
