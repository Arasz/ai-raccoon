# 0005 — Source-affinity ranking: adjacent-chunk boost, consolidation, document-first

Date: 2026-08-04

Status: Accepted

## Context

Plan C Wave 6's integration amendment moved S2's acceptance to Wave 3: "What does
ADR-0011 decide?" finds the Decision chunk at rank 5, behind four of its own file's
siblings (metadata header, context, consequences). The hybrid ranker has no notion of
document identity — chunks of the same source compete as strangers, and the top-5 can be
monopolized by one file while a section the query actually targets ranks mid-list. The
same mechanism was expected to help A6 ("How does the project handle data erasure?",
expected ADR-0067 file at rank 2, exact chunk outside the top 10) without regressing the
single-chunk invariants (C1/C2/C5 at hybrid rank 1).

The full sweep matrix and per-point gate numbers are in
docs/work/2026-08-04-wave3-source-affinity-sweep.md.

## Decision

**Wave 3 ships a source-affinity pass over the fused candidate list, applied before
minScore/limit truncation.** Three mechanisms, one threshold parameter:

1. **Adjacent-chunk boost.** A chunk at index N±1 of the same source gains
   `λ × (number of counted siblings)`. A sibling counts only when its raw RRF score is at
   least `maxRaw − consolidationThreshold` — the sibling-visibility floor. The floor is
   what keeps the single-chunk invariants at rank 1: without it (threshold = off),
   deep same-file siblings (e.g. architecture.md#1 at fused rank 18) boost their file's
   top chunk above the invariant at every λ (measured: C1/C2/C5 fall to ranks 2–10+).
2. **Source consolidation.** Per source, the best-scoring chunk is the representative; an
   adjacent sibling merges into it (is dropped) when the boosted score gap
   `best − sibling ≥ consolidationThreshold`. A strong adjacent sibling (gap < threshold)
   stays a separate result — required for S2, where the Decision chunk is adjacent to the
   file's best chunk and must survive.
3. **Document-first ranking.** Each source's document score = max(boosted chunk scores)
   (formula sweep: sum is measured equivalent on every grid point); it is the secondary
   sort key after the boosted score, replacing the bare path tie-break.

Parameters live on `SearchQuery` (`SourceLambda`, `ConsolidationThreshold`,
`DocScoreFormula`); the defaults are the chosen sweep point: **λ = 0.1, threshold = 0.1,
Max**. `λ = 0` short-circuits the pass (identity), so the pre-Wave-3 ranker is one
parameter away.

## Consequences

Measured on the committed jsaa corpus (SourceAffinitySweepTests, limit 10, k=60, 1:1):

- **S2 Decision chunk 5 → 3** (the Wave 3 gate; ≤ 3). File rank holds at 1.
- **A6 file 2 → 2** (≤ 3 held), **exact chunk: miss → rank 2** (the ADR-0067 Decision
  chunk surfaces in the top 5 for the first time).
- **A1 file 2 → 1, A4 file 2 → 1** (gate ≤ 2; the same-knowledge alternatives now rank
  behind the expected files), **A7 exact 4 → 2, A3 exact 3 → 1**.
- **Invariants C1/C2/C5 hold rank 1.**
- **ADR nDCG@5 0.650 → 0.722, MRR 0.786 → 0.929, recall@5 0.581 → 0.617** — beats the
  Wave 2 state (0.674) and the Wave 6 merged state (0.650).
- **Sweep outcome.** All-gates points: (λ=0.1, thr=0.1) nDCG@5 0.722, (λ=0.1, thr=0.15)
  0.714, (λ=0.2, thr=0.05) 0.666. λ=0.1/thr=0.1 chosen: best nDCG@5 and MRR among
  gate-passing points, and the only one surfacing the A6 exact chunk at rank 2.
  Max and Sum are identical on every point (measured) — Max kept as the simpler formula.
  Consolidation removes no top-10 result for the gate queries at the chosen point; at
  threshold 0.15 it would merge A7's rank-3 chunk, lowering nDCG@5.
- **Cost.** One extra sort + per-source grouping over the fused candidate list (~200
  items); the parity gate's p95 latency budget holds (full suite green, parity test asserts
  p95 ≤ 1000 ms).
- **Follow-ups.** Wave 4's RRF sweep now operates on top of the source-affinity ranking;
  the sweep matrix shape (λ × threshold) extends to it directly. Shared-scope rows have
  no source_file and participate as singletons (plan §6 Q6 — unchanged).

## Alternatives considered

- **Full-list boost without a visibility floor** — measured and rejected: breaks
  C1/C2/C5 at every λ (deep same-file siblings overtake the invariants).
- **Boost restricted to the final top-L window** — measured and rejected: A6's sibling
  pairs straddle rank 10 (its expected chunks sit at fused ranks 2/5 with siblings at
  11–13), so the 0068 cluster out-boosts the expected file and A6's file rank regresses
  to 6.
- **Document score as the primary sort key** — measured and rejected: the top-5 becomes
  mono-file (A6's rank-1 file fills it, pushing the expected file to rank 8).
- **Boost capped at the top raw score** — rejected by construction: S2's five siblings
  all cap, restoring the original order (Decision chunk back at 5).
- **BM25 length normalization** — deprioritized per the plan (A7 already hybrid rank 1;
  no length-attributable regression surfaced).
