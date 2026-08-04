# Dual-Vector α vs Plan Fixes — A/B Comparison Plan (reviewed)

**Date**: 2026-08-04 · **Task**: retrieval-improvement-cont
**Question**: Does the dual-vector per-query-α approach (exploration) earn a place alongside the
plans' FTS-side fixes (Wave 1 query construction, Wave 3 source consolidation), or do the cheap
fixes alone carry the day? Do they combine?

> Reviewed 2026-08-04 by architect subagent (deleg_792e0182): APPROVE-WITH-CHANGES — 9 findings
> folded in below (shared scorer, section-level primary, fixed-α control, no best-of fusion,
> pre-registered win rule, cost columns, no-invariant-guard statement, stratification).

## Design

Both approaches measured on the **same corpus, same queries, same ground truth, same metrics** —
any other design cannot answer the question.

### Corpus (held fixed — do NOT "fix" for this experiment)

`tests/AiRaccoon.Tests/Resources/jsaa-memory.db` — 6675 chunks:
- 1056 chunks from `docs/adr/` (16%), 4723 from `docs/work/` (71%), rest CHANGELOG/flows/…
- No stored embeddings — each harness computes its own; irrelevant to the comparison.
- Known limitations documented, not fixed (Wave 0 reproducible-baseline is a separate workstream):
  - C1/C2/C5 expected sources (`ai-badger:invariants/…`) are **absent** from this corpus → max
    achievable primary score is 7/10, not 10/10.
  - H1–H3 negative tests will fail due to pollution — reported as corpus-honesty evidence, not scored.

### Queries

All 35 from `scripts/baseline-queries.json`.
- **Primary metric set: A1–A7** — the 7 reachable expected-source queries, all with a
  `#decision` section fragment (section-targeted ground truth exists).
- **Secondary**: coverage (queries with ≥1 result / 35); hygiene H1–H3 reported as evidence.

### Arms

| # | Arm | Where | Mechanism |
|---|-----|-------|-----------|
| V1 | Vector content-only (α=1.0) | dual-vector prototype | cosine(query, content embedding) — the reference the exploration must beat |
| V6 | Vector structure-only (α=0.0) | dual-vector prototype | diagnostic: is the structure signal meaningful alone? |
| V7 | Fixed α=0.5 (no sigmoid) | dual-vector prototype | control: attributes any sigmoid-arm win to per-query α vs merely adding structure |
| V2–V5 | Dual-vector sigmoid α, T ∈ {0.1, 0.5, 0.8, 1.0} | dual-vector prototype | α = sigmoid(confidence × T); confidence = max−mean of structure sims; score = α·content + (1−α)·structure |
| F1 | FTS5 BM25, current normalizer (OR-join) | FTS harness | status quo — real `bm25(entries_fts)` over `entries_fts MATCH` |
| F2 | FTS5 BM25, fixed normalizer | FTS harness | plan Wave 1: stopword strip + `ADR-\d+` → `adr AND <n>` + AND-for-short + bigram phrases on long OR queries |
| F3 | F2 + source consolidation | FTS harness | plan Wave 3: collapse siblings per path, keep best, re-rank (pre-merge AND post-merge lists emitted) |
| F+V | F2 RRF-fused with **every** V-arm (k=60, 1:1) | shared scorer | the cross-reference's "merge paths" recommendation — no best-of selection, all V-arms reported |

Plus: **BM25 length analysis** — for A7 and A1, compare BM25 rank vs length-unaware
term-frequency rank on the same matched set, to quantify the length-penalty claim.

### Ground truth & metrics — computed ONCE in a shared scorer

- Both harnesses emit ONLY raw ranked lists: `results-dual-vector.json` / `results-plan.json`,
  schema `{ corpus, wallSeconds, arms, queries: [ { id, expectedSource,
  arms: { <arm>: [ { rank, hash, path, headingPath, score } × top-100 ] } } ] }`.
- `scripts/compare-harnesses.py` computes ALL metrics from these (single implementation, no
  per-harness drift): matcher (fragment-stripped last path segment), file-level hit@5,
  section-level hit@5 (heading-path last segment == fragment, case-insensitive), MRR
  (file + section), per-query ranks A1–A7, identifier vs concept group split (A7 vs A1–A6).
- **Section-level is the PRIMARY verdict metric** (all A-queries target `#decision`; file-level
  hit@5 can pass on the wrong section). Section ground truth is pre-flighted per query: a query
  whose expected file never shows a `decision` heading path in any ranked list is reported as
  section-ground-truth-missing, not assumed.
- **Pre-registered win threshold** (7 queries = low power): an arm BEATS content-only iff ≥2
  query flips on section-level hit@5 OR MRR(file) delta ≥ 0.1 vs content-only; below = tie.
  Per-query rank tables are published so single-query flips are visible, not averaged away.
- **No scored invariant guard**: C1/C2/C5 sources are absent from this corpus and C3/C4/C6 have
  null expected sources — stated explicitly; the invariant regression question is out of scope
  for this spike (needs the Wave 0 corpus).
- Coverage /35 and H1–H3 are reported but explicitly labeled non-evidential (already 100%
  coverage FTS-only; pollution guarantees H-failures).

## Hard constraints

1. **Memory**: machine has 24 GB. Both runs MUST be memory-bounded — RSS watchdog kills the run
   above 6 GB (`scripts/run-with-memcap.sh 6 <apphost>`). The previous run was OOM-killed at
   50 GB; that mistake is not repeated. Peak RSS + wall time are reported per run (cost side of
   the comparison; dual-vector also costs 2× vector storage, noted in the verdict).
2. **Spike only**: production code untouched. All work in the prototype worktree
   `/Users/arasz/RiderProjects/ai-raccoon-prototype` (branch `prototype/dual-vector-alpha`).
3. **Comparable output**: both harnesses emit the SAME raw-JSON schema; one shared scorer
   produces the comparison tables.
4. **Deterministic**: full corpus, no random sampling, no `Guid.NewGuid()` shuffles.

## Execution

- Orchestrator (in-session): fix + run V1–V7 in `tools/AiRaccoon.DualVectorPrototype`.
- Subagent (dotnet-engineer): build + run F1–F3 in `tools/AiRaccoon.FtsPlanPrototype`
  (console app; real FTS5 via SQL against `entries_fts`; normalizer variants; consolidation;
  length analysis).
- Integrate: shared scorer → side-by-side table → verdict (does per-query α carry its weight?
  do the fixes combine?) → findings doc in evidence-first-research format → commit docs on the
  task branch → merge.

## Known confound (documented, not fixed)

The V-arms are vector-only; the F-arms are FTS-only. Neither equals "the current hybrid"
(the DB has no vectors, so today's system ≈ F1). The comparison is deliberately between the
two *candidate* improvements as isolated mechanisms — the F+V arm shows what combining them
buys. Absolute numbers are corpus-conditional (71% pollution); the verdict rests on relative
differences between arms.
