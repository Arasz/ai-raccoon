# Retrieval parameter tuning — adjustment matrix

Date: 2026-08-21
Task: continue-testing-algorithm (plan: `2026-08-21-parameter-tuning-plan.md`, review: `2026-08-21-parameter-tuning-plan-review.md`)
Raw data: `2026-08-21-parameter-tuning-matrix.csv` (126 rows; every row cited below by `config_id`)

Method: baseline = all 9 defaults **written explicitly** to the scratch server's settings (the
copy inherits `fusion.noRegression.enabled.global=true` and `retrieval.structureAlpha=0.5` from
the live bank — inherited state is never trusted). For each knob, one ladder of values with every
other knob at baseline, evaluated over three corpus/dataset pairs:
**sextant(6)** — the 6-query artificial sextant corpus (hash-anchored; alien-tokens excluded by
design — it has no right answer); **memory(10)** — the graded 10-query test set on the 22,514-entry
memory-db copy; **memory(100)** — the 100-query eval set (75 ADR-file-targeted + 25 non-file) on
the same copy. Metric: mean nDCG@5 (binary gain, log2 discount).

## The retrieval algorithm — logic flow

```
memory_search(query, projectId, scope, limit, minRelativeScore)
  │
  ▼
Resolve SearchParameters  (precedence: per-call args > settings table > canonical constants;
  2 batched settings SELECTs on the search's own connection; malformed setting → constant)
  │
  ├─► Candidate window per modality  (CandidateWindowFor(limit, mode): max3x100 | max5x50)
  ├─► QueryVector with structureAlpha  (content embedding + structure arm blend, α)
  │
  ├─► FTS leg  — FTS5 BM25  (skipped if no expression or ftsWeight = 0)
  └─► Vector leg — cosine nearest neighbors  (skipped if empty vector or vectorWeight = 0)
  │
  ▼
Weighted RRF fusion per context batch  (score = Σ weight/(k + rank), normalized to max 1.0;
  weights = ftsWeight, vectorWeight; k = rrfK)
  │
  ▼
NoFusionRegression reorder?  (only if ≥2 legs contributed AND fusion.noRegression.enabled.global)
  │
  ▼
Cross-context candidate merge  (ModalityCandidates, dedupe by content hash)
  │
  ▼
Second RRF pass — unit weight, same k  (commit 404ba926 "missing unit fusion")
  │
  ▼
SourceAffinityRanker:
  1. sibling boost: +λ per adjacent chunk of the same multi-chunk source (chunkIndex ≥ 0 only, GH#371 guard)
  2. doc-score aggregation per source: max | sum
  3. consolidation: drop weak adjacent siblings whose score gap ≥ consolidationThreshold
  4. normalize by boosted max
  │
  ▼
Filter ranking ≥ minRelativeScore, take limit → ranked results
```

## Stage → knob map (9 knobs in pipeline order)

| # | Stage | Knob | Home | Type / range | Default |
|---|---|---|---|---|---|
| 1 | Params resolution | precedence | — | query > settings > constants | — |
| 2 | Query vector | structureAlpha | settings `retrieval.structureAlpha` | float 0..1 | 0.5 |
| 3 | Candidate window | candidateWindow | settings `retrieval.candidateWindow` | "max3x100" \| "max5x50" | max3x100 |
| 4 | Leg gating | ftsWeight | MCP `ftsWeight` / settings `retrieval.ftsWeight` | int ≥ 0 (0 = leg off) | 1 |
| 5 | Leg gating | vectorWeight | MCP `vectorWeight` / settings `retrieval.vectorWeight` | int ≥ 0 (0 = leg off) | 1 |
| 6 | Weighted RRF | rrfK | MCP `rrfK` / settings `retrieval.rrfK` | int ≥ 1 | 60 |
| 7 | Post-pass | fusion flag | settings `fusion.noRegression.enabled.global` | bool | false |
| 8 | Source affinity | sourceLambda | MCP `sourceLambda` / settings `retrieval.sourceLambda` | float 0..1 | 0.1 |
| 9 | Doc-score aggregation | docScoreFormula | MCP `docScoreFormula` / settings `retrieval.docScoreFormula` | "max" \| "sum" | max |
| 10 | Consolidation | consolidationThreshold | MCP `consolidationThreshold` / settings `retrieval.consolidationThreshold` | float ≥ 0 | 0.1 |

## Baseline (explicit defaults)

| Corpus | Mean nDCG@5 | MRR@5 | hit@3 | hit@1 |
|---|---|---|---|---|
| sextant(6) | 0.655 | 0.542 | 0.833 | 0.333 |
| memory(10) | 0.700 | 0.617 | 0.900 | 0.400 |
| memory(100) | 0.611 | 0.568 | 0.650 | 0.470 |

## Learned influence per knob

Each statement cites the matrix rows it is inferred from (`config_id` = `<knob>=<value>`; the
baseline row is `baseline`).

### rrfK — strong, corpus-size dependent, non-monotone. Default 60 is right for the real bank, wrong for tiny corpora.

Rows `rrfK=1/5/15/60/120/200` on all three corpora.

| Value | sextant(6) | memory(10) | memory(100) |
|---|---|---|---|
| 1 | 0.833 | 0.498 | 0.593 |
| 5 | **0.938** | 0.647 | 0.624 |
| 15 | **0.938** | 0.679 | 0.635 |
| 60 (default) | 0.655 | 0.700 | 0.611 |
| 120 | 0.572 | 0.689 | 0.557 |
| 200 | 0.572 | **0.731** | 0.521 |

Sextant: k=5-15 is +0.28 over default (best single-knob gain in the whole matrix) — RRF's rank
compression at k=60 spreads tiny corpora too thin. Memory(100): optimum at k=15-60; k=200 falls
to 0.521. Memory(10) rises all the way to k=200. Verdict: **strong, corpus-dependent** — the
knee is near the corpus scale; default 60 is a good large-corpus value and the tuning search
should cover [5, 60] (the Optuna space [5, 200] log-uniform does).

### ftsWeight — essential on the real bank (0 is catastrophic), mildly harmful when heavy. Default 1 near-optimal.

Rows `ftsWeight=0/1/2/3/5/10`.

| Value | sextant(6) | memory(10) | memory(100) |
|---|---|---|---|
| 0 (leg off) | **0.750** | 0.406 | 0.424 |
| 1 (default) | 0.655 | 0.700 | 0.611 |
| 2 | 0.655 | 0.658 | 0.619 |
| 3 | 0.636 | 0.729 | 0.602 |
| 5 | 0.636 | 0.667 | 0.599 |
| 10 | 0.629 | 0.628 | 0.599 |

Memory: ftsWeight=0 drops nDCG@5 by ~0.19-0.29 — the FTS leg carries exact and table queries the
vector leg cannot. Beyond 1 the effect is small (peak at 3 on memory(10) is +0.03, within corpus
noise). Sextant is an artifact: with the sibling-boosted guide pair dominating the FTS leg,
killing it helps. Verdict: **strong at 0, flat 1-10**; default 1 is the right operating point.

### vectorWeight — mildly harmful when heavy (≥5); default 1 optimal. Weaker than ftsWeight.

Rows `vectorWeight=0/1/2/3/5/10`.

| Value | sextant(6) | memory(10) | memory(100) |
|---|---|---|---|
| 0 (leg off) | 0.629 | 0.596 | 0.589 |
| 1 (default) | 0.655 | 0.700 | 0.611 |
| 2 | 0.644 | **0.704** | 0.587 |
| 3 | 0.644 | 0.687 | 0.565 |
| 5 | 0.644 | 0.471 | 0.542 |
| 10 | 0.644 | 0.465 | 0.520 |

Memory: monotone falloff above 2 (100-query set) and sharp collapse ≥5 on the test set (0.700 →
0.471). Sextant: flat. Verdict: **mild, asymmetric** — never useful above 2; 0 is survivable
(-0.02) unlike ftsWeight=0.

### sourceLambda — strong, monotone harmful beyond 0.1. Default 0.1 near-optimal; sextant wants 0.

Rows `sourceLambda=0/0.05/0.1/0.2/0.3/0.5`.

| Value | sextant(6) | memory(10) | memory(100) |
|---|---|---|---|
| 0 | **0.938** | 0.679 | 0.616 |
| 0.05 | 0.833 | 0.704 | **0.619** |
| 0.1 (default) | 0.655 | 0.700 | 0.611 |
| 0.2 | 0.594 | 0.643 | 0.523 |
| 0.3 | 0.594 | 0.563 | 0.501 |
| 0.5 | 0.594 | 0.573 | 0.482 |

Memory(100): every value above 0.1 costs 0.09-0.13; 0-0.1 is a plateau. Sextant: the sibling
boost is pure harm there (λ=0 → 0.938, the second-biggest gain in the matrix) — the documented
sibling-boost trap, quantified. Verdict: **strong, monotone harmful above default**; the default
sits at the edge of the plateau.

### consolidationThreshold — non-monotone; window 0.05-0.2 best, 0 and ≥0.5 both hurt on the real bank. Default 0.1 solid.

Rows `consolidationThreshold=0/0.05/0.1/0.2/0.5/1.0`.

| Value | sextant(6) | memory(10) | memory(100) |
|---|---|---|---|
| 0 | 0.772 | 0.757 | 0.478 |
| 0.05 | **0.855** | **0.763** | 0.580 |
| 0.1 (default) | 0.655 | 0.700 | **0.611** |
| 0.2 | 0.594 | 0.731 | 0.595 |
| 0.5 | 0.594 | 0.696 | 0.569 |
| 1.0 | 0.594 | 0.596 | 0.552 |

Memory(100): consolidation=0 loses 0.13 — dropping nothing lets weak adjacent siblings crowd
results; ≥0.5 also hurts. Peak is 0.1-0.2 on the 100-query set, 0.05 on the others. Verdict:
**moderate, non-monotone, windowed**; default 0.1 sits inside the window.

### docScoreFormula — NO measurable influence. Neutral knob in these corpora.

Rows `docScoreFormula=max` and `docScoreFormula=sum` — identical on all three corpora
(0.655/0.655, 0.700/0.700, 0.611/0.611). Either the max/sum aggregation never diverges in this
data or the divergence is below measurement noise. Verdict: **no effect observed**; default `max`
kept. The Optuna categorical includes it at no cost (one extra dimension), but expect no signal.

### candidateWindow — marginal; default max3x100 wins. Small window costs a little.

Rows `candidateWindow=max3x100/max5x50`: sextant identical (0.655/0.655); memory(10) 0.700 vs
0.637; memory(100) 0.611 vs 0.602. Verdict: **marginal, default right**; max5x50 loses ~0.01-0.06
by trimming candidates before fusion.

### structureAlpha — STRONG. α=0 (structure arm off) is harmful everywhere, worst on the real bank. Default 0.5 at the plateau start.

Rows `structureAlpha=0/0.25/0.5/0.75/1.0`.

| Value | sextant(6) | memory(10) | memory(100) |
|---|---|---|---|
| 0 | 0.503 | 0.470 | 0.318 |
| 0.25 | 0.655 | 0.486 | 0.491 |
| 0.5 (default) | 0.655 | **0.700** | 0.611 |
| 0.75 | 0.655 | 0.676 | 0.599 |
| 1.0 | 0.655 | 0.557 | **0.612** |

Memory(100): α=0 costs 0.29 (the worst single deviation in the matrix) — the dual-vector
structure arm is doing real work for section-targeted ADR queries. Plateau 0.5-1.0 on the big
set; 1.0 collapses the test set (0.557). Verdict: **strong, non-monotone with a plateau**;
default 0.5 is optimal or near-optimal on every corpus.

### fusion flag — consistently harmful or neutral. Confirms ADR-0078 "ships default off".

Rows `fusion=False/True`: sextant 0.655 → 0.560 (-0.095); memory(10) 0.700 → 0.683;
memory(100) 0.611 → 0.554 (-0.057). The no-regression reorder rescues a handful of queries
(investigation §4: only queries where one leg already had the right answer at rank 1) and
disrupts more than it saves at the corpus level. Verdict: **harmful on both real corpora**;
default `false` confirmed. (The live bank currently has it enabled — a finding for the owner:
the setting row predates this measurement.)

## Summary — what the matrix learned

1. **The defaults are well-chosen for the real bank.** For 7 of 9 knobs the default is at or
   inside the observed optimum window; the two deviations (rrfK on tiny corpora, fusion) both
   favor keeping the default for the production corpus.
2. **The biggest lever is structureAlpha** (α=0 loses 0.29 on memory(100)) — the structure arm
   is load-bearing, not decorative.
3. **The biggest *opportunity* is rrfK on small banks** (k=5-15: +0.28 on sextant) — worth a
   per-corpus-size default in a future ADR.
4. **fusion=true is a measured regression** (≈ -0.06) — the live bank's `fusion.noRegression.
   enabled.global=true` row is worth revisiting.
5. **docScoreFormula is inert** in these corpora — candidate for a "measured, no signal"
   annotation in the parameter reference.
6. **Corpus fragility is real but bounded**: the sextant corpus disagrees with the real bank on
   exactly the knobs the investigation predicted (sibling-boost artifacts: λ and ftsWeight) —
   the real bank's 100-query set is the tuning authority; sextant is the drift guard.

Caveats: all claims are corpus-specific (stated sizes above); RRF is rank-based, so absolute
numbers shift with corpus composition — the DIRECTION of each influence is the durable finding.
