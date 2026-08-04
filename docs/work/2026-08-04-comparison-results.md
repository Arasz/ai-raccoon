# Dual-Vector vs Plan Fixes — Comparison Results

Corpus: jsaa-memory.db (6675 chunks). Queries A1-A7 primary. Section ground truth available: 6/7 (decision heading found in ranked lists).

## Metrics by Arm (A1-A7)

| Arm | File hit@5 | Section hit@5 | MRR (file) | MRR (section) | Beats content-only? |
|---|---:|---:|---:|---:|---|
| V:content-only | 7/7 | 4/6 | 0.5595 | 0.3690 | — |
| V:structure-only | 5/7 | 1/6 | 0.4929 | 0.1429 | no |
| V:fixed-a0.5 | 7/7 | 6/6 | 0.6714 | 0.4571 | YES |
| V:sigmoid-T0.1 | 7/7 | 6/6 | 0.6714 | 0.4571 | YES |
| V:sigmoid-T0.5 | 7/7 | 6/6 | 0.6714 | 0.4929 | YES |
| V:sigmoid-T0.8 | 7/7 | 6/6 | 0.7429 | 0.5571 | YES |
| V:sigmoid-T1.0 | 7/7 | 6/6 | 0.7429 | 0.5571 | YES |
| F:F1 | 6/7 | 1/6 | 0.7500 | 0.1429 | YES |
| F:F2 | 3/7 | 1/6 | 0.4286 | 0.0286 | no |
| F:F3 | 3/7 | 0/6 | 0.4286 | 0.0000 | no |
| FV:content-only | 7/7 | 4/6 | 0.6190 | 0.2976 | tie |
| FV:structure-only | 4/7 | 1/6 | 0.5000 | 0.1429 | no |
| FV:fixed-a0.5 | 7/7 | 6/6 | 0.6190 | 0.3500 | YES |
| FV:sigmoid-T0.1 | 7/7 | 6/6 | 0.6190 | 0.3500 | YES |
| FV:sigmoid-T0.5 | 7/7 | 6/6 | 0.6190 | 0.3857 | YES |
| FV:sigmoid-T0.8 | 7/7 | 5/6 | 0.6548 | 0.3929 | tie |
| FV:sigmoid-T1.0 | 7/7 | 5/6 | 0.6310 | 0.3690 | tie |

## Per-Query A1-A7 (file rank / section rank; — = miss)

| Arm | A1 | A2 | A3 | A4 | A5 | A6 | A7 |
|---|---|---|---|---|---|---|---|
| V:content-only | 2/— | 4/4 | 3/3 | 2/— | 3/— | 1/1 | 1/1 |
| V:structure-only | —/— | 1/1 | 4/— | 1/— | 5/— | 1/— | —/— |
| V:fixed-a0.5 | 5/5 | 1/1 | 1/1 | 2/4 | 2/— | 1/4 | 2/2 |
| V:sigmoid-T0.1 | 5/5 | 1/1 | 1/1 | 2/4 | 2/— | 1/4 | 2/2 |
| V:sigmoid-T0.5 | 5/5 | 1/1 | 1/1 | 2/4 | 2/— | 1/2 | 2/2 |
| V:sigmoid-T0.8 | 5/5 | 1/1 | 1/1 | 2/5 | 2/— | 1/2 | 1/1 |
| V:sigmoid-T1.0 | 5/5 | 1/1 | 1/1 | 2/5 | 2/— | 1/2 | 1/1 |
| F:F1 | 1/— | 1/1 | 1/— | 1/— | 4/— | 1/— | —/— |
| F:F2 | 1/5 | —/— | 1/— | 1/— | —/— | —/— | —/— |
| F:F3 | 1/— | —/— | 1/— | 1/— | —/— | —/— | —/— |
| FV:content-only | 1/— | 4/4 | 3/3 | 1/— | 4/— | 1/1 | 2/2 |
| FV:structure-only | —/— | 1/1 | 2/— | 1/— | —/— | 1/— | —/— |
| FV:fixed-a0.5 | 4/4 | 1/1 | 2/2 | 1/5 | 3/— | 1/4 | 4/4 |
| FV:sigmoid-T0.1 | 4/4 | 1/1 | 2/2 | 1/5 | 3/— | 1/4 | 4/4 |
| FV:sigmoid-T0.5 | 4/4 | 1/1 | 2/2 | 1/5 | 3/— | 1/2 | 4/4 |
| FV:sigmoid-T0.8 | 4/4 | 1/1 | 2/2 | 1/— | 3/— | 1/2 | 2/2 |
| FV:sigmoid-T1.0 | 4/4 | 1/1 | 3/3 | 1/— | 3/— | 1/2 | 2/2 |

## Notes

- Beats-content-only rule (pre-registered): ≥2 query flips on section-level hit@5 OR MRR(file) delta ≥ 0.1 vs content-only; below = tie.
- Queries with section ground truth missing: ['A5']
- Coverage and H1-H3 are non-evidential on this corpus (71% docs/work pollution); not scored.
