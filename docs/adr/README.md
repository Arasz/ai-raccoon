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
| [0008 — Live PID discovery for monitoring](0008-live-pid-discovery-for-monitoring.md) | `GET /observability` on the serve HTTP port returns the live server's PID (and OTLP status) so the CLI can fill in the README's `<server-pid>` placeholder, correct under attach, with no PID file |
| [0009 — OTLP export](0009-otlp-export.md) | OpenTelemetry SDK adopted, opt-in on `OTEL_EXPORTER_OTLP_ENDPOINT`, exporting the existing `AiRaccoon.MemoryTools`/`AiRaccoon.PromotionQueue` instruments plus the built-in `System.Runtime` metrics over OTLP; `project_id` exported in plaintext (ADR 0002's hashing item retired, not implemented); supersedes ADR 0002's OTLP non-goal on owner instruction |
| [0010 — Bank maintenance](0010-bank-maintenance.md) | WAL checkpoint at every process boundary + on a timer, VACUUM/ANALYZE on a longer cadence, both settings-table configurable |
| [0011 — Schema versioning](0011-schema-versioning.md) | Records the gap (no `PRAGMA user_version`, per-feature existence probing instead) and the chosen direction (a `user_version`-keyed migration ladder); implementation is a separate work item |
| [0012 — SSH-key derivation → HKDF](0012-ssh-key-derivation-hkdf-replacement.md) | Replaces the hand-rolled `SHA-256(label ‖ seed)` bank-key composition with platform `HKDF-SHA-256`; existing Bitwarden/SSH-keyed banks are rekeyed via the `ai-raccoon encryption migrate` verb (shipped in #99) |
| [0013 — Extension host hook surface](0013-extension-host-hook-surface.md) | Drops the never-dispatched `OnSweepAsync`/`OnConsolidateAsync` hooks from `IMemoryExtension`; sweep and consolidation stay observable through `OnDeleteAsync`, which `SweepService`/`WorkspaceService` already trigger |
| [0014 — Settings never cross the sync boundary](0014-settings-never-sync.md) | Settings (cloud credentials, embedding endpoint/key) are per-machine; push strips them from every pushed snapshot and pull never reads `remote.settings`, gated by tests on both directions |
| [0015 — Retrieval gates assert portable bands](0015-retrieval-gates-assert-portable-bands.md) | Golden-file comparison moves from positional/1e-6-exact to set-based hashes within a 5e-3 ranking tolerance plus k-boundary absorption; rank pins become ceilings; A6 band widens to the measured cross-platform envelope; the osx-arm64 skip is removed so gates run on every platform |
| [0016 — Remove the extension host](0016-remove-the-extension-host.md) | Deletes `IMemoryExtension`/`MemoryExtensionHost`/`RetrievalRatingExtension` and `OnSourceChangedAsync` entirely — the sole registered extension never overrode the one hook still dispatched in production, so the pipeline was reachable but inert; supersedes ADR-0013 in full and reverses spec-issue-1 §6.2 |
| [0017 — TensorPrimitives in AiRaccoon.Core](0017-tensorprimitives-in-core.md) | Adds `System.Numerics.Tensors` as `AiRaccoon.Core`'s second third-party dependency and vectorizes `EmbeddingMath.MeanPoolAndNormalize` with `TensorPrimitives`; ships only if WP-4's benchmark shows a clear win, else reverted and withdrawn |
