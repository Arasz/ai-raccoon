# Dual-Vector α vs Plan Fixes — A/B Comparison Plan

**Date**: 2026-08-04 · **Task**: retrieval-improvement-cont
**Question**: Does the dual-vector per-query-α approach (exploration) earn a place alongside the
plans' FTS-side fixes (Wave 1 query construction, Wave 3 source consolidation), or do the cheap
fixes alone carry the day? Do they combine?

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
| V2–V5 | Dual-vector sigmoid α, T ∈ {0.1, 0.5, 0.8, 1.0} | dual-vector prototype | α = sigmoid(confidence × T); confidence = max−mean of structure sims; score = α·content + (1−α)·structure |
| V6 | Vector structure-only (α=0.0) | dual-vector prototype | diagnostic: is the structure signal meaningful alone? |
| F1 | FTS5 BM25, current normalizer (OR-join) | FTS harness | status quo — real `bm25(entries_fts)` over `entries_fts MATCH` |
| F2 | FTS5 BM25, fixed normalizer | FTS harness | plan Wave 1: stopword strip + `ADR-\d+` → `adr AND <n>` + AND-for-short |
| F3 | F2 + source consolidation | FTS harness | plan Wave 3: collapse siblings per path, keep best, re-rank |
| F+V | F2 fused with best V-arm (RRF k=60, 1:1) | post-hoc | the cross-reference's "merge paths" recommendation |

Plus: **BM25 length analysis** — for A7 ("What is ADR-0070 about?"), compare BM25 rank vs
length-unaware term-frequency rank on the same matched set, to quantify the length-penalty claim.

### Ground truth & metrics (identical across arms)

- Matcher: `expectedSource` minus `#fragment`, last segment after `:` or `/` → e.g. `0011-frontend-chassis-stack.md`;
  hit when any top-K result path ends with `/` + that name (case-insensitive).
- **file-level hit@5**: any chunk of the expected file in top-5 (A1–A7).
- **section-level hit@5**: a chunk from the expected file whose heading-path last segment equals
  the fragment (`decision`) — both harnesses use the SAME heading-path algorithm (per-file heading
  stack from chunk contents; assign most-recent heading at chunk start; normalize trailing `:`).
- **MRR** over A1–A7 (file-level); mean α per V-arm; coverage /35.
- Deterministic: full corpus, no random sampling, no `Guid.NewGuid()` shuffles.

## Hard constraints

1. **Memory**: machine has 24 GB. Both runs MUST be memory-bounded — RSS watchdog kills the run
   above 6 GB. The previous run was OOM-killed at 50 GB; that mistake is not repeated.
2. **Spike only**: production code untouched. All work in the prototype worktree
   `/Users/arasz/RiderProjects/ai-raccoon-prototype` (branch `prototype/dual-vector-alpha`).
3. **Comparable output**: `results-dual-vector.md` + `results-plan.md` with identical table
   schemas so numbers line up side by side.

## Execution

- Orchestrator (in-session): fix + run V1–V6 in `tools/AiRaccoon.DualVectorPrototype`.
- Subagent (dotnet-engineer): build + run F1–F3 in `tools/AiRaccoon.FtsPlanPrototype`
  (console app; real FTS5 via SQL against `entries_fts`; normalizer variants; consolidation;
  length analysis).
- Integrate: side-by-side table → verdict (does per-query α carry its weight? do the fixes
  combine?) → findings doc in evidence-first-research format → commit docs on the task branch →
  merge.

## Known confound (documented, not fixed)

The V-arms are vector-only; the F-arms are FTS-only. Neither equals "the current hybrid"
(the DB has no vectors, so today's system ≈ F1). The comparison is deliberately between the
two *candidate* improvements as isolated mechanisms — the F+V arm shows what combining them
buys. Absolute numbers are corpus-conditional (71% pollution); the verdict rests on relative
differences between arms.
