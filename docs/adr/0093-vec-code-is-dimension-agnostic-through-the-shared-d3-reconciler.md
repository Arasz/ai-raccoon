# 0093. vec_code is dimension-agnostic through the shared D3 reconciler

Date: 2026-08-24

Status: Accepted

Plan: `docs/work/2026-08-24-vec-code-unfix-dim-plan.md` (rev 1, review round 1 folded).

## Context

The code corpus's `vec_code` vec0 table was created at a fixed `float[768]`
(`MemorySchema.cs` Ddl block) and protected by a configure-time refusal: any manifest whose
dimensions were not 768 was rejected by both the CLI pre-flight and the store
(`SqliteCodeEngineStore.ActivateCodeEngineAsync`). The memory bank's `vec_entries` /
`vec_structure` tables have no such gate — a `VecDimensionReconciler` (D3) drops and recreates
them at the active engine's dimension at drain-start and at server open. The fixed-768 gate was
a v1 simplification, not a design constraint: the v1 code-search plan explicitly documented the
D3 machinery as the extension point, "NOT exercised in v1"
(`docs/work/2026-08-21-code-search-implementation-plan.md:137-138`).

The gate excluded every non-768 code embedding model — e.g. Salesforce/SFR-Embedding-Code-400M_R
at 1024 dimensions — from powering code search.

## Decision

1. **`VecDimensionReconciler` is generalized and reused for `vec_code`.** The table list is a
   parameter now (`MemoryVecTables` / `CodeVecTables`), and a transaction-aware overload lets a
   caller pass its own transaction; the reconciler never begins/commits when it does. The
   memory call sites are unchanged. No second reconciler exists — a copy would drift
   (`derive-or-delete-the-list`).
2. **The code engine's dimension is persisted** as `embedding.codeDimensions`, written at
   activation and at fingerprint change, deleted by `settings model code reset`. Missing or
   unparseable at server open defaults to 768 — the dimension every pre-1.35 bank was created
   at, so the default is the actual shape, not a guess (ADR-0083's fallback-to-constants
   precedent).
3. **Activation reconciles `vec_code` inside its own transaction** (settings + reconcile +
   invalidation commit atomically): a crash mid-activation rolls back to the old,
   fully-consistent bank instead of leaving a 1024 engine against a 768 index whose rows would
   hit `MaxEmbedAttempts` and be abandoned. The triggers survive the DROP and bind to the
   recreated table by name, exactly as for the memory tables.
4. **The open path reconciles too** (`NodeRunner` before `StartAsync`, DDL-only, mirroring the
   memory open reconcile) and the fingerprint-change path reconciles + re-records the dimension
   in its invalidation transaction — a manifest swapped in place to another dimension moves the
   index in the same commit that invalidates the rows.
5. **Both 768 refusal gates are removed** (store + CLI pre-flight + help text). The chunk-budget
   gate (window ≥ 510 content tokens) is unchanged and remains the only configure-time refusal —
   it protects a different property (silent truncation) that dimensionality does not touch.
   `CodeCorpusSchema.EmbeddingDimensions = 768` stays as the fresh-bank/legacy default, not as a
   gate. The Ddl block's `float[768]` is unchanged → no schema-digest change, no version-ladder
   step: fresh banks start at 768 and reconcile only when a different-dimension engine activates.

This **amends**: ADR-0084 (the D3 reconcile now also covers the code corpus), ADR-0085 (the
`vec_code float[768]` corpus shape becomes a default, not a fixed contract), ADR-0087 (the
configure-transaction now also reconciles the index), and ADR-0088 (the sentence "Non-768
manifests are refused… the only dimension gate" is reversed). The old ADRs are immutable; this
record is the amendment.

## Consequences

- Any manifest dimension activates for the code corpus; `kind=code` search vectors are stored at
  the engine's dimension.
- Pre-1.35 banks are untouched until a different-dimension engine activates (then reconcile +
  re-embed, one transaction).
- The open reconcile is DDL-only: a bank whose `vec_code` was reconciled away while rows are
  still `embedded` degrades to FTS-only until the next activation/fingerprint change — the same
  property memory's open reconcile already has, covered by the existing FTS-degrade contract.
- The layering rule is preserved: the new `ICodeEmbedder.ReconcileVecCodeDimensionsAsync` member
  lives on an infrastructure-only port (`ICodeEmbedder` is consumed by `CodeReindexJob`,
  `SqliteCodeSearchService`, `EmbedDrainService` — none CLI-reachable), and the call site is
  `NodeRunner`, the one path that becomes the server.
