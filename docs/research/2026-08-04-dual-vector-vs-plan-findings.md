# Dual-Vector per-Query α vs Plan FTS Fixes — Findings

**Date**: 2026-08-04 · **Task**: retrieval-improvement-cont
**Question**: Does the dual-vector per-query-α approach carry its weight next to the plans'
cheap FTS-side fixes (Wave 1 query construction, Wave 3 source consolidation)? Do they combine?

## Method (summary)

- **Corpus** (held fixed): `jsaa-memory.db`, 6675 chunks — 1056 `docs/adr/`, 4723 `docs/work/`
  (71% pollution). No stored embeddings; each harness computes its own.
- **Queries**: all 35 from `scripts/baseline-queries.json`. Primary set A1–A7 (7 reachable
  expected-source queries, all `#decision` fragments). C1/C2/C5 sources absent from the corpus;
  coverage and H1–H3 explicitly non-evidential.
- **Arms**: V1 content-only (α=1.0), V6 structure-only (α=0.0), V7 fixed-α=0.5, V2–V5 sigmoid α at
  T∈{0.1,0.5,0.8,1.0} (dual-vector prototype, full corpus, ONNX all-MiniLM-L6-v2 in-process);
  F1 current OR-join normalizer, F2 fixed normalizer (stopwords+identifier-AND+AND-for-short+
  bigrams), F3 F2+source consolidation (FTS harness, real FTS5 `bm25()`); F+V = RRF(k=60,1:1)
  fusion of F2 with every V-arm.
- **Metrics** computed once by `scripts/compare-harnesses.py` from both harnesses' raw top-100
  ranked lists: file-level hit@5, **section-level hit@5 (primary)**, MRR(file), MRR(section),
  per-query ranks.
- **Pre-registered win rule**: an arm beats content-only iff ≥2 section-level hit flips OR
  MRR(file) delta ≥ 0.1; below = tie.
- **Memory safety**: every run under `scripts/run-with-memcap.sh 6` (6 GB RSS cap; the prior
  prototype run OOM-killed the machine at 50 GB). Dual-vector run: 81.5 s wall, peak RSS ~0.8 GB.

## Results

### Vector arms (MEASURED)

| Arm | File hit@5 | Section hit@5 | MRR (file) | Mean α |
|---|---:|---:|---:|---:|
| content-only (α=1.0) | 6/7 | 2/7 | 0.417 | 1.000 |
| structure-only (α=0.0) | 4/7 | 1/7 | 0.350 | 0.000 |
| fixed-α=0.5 | 6/7 | 3/7 | 0.529 | 0.500 |
| sigmoid T=0.1 | 6/7 | 3/7 | 0.529 | 0.511 |
| sigmoid T=0.5 | 6/7 | 3/7 | 0.529 | 0.556 |
| sigmoid T=0.8 | 6/7 | 3/7 | 0.600 | 0.589 |
| sigmoid T=1.0 | 6/7 | 3/7 | 0.600 | 0.611 |

Per-query (best arm = sigmoid T=1.0, file rank / section rank):
A1 5/— (content-only: 2), A2 1/1 (content-only: 4), A3 1/1 (3), A4 2/5 (2),
A5 2/— (3), A6 —/— (—), A7 1/— (1).

### FTS arms (PENDING — FTS harness running)

| Arm | File hit@5 | Section hit@5 | MRR (file) |
|---|---:|---:|---:|
| F1 current OR-join | … | … | … |
| F2 fixed normalizer | … | … | … |
| F3 F2+consolidation | … | … | … |

### Hybrid F+V (PENDING — depends on F2)

## Graded findings

- **MEASURED — Structure signal helps, per-query α machinery does not.** fixed-α=0.5 lifts
  MRR(file) over content-only (0.529 vs 0.417, Δ=0.11) and section hits 2→3. Sigmoid arms at
  T≥0.8 add only 0.07 MRR over fixed-α=0.5 — within noise on 7 queries. Mean α 0.51–0.61 shows
  the sigmoid saturates near 0.5, i.e. confidence voting barely moves α per query.
  Evidence: results-dual-vector.md metrics table; per-query α values.
- **MEASURED — Structure-only is degenerate (predicted failure mode).** structure-only scores
  4/7 file, 1/7 section, and its top result for A7 is README.md with heading path "adr" — the
  generic-heading spurious match the research doc predicted. Structure is a fuse-able signal,
  not a standalone ranker.
- **MEASURED — A6 is a ground-truth artifact, not a retrieval failure.** "data erasure" returns
  ADR-0068 at rank 1 in every vector arm; expectedKnowledge names BOTH ADR-0067 and ADR-0068,
  but expectedSource names only 0067. Treating 0067-only as ground truth penalizes a correct
  answer. A6 needs a multi-answer ground truth (either ADR acceptable).
- **MEASURED — Vector arms answer the identifier query A7 (rank 1 via the title chunk).** The
  plan's premise "V-arms can't do ADR-number queries" does not hold on this corpus: content-only
  and sigmoid both find ADR-0070 at rank 1. The section-level hit still misses (the Decision
  chunk is not in top-5).
- **MEASURED — A1 regresses under structure fusion** (file rank 2 → 5). The structure signal
  pulls other documents' sections up for the shadcn/ui query. One flip; visible in the per-query
  table, not averaged away.
- **MEASURED — memory-safe at scale**: 6675 chunks embedded in-process in 81.5 s at ~0.8 GB RSS
  (was 50 GB OOM before the fix: hash-keyed content embeddings + batched calls + watchdog).
- **PENDING — FTS side** … (fill after FTS harness completes)
- **PENDING — F+V hybrid** … (fill after F2 list available)

## Verdict (PENDING F-side)

## What this means for the plans

## Limitations

1. 7-query primary set → low power (pre-registered win rule mitigates).
2. Corpus 71% docs/work pollution; absolute numbers corpus-conditional. A Wave 0 clean-corpus
   re-run is required before shipping any change (plan C gate).
3. Vector arms are vector-only, FTS arms are FTS-only; neither equals today's hybrid (which is
   ≈F1 on this DB — no stored vectors).
4. No scored invariant guard (C-sources absent from corpus).
5. Heading-path parse and matcher live in the spike; a production implementation must define the
   structure metadata contract first (Wave 2 schema work).
