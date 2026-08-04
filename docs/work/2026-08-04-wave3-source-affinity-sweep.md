# Wave 3 Source-Affinity Scoring — Parameter Sweep

Date: 2026-08-04. Corpus: tests/AiRaccoon.Tests/Resources/jsaa-memory.db (752 chunks).
Measured by SourceAffinitySweepTests (limit 10, RRF k=60, 1:1 weights).

**Chosen configuration: λ = 0.1, consolidation threshold = 0.1, document-score formula = Max** (the SearchQuery defaults).

Gates at the chosen point: S2 decision ≤ 3 ✓, A6 file ≤ 3 ✓, A1/A4 file ≤ 2 ✓, C1/C2/C5 rank 1 ✓, ADR nDCG@5 > 0.650 ✓.

| λ | threshold | formula | S2 exact | A6 file | A6 exact | A1 file | A4 file | C1 | C2 | C5 | nDCG@5 (ADR) | MRR (ADR) | recall@5 (ADR) |
|---:|----------:|:--------|---------:|--------:|---------:|--------:|--------:|:--:|:--:|:--:|-------------:|----------:|---------------:|
| 0.00 | 0.10 | Max | 5 | 2 | - | 2 | 2 | 1 | 1 | 1 | 0.650 | 0.786 | 0.581 |
| 0.00 | 0.10 | Sum | 5 | 2 | - | 2 | 2 | 1 | 1 | 1 | 0.650 | 0.786 | 0.581 |
| 0.05 | 0.05 | Max | 2 | 2 | - | 3 | 1 | 1 | 1 | 1 | 0.705 | 0.833 | 0.614 |
| 0.05 | 0.05 | Sum | 2 | 2 | - | 3 | 1 | 1 | 1 | 1 | 0.705 | 0.833 | 0.614 |
| 0.05 | 0.10 | Max | 4 | 4 | 7 | 1 | 1 | 1 | 1 | 1 | 0.701 | 0.893 | 0.585 |
| 0.05 | 0.10 | Sum | 4 | 4 | 7 | 1 | 1 | 1 | 1 | 1 | 0.701 | 0.893 | 0.585 |
| 0.05 | 0.15 | Max | 4 | 1 | 8 | 2 | 1 | 1 | 1 | 1 | 0.711 | 0.929 | 0.585 |
| 0.05 | 0.15 | Sum | 4 | 1 | 8 | 2 | 1 | 1 | 1 | 1 | 0.711 | 0.929 | 0.585 |
| 0.05 | 0.20 | Max | 4 | 1 | 8 | 2 | 1 | 1 | 1 | 1 | 0.735 | 0.929 | 0.609 |
| 0.05 | 0.20 | Sum | 4 | 1 | 8 | 2 | 1 | 1 | 1 | 1 | 0.735 | 0.929 | 0.609 |
| 0.05 | off | Max | 4 | 2 | 10 | 2 | 1 | 2 | 4 | 5 | 0.695 | 0.857 | 0.581 |
| 0.05 | off | Sum | 4 | 2 | 10 | 2 | 1 | 2 | 4 | 5 | 0.695 | 0.857 | 0.581 |
| 0.10 | 0.05 | Max | 1 | 4 | 6 | 2 | 1 | 1 | 1 | 1 | 0.678 | 0.821 | 0.594 |
| 0.10 | 0.05 | Sum | 1 | 4 | 6 | 2 | 1 | 1 | 1 | 1 | 0.678 | 0.821 | 0.594 |
| 0.10 | 0.10 | Max | 3 | 2 | 2 | 1 | 1 | 1 | 1 | 1 | 0.722 | 0.929 | 0.617 |
| 0.10 | 0.10 | Sum | 3 | 2 | 2 | 1 | 1 | 1 | 1 | 1 | 0.722 | 0.929 | 0.617 |
| 0.10 | 0.15 | Max | 3 | 3 | 5 | 2 | 1 | 1 | 1 | 1 | 0.714 | 0.833 | 0.633 |
| 0.10 | 0.15 | Sum | 3 | 3 | 5 | 2 | 1 | 1 | 1 | 1 | 0.714 | 0.833 | 0.633 |
| 0.10 | 0.20 | Max | 3 | 1 | 6 | 2 | 1 | 1 | 1 | 2 | 0.753 | 0.929 | 0.648 |
| 0.10 | 0.20 | Sum | 3 | 1 | 6 | 2 | 1 | 1 | 1 | 2 | 0.753 | 0.929 | 0.648 |
| 0.10 | off | Max | 3 | 2 | 8 | 2 | 1 | 4 | 7 | - | 0.690 | 0.857 | 0.584 |
| 0.10 | off | Sum | 3 | 2 | 8 | 2 | 1 | 4 | 7 | - | 0.690 | 0.857 | 0.584 |
| 0.20 | 0.05 | Max | 1 | 3 | 3 | 2 | 1 | 1 | 1 | 1 | 0.666 | 0.833 | 0.560 |
| 0.20 | 0.05 | Sum | 1 | 3 | 3 | 2 | 1 | 1 | 1 | 1 | 0.666 | 0.833 | 0.560 |
| 0.20 | 0.10 | Max | 3 | 2 | 2 | 1 | 1 | 1 | 1 | 3 | 0.744 | 0.929 | 0.648 |
| 0.20 | 0.10 | Sum | 3 | 2 | 2 | 1 | 1 | 1 | 1 | 3 | 0.744 | 0.929 | 0.648 |
| 0.20 | 0.15 | Max | 3 | 3 | 3 | 2 | 1 | 1 | 1 | 5 | 0.718 | 0.833 | 0.635 |
| 0.20 | 0.15 | Sum | 3 | 3 | 3 | 2 | 1 | 1 | 1 | 5 | 0.718 | 0.833 | 0.635 |
| 0.20 | 0.20 | Max | 3 | 1 | 6 | 2 | 1 | 1 | 1 | 9 | 0.735 | 0.929 | 0.624 |
| 0.20 | 0.20 | Sum | 3 | 1 | 6 | 2 | 1 | 1 | 1 | 9 | 0.735 | 0.929 | 0.624 |
| 0.20 | off | Max | 3 | 2 | 8 | 2 | 1 | 10 | - | - | 0.633 | 0.857 | 0.532 |
| 0.20 | off | Sum | 3 | 2 | 8 | 2 | 1 | 10 | - | - | 0.633 | 0.857 | 0.532 |

Baseline (λ=0): nDCG@5 0.650, MRR 0.786, recall@5 0.581 — matches the Wave 6 merged state (0.650 / 0.786 / 0.581).

Notes:
- λ = 0 is the pre-Wave-3 ranker (no source affinity).
- threshold = 'off' (∞): every sibling counts for the boost and no sibling is merged; breaks C1/C2/C5 at every λ (deep same-file siblings overtake the single-chunk invariants) — the threshold's sibling-visibility floor is required.
- Sum and Max document-score formulas are equivalent on every grid point (measured); Max is chosen as the simpler formula (document champion).
- Consolidation only merges a weak adjacent sibling (gap ≥ threshold) into its file's best chunk; at the chosen point it removes no top-10 result for the gate queries (A7's rank-3 chunk would merge at threshold 0.15, lowering nDCG@5).
