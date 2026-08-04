# Dual-Vector vs Plan Fixes — Comparison Results

Corpus: jsaa-memory.db (762 chunks, clean Wave 0). Expected-source queries: 10. Section ground truth available: 6/10.

## Metrics by Arm

| Arm               | File hit@5 | Section hit@5 | MRR (file) | MRR (section) | Beats content-only? |
|-------------------|-----------:|--------------:|-----------:|--------------:|---------------------|
| V:content-only    |       8/10 |           5/6 |     0.6750 |        0.5750 | —                   |
| V:structure-only  |       5/10 |           2/6 |     0.5000 |        0.3667 | no                  |
| V:fixed-a0.5      |       9/10 |           5/6 |     0.8200 |        0.6667 | YES                 |
| V:sigmoid-T0.1    |       8/10 |           5/6 |     0.8000 |        0.6667 | YES                 |
| V:sigmoid-T0.5    |       8/10 |           5/6 |     0.8000 |        0.6667 | YES                 |
| V:sigmoid-T0.8    |       8/10 |           5/6 |     0.8000 |        0.6667 | YES                 |
| V:sigmoid-T1.0    |       8/10 |           5/6 |     0.8000 |        0.6667 | YES                 |
| F:F1              |       9/10 |           3/6 |     0.7333 |        0.4250 | no                  |
| F:F2              |       4/10 |           1/6 |     0.3333 |        0.0333 | no                  |
| F:F3              |       4/10 |           1/6 |     0.3333 |        0.0333 | no                  |
| FV:content-only   |       8/10 |           4/6 |     0.6000 |        0.2500 | no                  |
| FV:structure-only |       7/10 |           3/6 |     0.4700 |        0.2233 | no                  |
| FV:fixed-a0.5     |       9/10 |           5/6 |     0.6500 |        0.3283 | tie                 |
| FV:sigmoid-T0.1   |       9/10 |           5/6 |     0.6500 |        0.3283 | tie                 |
| FV:sigmoid-T0.5   |       9/10 |           5/6 |     0.6500 |        0.3283 | tie                 |
| FV:sigmoid-T0.8   |       9/10 |           5/6 |     0.6500 |        0.3283 | tie                 |
| FV:sigmoid-T1.0   |       9/10 |           5/6 |     0.6500 |        0.3283 | tie                 |

## Per-Query (file rank / section rank; — = miss)

| Arm               | A1  | A2  | A3  | A4  | A5  | A6  | A7  | C1  | C2  | C5  |
|-------------------|-----|-----|-----|-----|-----|-----|-----|-----|-----|-----|
| V:content-only    | 1/1 | 1/4 | 1/1 | 2/4 | 1/1 | —/— | —/— | 1/1 | 4/4 | 1/1 |
| V:structure-only  | —/— | 1/3 | —/— | 1/3 | —/— | —/— | —/— | 1/1 | 1/1 | 1/1 |
| V:fixed-a0.5      | 1/1 | 1/3 | 1/1 | 1/3 | 1/1 | —/— | 5/— | 1/1 | 1/1 | 1/1 |
| V:sigmoid-T0.1    | 1/1 | 1/3 | 1/1 | 1/3 | 1/1 | —/— | —/— | 1/1 | 1/1 | 1/1 |
| V:sigmoid-T0.5    | 1/1 | 1/3 | 1/1 | 1/3 | 1/1 | —/— | —/— | 1/1 | 1/1 | 1/1 |
| V:sigmoid-T0.8    | 1/1 | 1/3 | 1/1 | 1/3 | 1/1 | —/— | —/— | 1/1 | 1/1 | 1/1 |
| V:sigmoid-T1.0    | 1/1 | 1/3 | 1/1 | 1/3 | 1/1 | —/— | —/— | 1/1 | 1/1 | 1/1 |
| F:F1              | 2/2 | 1/1 | 1/— | 1/— | 1/4 | —/— | 3/— | 2/2 | 1/1 | 1/1 |
| F:F2              | 3/3 | —/— | 1/— | 1/— | —/— | —/— | 1/— | —/— | —/— | —/— |
| F:F3              | 3/3 | —/— | 1/— | 1/— | —/— | —/— | 1/— | —/— | —/— | —/— |
| FV:content-only   | 2/2 | 1/4 | 1/4 | 1/— | 2/2 | —/— | 1/— | 2/2 | —/— | 2/2 |
| FV:structure-only | 5/5 | 1/3 | —/— | 1/5 | —/— | —/— | 1/— | 2/2 | 2/2 | 2/2 |
| FV:fixed-a0.5     | 2/2 | 1/3 | 1/4 | 1/5 | 2/2 | —/— | 1/— | 2/2 | 2/2 | 2/2 |
| FV:sigmoid-T0.1   | 2/2 | 1/3 | 1/4 | 1/5 | 2/2 | —/— | 1/— | 2/2 | 2/2 | 2/2 |
| FV:sigmoid-T0.5   | 2/2 | 1/3 | 1/4 | 1/5 | 2/2 | —/— | 1/— | 2/2 | 2/2 | 2/2 |
| FV:sigmoid-T0.8   | 2/2 | 1/3 | 1/4 | 1/5 | 2/2 | —/— | 1/— | 2/2 | 2/2 | 2/2 |
| FV:sigmoid-T1.0   | 2/2 | 1/3 | 1/4 | 1/5 | 2/2 | —/— | 1/— | 2/2 | 2/2 | 2/2 |

## Notes

- Beats-content-only rule (pre-registered): ≥2 query flips on section-level hit@5 OR MRR (file) delta ≥ 0.1 vs
  content-only; below = tie.
- Queries with section ground truth missing: ['A6', 'C1', 'C2', 'C5']
- Coverage and H1-H3 are non-evidential; not scored.
- Corpus: clean Wave 0 (762 chunks, 762 embedded, project_id=job-search-ai-assistant).
