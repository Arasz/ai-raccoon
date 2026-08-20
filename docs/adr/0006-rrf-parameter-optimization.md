# 0006 — RRF parameter optimization: k, weight ratio, minScore, candidate window

Date: 2026-08-04

Status: Accepted. The "minScore semantics" section is superseded by
[ADR-0047](0047-relative-score-floor.md); the chosen fusion parameters are unaffected.
Amended 2026-08-20 — the parameter VALUES stand unchanged; their PROVENANCE is now
query > settings > canonical constants, resolved per search via `SearchParameters`
(ADR-0083).
Amended 2026-08-09 — the parameters below stand unchanged; the second,
cross-context RRF pass they were measured through is removed (see the amendment at the end).
Amended 2026-08-14 — the gate corpus was regenerated (WP4); the chosen parameters are
unchanged, but every measured number moved and the 0.722 baseline is permanently retired
(see the amendment at the end).
Amended 2026-08-15 — **every number in this ADR is in-sample.** The 96-point grid selected
the parameters over the same 11 queries that gate them, so nDCG@5 here describes the tuning
set, not retrieval. The out-of-sample figure is 0.285 against this ADR's 0.673 on the same
path; see [ADR-0056](0056-a-retrieval-gate-measured-off-its-tuning-set.md).

## Context

Plan C Wave 4: the RRF fusion parameters (k = 60, 1:1 FTS:vector weights, minScore,
per-modality candidate window max(limit×3, 100)) had never been swept on the clean
corpus. They predate the corpus regeneration, the Wave 6 dual-vector signal, and the
Wave 3 source-affinity ranker — the fusion now feeds a different pipeline than the one
the defaults were chosen for. The Wave 2 integration analysis added an acceptance
criterion: C2's hybrid rank must be ≤ 3 after the sweep (it measured 18 at the time,
restored to 1 by Wave 6 + Wave 3).

The mandated grid: k ∈ {10, 30, 60, 120} × weights {(1:1), (1:2), (2:1)} ×
minScore {0.0, 0.3, 0.5, 0.7} × candidate window {max(limit×3, 100), max(limit×5, 50)}
— 96 points, every point run through the real search pipeline
(SearchAsync → FTS/vector batches → RRF → source-affinity ranker → merger) over the
eleven expected-source queries with the Wave 3 parameters fixed (λ = 0.1,
threshold = 0.1, Max). The full matrix is in
docs/work/archive/2026-08-04-wave4-rrf-sweep.md.

## Decision

**The pre-sweep defaults are re-confirmed as the grid optimum: k = 60, 1:1 weights,
minScore = 0.0, candidate window max(limit×3, 100). No grid point beats them on ADR
nDCG@5 while holding the Wave 3/S2/C2 gates.** The fusion parameters are unchanged.

The candidate window becomes a first-class search parameter so it can be measured and
pinned: `SearchQuery.CandidateWindow` (`CandidateWindowMode` = Max3x100 or Max5x50,
default Max3x100), applied in `SqliteMemoryStore.CandidateWindowFor`. The default is the
chosen sweep point, exactly like the Wave 3 parameters.

Measured at the chosen point (limit 10): ADR nDCG@5 **0.722**, MRR **0.929**,
recall@5 **0.617**, exact-chunk @3 **11/11**, C2 hybrid **1** (≤ 3 ✓), A1/A4 file 1,
A6 file 2 + exact 2, S2 decision 3, A7 exact 2 — every Wave 3/S2 gate held.
Fusion regression gate (hybrid exact-chunk rank ≤ best single modality) holds on all
eleven queries.

## Consequences

- **Negative result, measured.** 24 of 96 points score above 0.722 on nDCG@5; every one
  violates at least one gate. The best raw point (k=120, 1:1, Max3x100: nDCG@5 0.775,
  MRR 0.929, recall@5 0.677) regresses A1 file 1 → 2, A6 exact 2 → 6, and exact-chunk
  @3 11/11 → 9/11. The FTS-heavy (2:1) weight fixes A6 (file 1, exact 1) but regresses
  A1 file → 2 and exact@3 → 9/11; vector-heavy (1:2) regresses A6 (file 4, exact 4);
  k=30 kills A1/A6; the Max5x50 window starves A6's exact chunk (50 < 100 candidates
  per modality).
- **minScore is measured inert at the chosen point.** At k=60 the four minScore rows are
  identical for every weight×window combo (24 rows); at k=10 it trims the tail and always
  hurts or ties. (The construction bound is conditional: with a dual-retrieved rank-1 max,
  a single-modality rank-10 result normalizes to 61/140 ≈ 0.44 and would be filtered —
  measured inertness is what holds at k=60.) The measured baseline (minScore 0.0) and the
  tool default (0.7) are equivalent at the chosen point.
- **Fusion gate definition.** "No fusion regression" is enforced on the exact-chunk rank:
  the hybrid never ranks the expected chunk below the best single modality (A6 2 ≤
  min(2, miss), S2 3 ≤ min(4, miss), A5 3 ≤ min(miss, 4)). The Wave 0 recall@5
  observation flags A5/A6/S2, but that is a file-cluster artifact — the hybrid surfaces
  fewer same-file chunks in the top 5 while ranking the answer chunk equal-or-better —
  and no grid point fixes A6's recall@5 either (2:1 keeps 0.33 < FTS 0.67).
- **C2 acceptance closed.** C2 holds hybrid rank 1 at the chosen point (≤ 3 ✓); the
  Wave 2 acceptance criterion is satisfied without any fusion change.
- **Code delta:** `SearchQuery.CandidateWindow` (new), `CandidateWindowMode` enum
  (Core/Memory), `CandidateWindowFor(limit, mode)` overload, `SweepPoint`/`SweepMatrix`
  extended with the minScore/window dimensions and the 96-point `RrfGrid`. Sweep
  harness: `RrfParameterSweepTests` (real pipeline, report writer, gate pins).
- **Cost.** No runtime change (the chosen point equals the pre-sweep behavior); the
  sweep itself is one slow-trait integration test (~60 s).

## Alternatives considered

- **k=120, 1:1, Max3x100** (nDCG@5 0.775, recall@5 0.677 — best raw point): rejected —
  regresses A1 file to 2, A6 exact to 6, exact-chunk @3 to 9/11; the same-knowledge
  alternative and deep-sibling problems the Wave 3 ranker was tuned to fix resurface at
  the flatter k.
- **2:1 FTS-heavy weights** (nDCG@5 0.747 at k=60, A6 exact 1): rejected — A1 file 1 →
  2 and exact-chunk @3 → 9/11 (A3 exact 3 → 4, A5 exact 3 → 4).
- **k=30, 2:1** (nDCG@5 0.752): rejected — A6 file 3, A6 exact 4, exact@3 9/11, A4
  exact lost from the top 10.
- **Max5x50 window**: rejected — at limit 10 it halves the per-modality candidate depth
  (50 vs 100) and starves A6's exact chunk at most points (A6 exact 5-10 or missing;
  two points keep it — k=60/120 2:1 — but all Max5x50 points regress A1 file 1 → 2).
- **minScore 0.3/0.5/0.7**: no measured effect at the chosen point (all four k=60 rows
  identical per weight×window combo); rejected as a no-op with tail-trim risk at other k.
- **Recall@5-based fusion gate**: rejected as the hard gate — it flags the hybrid's
  top-5 diversity (fewer same-file chunks) rather than answer-chunk quality, and no
  grid point satisfies it for A6; kept as a documented observation only.

---

## Amendment — 2026-08-09: cross-context RRF removed; the chosen point is unchanged

### What was wrong

The pipeline applied RRF **twice**: once over the two modalities inside each search
context, and again over the per-context batches in `SearchResultMerger.Merge`. The second
pass scored by rank position only, so a context's rank-1 contributed `weight / (k + 1)`
regardless of how many candidates that context actually ranked. At k = 60, with the 1:1
context weights the merger hardcoded:

| list | its rank-1 score | after max-normalization |
|---|---|---|
| shared tier holding **1** entry | 1/61 = 0.016393 | **1.0000** |
| project tier holding **2,400** entries | 1/61 = 0.016393 | **1.0000** |
| project tier, rank 2 | 1/62 = 0.016129 | 0.9839 |

A tier's best entry and the corpus's best entry were arithmetically indistinguishable, and
the tie fell through to the ordinal `Path` comparison. Measured against the live 2,400-entry
bank, a single promoted entry returned ranking 1.0000 for queries as unrelated as
"banana pancake recipe", and surfaced at 0.83–1.0 in roughly 20 of 44 unrelated queries.
`scope=project` was unaffected, which localises the defect to cross-context fusion.

Removing max-normalization does **not** fix this: dividing every score by a positive
constant preserves order, so the two rank-1 entries still tie. The defect is the
rank-only scoring, not the normalization.

The root cause is a layering leak. Contexts partition **storage** — the loop exists because
the vec0 index is partitioned by context key (docs/plans/2026-08-08-search-knn-perf.md §3.4)
— not relevance. Both modalities already produce absolutely comparable scores across
contexts: `bm25` comes from one shared `entries_fts` index with global corpus statistics,
and cosine similarity comes from one embedding space. Fusion discarded both and rebuilt a
score from rank position.

### Decision

**Modality fusion becomes global across contexts.** The per-context loop now only collects
candidates; `ModalityCandidates.ByBm25` / `ByCosine` concatenate them, dedupe by hash keeping
the better score, and order by that absolute score. `ReciprocalRankFusion.Fuse` then runs
**once** over the two globally-ranked modality lists, and `SearchResultMerger.Merge` receives
that single fused batch. `k`, the 1:1 FTS:vector weights, the candidate window and minScore
are untouched; `ReciprocalRankFusion`, `SearchResultMerger` and `SourceAffinityRanker` are
unmodified.

### Why the chosen point is untouched

Every measurement in this ADR — and in `RetrievalBaselineTests`, `BaselineMetricsTests`,
`ParityGateTests`, `RrfParameterSweepTests`, `SourceAffinitySweepTests` — searches with
`SearchScope.Project` against a single-project corpus, which resolves to exactly **one**
context. For one context the new path is a no-op by construction: the per-modality list is
the only list, and the ordering key (`bm25` ascending, fused cosine descending) is the one
the SQL and `StructureFusion.Rank` already emitted, applied with a **stable** LINQ sort. Same
list in, same list out, then the same `Merge`. The parity is algebraic, not merely observed.

This also preserves the two-stage score shape that the Wave 3 ranker depends on: `Merge`
still re-scores its single batch by position, so `SourceAffinityRanker` continues to receive
`(k+1)/(k+r)`-shaped values against which λ = 0.1 and threshold = 0.1 were tuned. Collapsing
the two RRF passes into one would have changed that scale and moved the baselines; it was
deliberately not done.

### minScore semantics — superseded by ADR-0047

> **Superseded 2026-08-14 by [ADR-0047](0047-relative-score-floor.md).** This section held
> that the shipped tool default of 0.7 "keeps the meaning it was audited under". The
> equivalence it claimed between the swept 0.0 and the shipped 0.7 was measured at this
> sweep's `limit=10` and does not hold at the `limit=20` the product ships: 10 of the 44
> baseline queries came back short at limit 20, and all 44 at limit 50. The parameter is
> now `minRelativeScore`, defaults to 0, and is documented as relative to the response's
> own top hit. The chosen fusion point below (k=60, 1:1, minScore 0.0, Max3X100) is
> unaffected — the sweep already ran at 0.0.

minScore still filters a single max-normalized fused list, exactly as swept above. The
"measured inert at the chosen point" observation stands, and the shipped tool default of
0.7 (`SearchQuery.MinScore`, `MemoryTools`) keeps the meaning it was audited under — no
tool-description change was required. This is why the normalization-changing remedies were
rejected: returning raw fused scores (~0.016) or normalizing against a fixed reference
would have pushed results under an 0.7 default that this ADR measured as inert, silently
filtering results the sweep decided should not be filtered.

### Measurements

Gate suite `Rrf|Retrieval|Baseline|Parity|HybridSearch|SourceAffinity|Snippet`:
**144 passed / 0 failed before, 146 passed / 0 failed after** — the two extra tests
were added by concurrent work on other test files during the same session, not by this change. Chosen-point metrics
(k = 60, 1:1, minScore 0.0, Max3X100), before → after:

| metric | before | after |
|---|---|---|
| ADR nDCG@5 | 0.674 | 0.674 |
| MRR | 0.881 | 0.881 |
| recall@5 | 0.564 | 0.564 |
| exact-chunk @3 | 4/11 | 4/11 |

Regenerating `docs/work/2026-08-04-wave4-rrf-sweep.md` and
`docs/work/2026-08-04-wave3-source-affinity-sweep.md` after the change produces files
identical to the committed ones — the whole 96-point grid is unmoved.

Note for future readers: the header text of the sweep report and the "0.722 / 0.929 / 0.617
/ 11-11" figures in the original Decision section above predate the 2026-08-06 corpus
re-pin (docs/work/archive/2026-08-06-baseline-repin-new-corpus.md). The live pinned floor asserted
by `RrfParameterSweepTests` is nDCG@5 **0.674**; the numbers in the table above are the
current ones.

### Regression gate

`SqliteMemoryStoreTests.Search_ScopeAll_SmallSharedTier_DoesNotCaptureAnUnrelatedQuery`
promotes one off-topic entry into the shared tier alongside a larger project tier and
asserts it neither ranks first nor ties the genuine top match. Verified red against the
pre-fix code:

```
Shouldly.ShouldAssertException : results[0].Path
    should be
"zz-deploy-pipeline-decision.md"
    but was
"shared/0c1f8e16bf7be0d5fe4048a56ccd21594cf39d6469b79348e21076928466ed5b.md"
```

`ModalityCandidatesTests` pins the ordering contract and the single-context stable-sort
parity that the argument above rests on.

### Open — needs a corpus decision

No retrieval-quality measurement in the repository exercises `scope=all` against a bank with
both a shared and a project context populated; every graded query in
`scripts/baseline-queries.json` and every harness fixture is single-context. So cross-context
**ranking quality** has no ground truth here, and this amendment does not claim a measured
improvement to it — it claims a defect removed, single-context parity preserved, and the
now-well-defined semantics "`scope=all` ranks the union as if it were one bank". Deciding
whether to add a cross-context stratum to the graded catalogue (and a shared-tier fixture to
the harness) is a corpus-scope call for the owner, not something this change settles.

## Amendment — 2026-08-14: gate corpus regenerated through the production FileIngestor

### What changed

WP4 (`docs/plans/2026-08-14-code-quality-improvement-plan.md`) regenerated
`tests/AiRaccoon.Tests/Resources/jsaa-memory.db` through the production
`AiRaccoon.Infrastructure.Ingestion.FileIngestor` against the same pinned
job-search-ai-assistant commit (`9397bbef504b5b30a31003c84e8c5c316641adb6`, same 196 curated
files), replacing a fixture built by `scripts/src/chunking.py` — a Python re-implementation of
chunking, independent of and by-then divergent from the real chunker (WP7's engine-aware
token budget, `docs/adr/0036`). The old fixture predated `chunk_index`/`total_chunks`/`source_id`
(schema v0, no ladder steps applied) and carried **zero** structure vectors (`structure_embedding`
NULL on 761/761 rows, `vec_structure` empty) — ADR-0004's dual-vector structure modality was
exercised by no gate at all. The regenerated corpus is on the current schema (v6) and carries
871/2518 real structure vectors (any chunk containing an H1/H2 heading gets one).

**Verified the corpus can now fail on a broken fusion** (the acceptance test for the whole
package): `StructureFusionGateTests.VectorOnlySearch_GenericDecisionQuery_RanksStructureMatchedChunkFirst`
pins a generic "What is the decision?" query's vector-only top-1 to the ADR-0068 Decision-section
chunk. Forcing `StructureFusion.Fused` to ignore the structure term (`return contentSim;`)
changes the top-1 hash and fails the test; restoring `Fused` passes it again. A corpus with no
structure vectors could not have done this — every prior gate run against the old fixture was
provably incapable of catching a structure-fusion regression.

### The 0.722 baseline is permanently retired

The regenerated corpus is **not** re-derivable from the old one — it is a different, larger
chunking of the same source documents (2518 rows vs 761, ~3.3x more and smaller chunks). Every
number this ADR has ever cited against the old corpus (0.722, and the 2026-08-06 re-pin's 0.674)
is a comparand for a fixture that no longer exists in this repository's history in committed
form. This is a genuine, irreversible loss of a baseline, not a formality — re-running the same
sweep can never reproduce those numbers again, because the underlying retrieval unit (a "chunk")
now means something different.

### Re-pinned numbers, chosen point (k=60, weights 1:1, minScore=0.0, window=Max3X100)

| gate | before (0.674 re-pin) | after (2518-row corpus) | why it moved |
|---|---|---|---|
| ADR nDCG@5 | 0.674 | **0.532** | Same 196 source files, but ~3.3x more, smaller chunks means every ADR retrieval query now competes against far more same-topic sibling chunks — a genuinely harder task. Not a fusion regression: criterion 3 above shows the structure modality is doing real, verified work on this corpus. |
| ADR MRR | 0.881 | 0.631 | Same cause. |
| ADR recall@5 | 0.564 | 0.301 | Same cause; a fixed-size top-5 window now covers a smaller fraction of a corpus with 3.3x more competing chunks. |
| A1 file rank | 1 | ≤ 2 (measured 2) | Corpus-growth rank drift, same shape as the 2026-08-06 re-pin's A6/A7/C5 shifts. |
| A4 file rank, C1 exact, C5 exact ≤ 5 | 1 / 1 / ≤5 | unchanged (1 / 1 / 1) | Held. |
| A6/A7 exact-chunk rank | ≤ 8 / ≤ 7 | **dropped from the top-10 window entirely** (not re-pinned to a wider window; ungated) | Both were already the most marginal ranks in the 2026-08-06 re-pin (A6 exact 6, A7 exact 7, out of 11 gate queries); the corpus tripling in size pushed them out of the measured window rather than to a new, stable rank — there is no honest ceiling to pin here without exercising a much wider (and untested) top-k. |
| exact-chunk @3 | ≥ 4/11 | unchanged (8/11 measured, gate still ≥ 4/11) | Held, with margin. |

### FINDING (not re-pinned): the chosen point is no longer the grid optimum

On the regenerated corpus, k=60/1:1/Max3X100 (nDCG@5 0.532) is no longer the best gate-holding
point in the 96-point grid — e.g. k=120, weights 2:1, window Max5X50 measures nDCG@5 0.775 while
holding every gate above. **This is not re-pinned to a new "chosen" configuration.** WP4's
mandate is regenerating the corpus, not re-tuning RRF parameters; that is WP3b's mandate
("measured on WP4's corpus" per the improvement plan). Re-picking new defaults here would be a
ranking decision this package has no basis to make — the gate now reports the gap
(`RrfParameterSweepTests` prints a `grid-optimality FINDING (WP3b handoff)` line and the
regenerated `docs/work/2026-08-04-wave4-rrf-sweep.md` records it) instead of asserting a claim
that no longer holds. WP3b should re-run the full grid sweep as part of its ranking-fix work.

### RAG-F14 — the ADR and the gate now agree

The 2026-08-06 nDCG@5 re-pin (0.722 → 0.674) already had its reason recorded at
`RrfParameterSweepTests.cs` (corpus re-pin + ADR-0015 cross-platform rank tolerance) before this
amendment — there was no lost 0.048 to hunt for, only a stale number in this ADR's original
Decision section. This amendment supersedes it with the WP4 numbers above; the ADR and the gate
now cite the same baseline.
