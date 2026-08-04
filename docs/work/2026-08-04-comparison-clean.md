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

---

## Post-Wave-2 Integration — 2026-08-04 (source as first-class citizen: source_file + weighted FTS)

Corpus: jsaa-memory.db REGENERATED — 752 chunks (762 − 10 CLAUDE/HERMES byte-identical dedup collisions), 752 embedded, 0 hash mismatches, 196 source files, 664 sectioned; provenance headers removed from content (2d). Commit: 54b494e. Full suite 469 passed / 0 failed / 43 skipped.

### Per-query (hybrid, limit 10) — vs Wave 0 (old corpus) and post-Wave-1

| Query | W0 exact/file | W1 exact/file | W2 exact/file | Delta vs W0 |
|-------|--------------:|--------------:|--------------:|-------------|
| A1 | 1 / 1 | 1 / 1 | 1 / 1 | = |
| A2 | 2 / 1 | 2 / 1 | 3 / 1 | exact 2→3 (still ≤3) |
| A3 | 4 / 1 | 4 / 1 | 2 / 1 | exact 4→2 ✓ |
| A4 | 5 / 1 | 5 / 1 | 4 / 1 | exact 5→4 ✓ |
| A5 | 1 / 1 | 2 / 1 | 1 / 1 | = |
| A6 | — / 4 | — / 4 | — / 4 | = (open case, Wave 3) |
| A7 | — / 1 | — / 1 | — / 1 | = |
| S2 | — | — | — / 1 | new query; file ≤3 ✓ (section chunk → Wave 6) |
| C1 | 1 / 1 | 1 / 1 | 1 / 1 | = |
| C2 | 1 / 1 | 1 / 1 | — / — | hybrid COLLAPSED — see analysis |
| C5 | 1 / 1 | 1 / 1 | 1 / 1 | = |

Exact-chunk @3: 6/11 (W0: 3/10 — improved). File-level @3: 9/11. Zero-match: 0.

### Metrics

| Metric | Wave 0 | Wave 1 | Wave 2 | Delta vs W0 |
|--------|-------:|-------:|-------:|-------------|
| nDCG@5 (ADR) | 0.642 | 0.652 | 0.674 | +0.032 |
| MRR (ADR) | 0.893 | 0.893 | 0.893 | = |
| recall@5 (ADR) | 0.544 | 0.544 | 0.559 | +0.015 |
| Invariants nDCG@5 | 1.000 | 1.000 | 0.667 | −0.333 (C2) |

### C2 degradation analysis (priority per integration rule)

- **What:** C2 (screaming-architecture invariant) hybrid rank 1 → beyond top-10 (measured rank 18 at limit 30). FTS-only rank 1 holds; vector-only rank >100 (absent from top-100).
- **Why:** Wave 2 2d removed the embedded `## Source:`/`[context]` provenance prefix from chunk content. The Wave-0 hybrid rank 1 was an artifact: the vector modality matched the query against the provenance prefix text, not the invariant body. With clean content the invariant's embedding no longer ranks near the query; under RRF (k=60) the single strong FTS contribution (1/61 ≈ 0.0164) is outscored by chunks that receive contributions from BOTH modalities, sinking a perfect FTS rank 1. Verified live by the Wave 2 review (probe: hybrid 18, FTS-only 1, vector >100; RRF math reproduced).
- **Not a W2 bug** — a consequence of the intended 2d cleanup, exposed by the current fixed RRF weights.
- **Plan revision (folded into Wave 4 gate):** the RRF sweep gains an acceptance criterion — "C2 hybrid rank ≤ 3 after the sweep (restoring the invariant's hybrid visibility); if no sweep point achieves it, the fusion design (weights/minScore/candidate window) is revisited before Wave 5b."

### Other notes

- Source identity live: results carry SourceFile/ChunkIndex/TotalChunks (A1 top-1 src=docs/adr/0011-frontend-chassis-stack.md idx=2/5); source-path query `docs/adr/0011-frontend-chassis-stack.md#decision` returns the exact chunk at rank 1 (hybrid and FTS-only).
- Q2 "ADR-0070" FTS-only file rank 1 (≤3 ✓) — the weighted source column fixes the identifier path without the vector crutch.
- bm25 weights (1.0, 8.0, 16.0) documented in ADR-0003; context-label searches now include custom-scoped rows without double-counting project rows in RRF (review fix).
- Migration is transactional with heal-on-reopen (review fix): a crash mid-rebuild cannot leave a bank without an FTS index.
- **Verdict:** improvement on ADR metrics (nDCG +0.032, recall +0.015, exact@3 3/10 → 6/11) and all identifier/source gates pass; one documented invariant hybrid regression (C2) analyzed, attributed, and assigned to Wave 4.

---

## Post-Wave-6 Integration — 2026-08-04 (section-targeted retrieval: dual-vector structure signal)

Corpus: jsaa-memory.db — 752 chunks (W2 state) + Wave 6 backfill: heading_path + structure_embedding
on 746 chunks, 704 unique heading paths, vec_structure populated; chunk content/hashes unchanged
(chunk-hash-map.json untouched). Commit: de648b4. Full suite 505 passed / 0 failed / 43 skipped.

### Per-query (hybrid) — vs post-Wave-2

| Query | W2 exact/file | W6 exact/file | Delta |
|-------|--------------:|--------------:|-------|
| A1 | 1 / 1 | 2 / 2 | file 1→2 — same-knowledge alt (see notes) |
| A2 | 3 / 1 | 1 / 1 | exact 3→1 ✓ |
| A3 | 1 / 1 | 3 / 1 | exact 1→3 (file =; ≤3) |
| A4 | 4 / 1 | 5 / 2 | file 1→2 — same-knowledge alt (see notes) |
| A5 | 1 / 1 | 1 / 1 | = |
| A6 | — / 4 | — / 2 | file 4→2 ✓ (plan Wave-3 target, hit by W6) |
| A7 | — / 1 | 4 / 1 | exact restored ✓ |
| S2 | — / 1 | 5 / 1 | file ≤3 ✓; decision chunk 5 (Wave 3 gate) |
| C1 | 1 / 1 | 1 / 1 | = |
| C2 | — / — | 1 / 1 | RESTORED to hybrid rank 1 (Wave-4 criterion satisfied) |
| C5 | 1 / 1 | 1 / 1 | = |

### Metrics

| Metric | W0 | W1 | W2 | W6 | Delta vs W2 |
|--------|-----|-----|-----|-----|-------------|
| nDCG@5 (ADR) | 0.642 | 0.652 | 0.674 | 0.650 | −0.024 (still > W0) |
| MRR (ADR) | 0.893 | 0.893 | 0.893 | 0.786 | −0.107 (A1/A4 file slips) |
| recall@5 (ADR) | 0.544 | 0.544 | 0.559 | 0.581 | +0.022 ✓ |
| Invariants nDCG@5 | 1.000 | 1.000 | 0.667 | 1.000 | +0.333 ✓ (C2 restored) |
| Section hit@5 (A1-A5,A7) | — | — | — | 6/6 | gate ≥4/6 ✓ |

### Notes (content-verified per integration rule)

- **A1/A4 rank-1 alternatives carry the same knowledge (chunks read and verified):** A1 —
  frontend-architecture.md#3 "The gluestack → shadcn/ui pivot" states the evidence and links
  "The formal decision record is ADR-0011 §1" (ADR-0011 links back "Full evidence:
  docs/frontend-architecture.md §3"). A4 — behaviour-specification.md#3 "MCP tools — retired":
  "The MCP server was deleted; see ADR-0060". Both expected files stay in the top-2; the MRR
  cost reflects ground-truth rank movement, not answer-quality loss.
- **A3** decision chunk 1→3 (file rank 1 held); rank-2 = docs:architecture#4-auth-security —
  weakly relevant to "offer-page fetching security" (auth, not fetch security); bounded.
- **A6 rank-1 = ADR-0069#consequences** (retention sweep, cross-links ADR-0068) — legitimate
  erasure-adjacent answer; expected ADR-0067 file improved 4→2.
- **S2** decision chunk ranks 5 — the top-1 is the ADR's metadata header (within-file sibling
  competition); the plan's S2 ≤3 target moves to Wave 3's source-affinity gate.
- **C2 restored by the structure signal** (invariant heading path matches the query embedding) —
  the Wave-4 C2 acceptance criterion is already satisfied; Wave 4's sweep now only needs to
  hold it.
- α is bank-tunable via `memory_set_structure_alpha(projectId, alpha)` (rw tier; the
  open question is resolved — see plan Wave 6 gate amendments).
- **Verdict:** the wave delivers its purpose — section-targeted retrieval (S2 file-level, S4 ≤3,
  section hit@5 6/6) plus C2/A6/A7/recall improvements — at bounded, content-verified file-rank
  costs on A1/A4 (same-knowledge alternatives) and A3 (exact ≤3).
