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

---

## Post-Wave-1 Integration — 2026-08-04 (query construction: stopwords + bigrams + AND-with-OR-fallback)

Corpus: jsaa-memory.db (762 chunks, clean Wave 0 — unchanged by Wave 1). Commit: 80472af. Full suite 444 passed / 0 failed / 43 skipped.

### Per-query (hybrid, expected-source suite) — vs Wave 0

| Query | Wave 0 exact / file | Wave 1 exact / file | Delta |
|-------|--------------------:|--------------------:|-------|
| A1 | 1 / 1 | 1 / 1 | = |
| A2 | 2 / 1 | 2 / 1 | = |
| A3 | 4 / 1 | 4 / 1 | = |
| A4 | 5 / 1 | 5 / 1 | = (was beyond top-10 pre-fix, see note) |
| A5 | 1 / 1 | 2 / 1 | exact 1→2 (still ≤3; file =) |
| A6 | — / 4 | — / 4 | = |
| A7 | — / 1 | — / 1 | = |
| C1 | 1 / 1 | 1 / 1 | = |
| C2 | 1 / 1 | 1 / 1 | = |
| C5 | 1 / 1 | 1 / 1 | = |

Exact-chunk @3: 3/10 (unchanged). File-level @3: 9/10 (unchanged). Zero-match across all 35 baseline queries: 0.

### Metrics

| Metric | Wave 0 | Wave 1 | Delta |
|--------|-------:|-------:|-------|
| nDCG@5 (ADR) | 0.642 | 0.652 | +0.010 |
| MRR (ADR) | 0.893 | 0.893 | = |
| recall@5 (ADR) | 0.544 | 0.544 | = |
| Invariants nDCG@5 | 1.000 | 1.000 | = |
| FTS-only ADR file hit@5 | — | 7/7 | guard ≥6/7 ✓ |
| FTS-only MRR (expected-source) | — | 0.825 | guard ≥0.70 ✓ |

### Notes

- **A4 boundary analysis (degradation caught at integration, fixed before accept):** with the
  initial trigger (`hits < max(TokenCount, limit)`) A4's exact chunk fell from rank 5 to
  beyond top-10: "What happened to the MCP server?" → AND primary 'happened AND mcp AND
  server' matches exactly max(3, 5) = 5 rows (the boundary), yet 'happened' does not occur
  in the Decision chunk, so the AND list is file-precise (file@1 held) but excludes the
  target chunk. Measured across all 35 queries: A4 is the ONLY query sitting exactly at the
  boundary (all others under → fallback already fires, or A7 at 16 hits → comfortably over).
  Fix: trigger changed to `hits <= max(TokenCount, limit)`; A4's decision chunk returns to
  FTS-only rank 7 (fallback with bigrams; plain OR ranked it 8) and hybrid exact@5. Guarded
  by QueryConstructionTests.AndPrimary_AtBoundary_A4DecisionChunkRestoredByFallback.
- **Per-context restructure (review finding):** the fallback is now decided per context
  (SearchContexts) and the vector pass runs once per context — previously a global trigger
  re-ran the whole pass (vector included) on ~46% of short-query searches. Behavior on the
  single-context baseline suite is identical; multi-context searches no longer double-count
  or cross-drag fallback decisions.
- **Diagnostic triplet (FTS-only):** Q1 "What is ADR-0070 about?" file@1; Q2 "ADR-0070"
  file@1 (≤5 ✓); Q3 "documentation structure trust model" returns results.
- **Verdict:** improvement — nDCG@5 +0.010, no rank regression on any expected-source
  query, invariants intact, FTS-only guard exceeded, zero-match eliminated.
