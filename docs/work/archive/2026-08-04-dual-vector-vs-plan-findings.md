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
- **Arms**: V1 content-only (α=1.0), V6 structure-only (α=0.0), V7 fixed-α=0.5, V2–V5 sigmoid α
  at T∈{0.1,0.5,0.8,1.0} (dual-vector prototype, full corpus, ONNX all-MiniLM-L6-v2 in-process,
  `score = α·sim(q,content) + (1−α)·sim(q,structure)` where structure = heading-path embedding,
  α = sigmoid(confidence×T), confidence = max−mean of structure sims); F1 current OR-join
  normalizer, F2 fixed normalizer (stopwords + identifier-AND + AND-for-short + bigrams), F3
  F2+source consolidation (FTS harness, real FTS5 `bm25()` over `entries_fts`, window 100);
  F+V = RRF(k=60, 1:1) fusion of F2 with every V-arm.
- **Metrics** computed ONCE by `scripts/compare-harnesses.py` from both harnesses' raw top-100
  ranked lists (schema `{rank, hash, path, headingPath, score}` per arm per query): file-level
  hit@5, **section-level hit@5 (primary)**, MRR(file), MRR(section), per-query ranks.
  Ground-truth matcher: fragment-stripped last path segment (`0011-frontend-chassis-stack.md`);
  section = any heading-path segment equals the fragment (Decision sections carry sub-headings
  like `Decision > D9 — Orchestration` — last-segment matching would miss them). A6 is
  multi-answer (expectedKnowledge names ADR-0067 AND ADR-0068; expectedSource names 0067 only —
  scorer accepts either).
- **Pre-registered win rule**: an arm beats content-only iff ≥2 section-level hit flips OR
  MRR(file) delta ≥ 0.1; below = tie.
- **Memory safety**: every run under `scripts/run-with-memcap.sh 6` (6 GB RSS cap; the prior
  prototype run OOM-killed the machine at 50 GB — root cause was content embeddings keyed by
  text instead of hash + unbounded batches; fixed).

## Results

### Full comparison (A1–A7; section ground truth available 6/7 — A5's Decision chunk never ranks in any top-100)

| Arm | File hit@5 | Section hit@5 | MRR (file) | MRR (section) | Beats content-only? |
|---|---:|---:|---:|---:|---|
| V: content-only (α=1.0) | 7/7 | 4/6 | 0.560 | 0.369 | — |
| V: structure-only (α=0.0) | 5/7 | 1/6 | 0.493 | 0.143 | no |
| V: fixed-α=0.5 | 7/7 | **6/6** | 0.671 | 0.457 | **YES** |
| V: sigmoid T=0.1 / 0.5 | 7/7 | **6/6** | 0.671 | 0.457 / 0.493 | **YES** |
| V: sigmoid T=0.8 / 1.0 | 7/7 | **6/6** | **0.743** | **0.557** | **YES** |
| F: F1 current OR-join | 6/7 | 1/6 | **0.750** | 0.143 | YES |
| F: F2 fixed normalizer | 3/7 | 1/6 | 0.429 | 0.029 | no |
| F: F3 F2+consolidation | 3/7 | 0/6 | 0.429 | 0.000 | no |
| FV: F2 + sigmoid-T0.5 | 7/7 | 6/6 | 0.619 | 0.386 | YES (weakest) |
| FV: F2 + sigmoid-T0.8 | 7/7 | 5/6 | 0.655 | 0.393 | tie |

Cost: dual-vector run 81.5 s / peak RSS ~0.8 GB / 2× vector storage (content + structure
embeddings, ~6675 + 3638 vectors). FTS harness run: sub-second wall (FTS5 is cheap).

### F2 emitted MATCH expressions (A1–A7) — plan Wave 1 as specced

| Query | F2 expression | Outcome |
|---|---|---|
| A1 | `why OR shadcn OR ui OR chosen OR over OR gluestack OR io OR "why shadcn" OR …` (7 tokens → OR+bigrams) | hit@1 |
| A2 | `adr AND governs AND uuid AND choice` | **0 rows** |
| A3 | `project OR handle OR offer OR page OR fetching OR security OR …` (OR+bigrams) | hit@1 |
| A4 | `happened AND mcp AND server` | hit@1 |
| A5 | `replaced AND llm AND cost AND nfr` | 1 row, wrong file |
| A6 | `project AND handle AND data AND erasure` | **0 rows** |
| A7 | `adr AND 0070` | rank 11 (of 28 matches) |

### Length analysis (L): bm25 vs length-unaware raw-TF

| Query | Window | bm25 rank | raw-TF rank | Chunks outranking (raw-TF) | Outranker lengths |
|---|---:|---:|---:|---:|---|
| A1 | 100 | 1 | 2 | 1 | 151 words |
| A7 | 28 | 11 | 6 | 5 | 133–168 words |

## Graded findings

- **MEASURED — The structure signal delivers section-targeted retrieval; nothing else does.**
  Any structure fusion (fixed-α=0.5 or sigmoid) puts the Decision-section chunk of every
  expected file in top-5 (6/6), vs 4/6 for content-only and 1/6 for every FTS arm. MRR(section)
  rises 0.37 → 0.46–0.56. The exploration's core claim is confirmed: the structure embedding
  carries the section-intent signal the flat index lacks. Evidence: comparison table
  (`results-dual-vector.json` + `scripts/compare-harnesses.py` output).
- **MEASURED — Per-query α adds nothing over a fixed blend; the machinery collapses.** Sigmoid
  T=0.1 and T=0.5 are IDENTICAL to fixed-α=0.5 on every metric. T=0.8/1.0 add MRR(file)
  (0.67→0.74) but no section flips — within noise on 7 queries. Root cause: the confidence
  signal (max−mean of structure sims) is query-invariant on this corpus (0.39–0.49), so per-query
  α ≈ 0.58 always; the temperature knob only moves the mean. The research doc's open question
  ("does per-query α beat a fixed blend?") is answered: **no**.
  Evidence: per-query α/confidence in results-dual-vector.json (A1 α=0.597, A7 α=0.587, C1
  α=0.577); sigmoid arms ≡ fixed-a0.5 in the comparison table.
- **MEASURED — Plan Wave 1 as specced REGRESSES the status quo on this corpus.** F2 drops file
  hit@5 from 6/7 (F1) to 3/7, MRR 0.75 → 0.43, and section hits 1/6 → 1/6 (no gain). Two failure
  modes: (a) AND-for-short zero-matches when any non-stopword is absent from the target (A2
  `governs`, A6 `handle`; 11 of 35 queries zero-match); (b) identifier-AND fails A7 because the
  corpus's cross-referencing ADRs hold more `adr`+`0070` occurrences than ADR-0070's own chunks
  (28 matches; ADR-0071 alone holds 13; target at rank 11). The plan's Wave-1 prediction
  ("stopword removal alone fixes ADR-0070") is **falsified on this corpus**.
  Evidence: results-plan.md F2 MATCH expressions + zero-match list; direct FTS5 `bm25()` queries.
- **MEASURED — Length normalization is real but not the whole A7 story.** Removing it moves
  ADR-0070 from rank 11 to rank 6 (length-unaware raw-TF) — still outside top-5, because the
  outrankers' term frequency (not their length) is what beats the target. Length normalization is
  a contributor, not the cause; the cause is cross-reference term frequency + no path/source
  signal. Evidence: L analysis table in results-plan.md.
- **MEASURED — Wave 3 source consolidation changes nothing on this corpus** (F3 ≡ F2: 3/7,
  0/6). Consolidation cannot help when the underlying ranker misses; it only de-duplicates.
- **MEASURED — F+V fusion dilutes rather than combines.** Fusing F2 (a degraded ranker) with any
  V-arm lands MRR(file) at 0.62–0.65 — below F1 alone (0.75) and below the best V-arms (0.74).
  The "merge paths" hypothesis needs a WORKING FTS ranker to fuse; on this corpus F2's misses
  poison the RRF.
- **MEASURED — F1 (status quo FTS) is stronger on file-level than the plans assumed** (6/7,
  MRR 0.75 — the best file-level MRR of all arms). The status quo's real gaps are exactly two:
  identifier queries (A7 — needs the FTS source column, Wave 2c, not query construction) and
  section-targeting (FTS cannot do it at all — needs the structure signal).
- **MEASURED — Structure-only is degenerate (predicted failure mode).** 5/7 file, 1/6 section;
  its top result for A7 is README.md with heading path "adr". Structure is a fuse-able signal,
  not a standalone ranker.
- **MEASURED — A6 is a ground-truth artifact, not a retrieval failure** (expectedKnowledge names
  ADR-0067 AND 0068; scorer accepts either; every vector arm returns 0068's erasure content at
  rank 1).
- **MEASURED — memory-safe at scale**: 6675 chunks embedded in-process in 81.5 s at ~0.8 GB RSS
  (was 50 GB OOM before the fix).
- **MEASURED — the α-sigmoid design point is moot for this corpus but the structure embedding is
  not**: the section-intent signal lives in the structure SIMILARITY, not in the adaptive α.

## Verdict

1. **The dual-vector structure signal EARNS a place** — it is the only measured mechanism that
   delivers section-targeted retrieval (6/6 vs FTS 1/6), at a cost of 2× vector storage + one
   extra embedding pass (81 s / 0.8 GB for 6675 chunks — acceptable for a memory server).
2. **The per-query α machinery does NOT earn its place** — a fixed α≈0.5 blend captures the
   measured benefit; sigmoid confidence voting is query-invariant on this corpus. If dual-vector
   ships, ship it as a fixed-weight fusion with a tunable constant, not per-query sigmoid.
3. **Plan Wave 1 (stopwords + AND semantics) as specced should NOT ship on this corpus** — it
   regresses 6/7 → 3/7. The plans' own measurement-first discipline overturns their prediction.
4. **The FTS fixes that WOULD work** (from the data): AND-with-OR-fallback on zero match, and the
   FTS source column (Wave 2c) so identifier tokens match *paths* — the structural fix, not the
   query-construction fix. Wave 3 consolidation is a no-op without a ranker that finds the files.

## What this means for the plans

- Plan C Wave 1 needs re-specification: the AND-for-short rule must be conditioned on
  zero-match fallback, and the identifier case (A7) belongs to Wave 2c (source column), where it
  is solved structurally rather than lexically.
- The exploration's dual-vector idea upgrades from "medium-term maybe" to "the only measured
  section-targeting mechanism" — but with the α-machinery stripped: fixed-weight fusion of a
  content embedding and a heading-path embedding.
- Recommended immediate path: (a) Wave 0 reproducible baseline (unchanged — still the gate);
  (b) Wave 2c source column (fixes A7 for FTS); (c) fixed-α dual-vector as an additional ranking
  signal for section-intent queries; (d) re-run this comparison on the clean Wave-0 corpus before
  shipping any of it (all numbers here are corpus-conditional).

## Limitations

1. 7-query primary set → low power; the pre-registered win rule and per-query tables mitigate.
2. Corpus 71% docs/work pollution; absolute numbers corpus-conditional. A Wave 0 clean-corpus
   re-run is required before shipping any change (plan C gate).
3. Vector arms are vector-only, FTS arms are FTS-only; neither equals today's hybrid (which is
   ≈F1 on this DB — no stored vectors).
4. No scored invariant guard (C-sources absent from corpus).
5. Heading-path parse + matcher live in the spike; a production implementation needs the
   structure metadata contract first (Wave 2 schema work — `source_file` / heading path storage).
6. Section ground truth for A5 unavailable in ranked lists (0046's Decision chunk never ranks
   top-100 in any arm) — the corpus has the section; the ranking gap is itself a finding.
7. A separate session (task `fix-baseline`) moved the exploration docs and closed this task's
   tracker entry mid-run; the tracker entry was reopened and all documents recovered — the
   pre-review copy of the comparison plan lives under `docs/research/` on `task/fix-baseline`;
   this file is the reviewed, authoritative version.
