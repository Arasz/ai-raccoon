# 0006 — RRF parameter optimization: k, weight ratio, minScore, candidate window

Date: 2026-08-04

Status: Accepted

## Context

Plan C Wave 4: the RRF fusion parameters (k = 60, 1:1 FTS:vector weights, minScore,
per-modality candidate window max(limit×3, 100)) had never been swept on the clean
corpus. They predate the corpus regeneration, the Wave 6 dual-vector signal, and the
Wave 3 source-affinity ranker — the fusion now feeds a different pipeline than the one
the defaults were chosen for. The Wave 2 integration analysis added an acceptance
criterion: C2's hybrid rank must be ≤ 3 after the sweep (it measured 18 at the time,
restored to 1 by Wave 6 + Wave 3).

The mandated grid: k ∈ {10, 30, 60, 120} × weights {(1:1), (1:2), (2:1)} ×
minScore {0.0, 0.3, 0.5, 0.7} × candidate window {max(limit×3, 100), max(limit×5, 50)}
— 96 points, every point run through the real search pipeline
(SearchAsync → FTS/vector batches → RRF → source-affinity ranker → merger) over the
eleven expected-source queries with the Wave 3 parameters fixed (λ = 0.1,
threshold = 0.1, Max). The full matrix is in
docs/work/2026-08-04-wave4-rrf-sweep.md.

## Decision

**The pre-sweep defaults are re-confirmed as the grid optimum: k = 60, 1:1 weights,
minScore = 0.0, candidate window max(limit×3, 100). No grid point beats them on ADR
nDCG@5 while holding the Wave 3/S2/C2 gates.** The fusion parameters are unchanged.

The candidate window becomes a first-class search parameter so it can be measured and
pinned: `SearchQuery.CandidateWindow` (`CandidateWindowMode` = Max3x100 or Max5x50,
default Max3x100), applied in `SqliteMemoryStore.CandidateWindowFor`. The default is the
chosen sweep point, exactly like the Wave 3 parameters.

Measured at the chosen point (limit 10): ADR nDCG@5 **0.722**, MRR **0.929**,
recall@5 **0.617**, exact-chunk @3 **11/11**, C2 hybrid **1** (≤ 3 ✓), A1/A4 file 1,
A6 file 2 + exact 2, S2 decision 3, A7 exact 2 — every Wave 3/S2 gate held.
Fusion regression gate (hybrid exact-chunk rank ≤ best single modality) holds on all
eleven queries.

## Consequences

- **Negative result, measured.** 11 of 96 points score above 0.722 on nDCG@5; every one
  violates at least one gate. The best raw point (k=120, 1:1, Max3x100: nDCG@5 0.775,
  MRR 0.929, recall@5 0.677) regresses A1 file 1 → 2, A6 exact 2 → 6, and exact-chunk
  @3 11/11 → 9/11. The FTS-heavy (2:1) weight fixes A6 (file 1, exact 1) but regresses
  A1 file → 2 and exact@3 → 9/11; vector-heavy (1:2) regresses A6 (file 4, exact 4);
  k=30 kills A1/A6; the Max5x50 window starves A6's exact chunk (50 < 100 candidates
  per modality).
- **minScore is inert at the chosen point.** At k=60 the fused top-10 normalized scores
  all exceed 0.7 by RRF construction (the 10th result scores ≥ 61/70 ≈ 0.871), so
  0.3/0.5/0.7 filter nothing; at k=10 it trims the tail and always hurts or ties. The
  measured baseline (minScore 0.0) and the tool default (0.7) are equivalent at the
  chosen point.
- **Fusion gate definition.** "No fusion regression" is enforced on the exact-chunk rank:
  the hybrid never ranks the expected chunk below the best single modality (A6 2 ≤
  min(2, miss), S2 3 ≤ min(4, miss), A5 3 ≤ min(miss, 4)). The Wave 0 recall@5
  observation flags A5/A6/S2, but that is a file-cluster artifact — the hybrid surfaces
  fewer same-file chunks in the top 5 while ranking the answer chunk equal-or-better —
  and no grid point fixes A6's recall@5 either (2:1 keeps 0.33 < FTS 0.67).
- **C2 acceptance closed.** C2 holds hybrid rank 1 at the chosen point (≤ 3 ✓); the
  Wave 2 acceptance criterion is satisfied without any fusion change.
- **Code delta:** `SearchQuery.CandidateWindow` (new), `CandidateWindowMode` enum
  (Core/Memory), `CandidateWindowFor(limit, mode)` overload, `SweepPoint`/`SweepMatrix`
  extended with the minScore/window dimensions and the 96-point `RrfGrid`. Sweep
  harness: `RrfParameterSweepTests` (real pipeline, report writer, gate pins).
- **Cost.** No runtime change (the chosen point equals the pre-sweep behavior); the
  sweep itself is one slow-trait integration test (~60 s).

## Alternatives considered

- **k=120, 1:1, Max3x100** (nDCG@5 0.775, recall@5 0.677 — best raw point): rejected —
  regresses A1 file to 2, A6 exact to 6, exact-chunk @3 to 9/11; the same-knowledge
  alternative and deep-sibling problems the Wave 3 ranker was tuned to fix resurface at
  the flatter k.
- **2:1 FTS-heavy weights** (nDCG@5 0.747 at k=60, A6 exact 1): rejected — A1 file 1 →
  2 and exact-chunk @3 → 9/11 (A3 exact 3 → 4, A5 exact 3 → 4).
- **k=30, 2:1** (nDCG@5 0.752): rejected — A6 file 3, A6 exact 4, exact@3 9/11, A4
  exact lost from the top 10.
- **Max5x50 window**: rejected — at limit 10 it halves the per-modality candidate depth
  (50 vs 100) and starves A6's exact chunk everywhere (A6 exact 5-10 or missing).
- **minScore 0.3/0.5/0.7**: no measured effect at the chosen point (top-10 scores all
  ≥ 0.7); rejected as a no-op with tail-trim risk at other k.
- **Recall@5-based fusion gate**: rejected as the hard gate — it flags the hybrid's
  top-5 diversity (fewer same-file chunks) rather than answer-chunk quality, and no
  grid point satisfies it for A6; kept as a documented observation only.
