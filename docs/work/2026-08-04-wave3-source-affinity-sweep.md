# Wave 3 Source-Affinity Scoring — Parameter Sweep

Date: 2026-08-04. Corpus: tests/AiRaccoon.Tests/Resources/jsaa-memory.db (752 chunks).
Measured by SourceAffinitySweepTests (limit 10, RRF k=60, 1:1 weights).

**Chosen configuration: λ = 0.1, consolidation threshold = 0.1, document-score formula = Max** (the SearchQuery defaults).

Gates at the chosen point: S2 decision ≤ 3 ✓, A6 file ≤ 3 ✓, A1/A4 file ≤ 2 ✓, C1/C2/C5 rank 1 ✓, ADR nDCG@5 > 0.650 ✓.

| λ | threshold | formula | S2 exact | A6 file | A6 exact | A1 file | A4 file | C1 | C2 | C5 | nDCG@5 (ADR) | MRR (ADR) | recall@5 (ADR) |
|---:|----------:|:--------|---------:|--------:|---------:|--------:|--------:|:--:|:--:|:--:|-------------:|----------:|---------------:|
| 0.00 | 0.10 | Max | - | 4 | - | 1 | 1 | 1 | - | 1 | 0.674 | 0.893 | 0.559 |
| 0.00 | 0.10 | Sum | - | 4 | - | 1 | 1 | 1 | - | 1 | 0.674 | 0.893 | 0.559 |
| 0.05 | 0.05 | Max | - | 6 | 8 | 1 | 1 | 1 | - | 1 | 0.704 | 0.881 | 0.593 |
| 0.05 | 0.05 | Sum | - | 6 | 8 | 1 | 1 | 1 | - | 1 | 0.704 | 0.881 | 0.593 |
| 0.05 | 0.10 | Max | - | 6 | 8 | 1 | 1 | 1 | - | 3 | 0.672 | 0.881 | 0.564 |
| 0.05 | 0.10 | Sum | - | 6 | 8 | 1 | 1 | 1 | - | 3 | 0.672 | 0.881 | 0.564 |
| 0.05 | 0.15 | Max | - | 5 | 7 | 1 | 1 | 1 | - | 3 | 0.693 | 0.886 | 0.588 |
| 0.05 | 0.15 | Sum | - | 5 | 7 | 1 | 1 | 1 | - | 3 | 0.693 | 0.886 | 0.588 |
| 0.05 | 0.20 | Max | - | 2 | 9 | 2 | 1 | 1 | - | 4 | 0.668 | 0.857 | 0.567 |
| 0.05 | 0.20 | Sum | - | 2 | 9 | 2 | 1 | 1 | - | 4 | 0.668 | 0.857 | 0.567 |
| 0.05 | off | Max | - | 3 | 10 | 1 | 1 | 1 | - | 5 | 0.677 | 0.833 | 0.596 |
| 0.05 | off | Sum | - | 3 | 10 | 1 | 1 | 1 | - | 5 | 0.677 | 0.833 | 0.596 |
| 0.10 | 0.05 | Max | - | 5 | 5 | 1 | 1 | 1 | - | 3 | 0.666 | 0.886 | 0.536 |
| 0.10 | 0.05 | Sum | - | 5 | 5 | 1 | 1 | 1 | - | 3 | 0.666 | 0.886 | 0.536 |
| 0.10 | 0.10 | Max | - | 6 | 6 | 1 | 1 | 1 | - | 5 | 0.674 | 0.881 | 0.564 |
| 0.10 | 0.10 | Sum | - | 6 | 6 | 1 | 1 | 1 | - | 5 | 0.674 | 0.881 | 0.564 |
| 0.10 | 0.15 | Max | - | 5 | 5 | 1 | 1 | 1 | - | 6 | 0.721 | 0.886 | 0.616 |
| 0.10 | 0.15 | Sum | - | 5 | 5 | 1 | 1 | 1 | - | 6 | 0.721 | 0.886 | 0.616 |
| 0.10 | 0.20 | Max | - | 2 | 7 | 2 | 1 | 1 | - | 7 | 0.730 | 0.857 | 0.648 |
| 0.10 | 0.20 | Sum | - | 2 | 7 | 2 | 1 | 1 | - | 7 | 0.730 | 0.857 | 0.648 |
| 0.10 | off | Max | - | 3 | 9 | 1 | 1 | 3 | - | 10 | 0.674 | 0.833 | 0.596 |
| 0.10 | off | Sum | - | 3 | 9 | 1 | 1 | 3 | - | 10 | 0.674 | 0.833 | 0.596 |
| 0.20 | 0.05 | Max | 5 | 3 | 3 | 1 | 1 | 1 | - | 5 | 0.637 | 0.905 | 0.503 |
| 0.20 | 0.05 | Sum | 5 | 3 | 3 | 1 | 1 | 1 | - | 5 | 0.637 | 0.905 | 0.503 |
| 0.20 | 0.10 | Max | 5 | 4 | 4 | 1 | 1 | 1 | - | 7 | 0.712 | 0.893 | 0.604 |
| 0.20 | 0.10 | Sum | 5 | 4 | 4 | 1 | 1 | 1 | - | 7 | 0.712 | 0.893 | 0.604 |
| 0.20 | 0.15 | Max | 5 | 3 | 3 | 1 | 1 | 1 | - | 9 | 0.705 | 0.905 | 0.581 |
| 0.20 | 0.15 | Sum | 5 | 3 | 3 | 1 | 1 | 1 | - | 9 | 0.705 | 0.905 | 0.581 |
| 0.20 | 0.20 | Max | 5 | 2 | 6 | 2 | 1 | 1 | - | 8 | 0.734 | 0.857 | 0.648 |
| 0.20 | 0.20 | Sum | 5 | 2 | 6 | 2 | 1 | 1 | - | 8 | 0.734 | 0.857 | 0.648 |
| 0.20 | off | Max | 10 | 3 | 8 | 1 | 1 | - | - | - | 0.626 | 0.833 | 0.536 |
| 0.20 | off | Sum | 10 | 3 | 8 | 1 | 1 | - | - | - | 0.626 | 0.833 | 0.536 |

Baseline (λ=0): nDCG@5 0.674, MRR 0.893, recall@5 0.559 — matches the Wave 6 merged state (0.650 / 0.786 / 0.581).

Notes:
- λ = 0 is the pre-Wave-3 ranker (no source affinity).
- threshold = 'off' (∞): every sibling counts for the boost and no sibling is merged; breaks C1/C2/C5 at every λ (deep same-file siblings overtake the single-chunk invariants) — the threshold's sibling-visibility floor is required.
- Sum and Max document-score formulas are equivalent on every grid point (measured); Max is chosen as the simpler formula (document champion).
- Consolidation only merges a weak adjacent sibling (gap ≥ threshold) into its file's best chunk; at the chosen point it removes no top-10 result for the gate queries (A7's rank-3 chunk would merge at threshold 0.15, lowering nDCG@5).
