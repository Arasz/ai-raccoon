# Retrieval parameter tuning — regression elimination (WIP draft)

Date: 2026-08-22
Task: retrieval-tune-no-regress (follow-up to continue-testing-algorithm / PR #400)
Scratch data: /tmp/retrieval-tune-no-regress/ (study dbs, per-config JSONs; this
report's numbers are reproducible from them)

## Question

PR #400's Optuna run found a config (trial 31) that beat defaults on the 100-query
eval set mean (+0.055 nDCG@5) but regressed 14/100 eval queries — several
exact/identifier-heavy queries collapsed from perfect 1.0 → 0.5 (E028, E049, E051,
E052) — and two knobs (consolidationThreshold=0.422, structureAlpha=0.990) sat
outside the matrix's measured safe windows: the classic overfit-to-eval-set
signature.

Can parameters be adjusted to eliminate those regressions while keeping the gains?

## Environment note (reproducibility caveat)

Three findings about the measurement environment:

1. **The reported 0.6105 defaults baseline does not reproduce across fresh bank
   copies**: the same 22,511-entry source file, same binary, same corpus evaluates
   at 0.6055, 0.6092 or 0.6105 depending on the copy's file state — 1-3 queries
   (E007/E018/E048 and near-ties) flip between runs. Within ONE server + file the
   results are byte-deterministic (verified: repeated defaults evals identical;
   the previous session's drift check PASS at 0.6105). The variance is a
   tie-breaking sensitivity in the fused ranking (near-equal RRF scores resolved
   by candidate order, which the file's page layout can change), ~0.005 nDCG@5.
2. **The scratch server's inherited file-watcher ingests host files mid-run**: the
   first search run grew the bank 22,511 → 22,740 (236 entries from the live
   docs/work directory) and its drift check FAILED. Fix: freeze the copy
   (watch.enabled.*=false, sweep.enabled.global=false, extract.enabled.global=false,
   watches tables cleared) before serving. The final search runs on such a frozen
   bank and its drift check passes.
3. All comparisons below are within-run: defaults and every candidate measured on
   the same frozen copy with the same binary and the same server session; the
   defaults config is re-evaluated at session end (drift check).

## Diagnosis (how the collapses happen)

Reproduction: trial-31's exact config in today's environment → mean 0.6563
(+0.046 vs defaults), 12 regressions with the same collapse signature
(E028/E049/E051/E052 1.0→0.5, E048→0.0).

Toggle analysis (trial-31 config with ONE knob reverted to its default; eval-100):

| config | ndcg5 | n_reg | note |
|---|---|---|---|
| defaults | 0.6105 | 0 | |
| trial31 | 0.6563 | 12 | |
| structureAlpha→0.5 | 0.6435 | 12 | collapses persist — α not the driver |
| consolidationThreshold→0.1 | 0.6434 | 14 | MORE regressions — 0.422 protects |
| rrfK→60 | 0.5819 | 23 | rrfK=17 load-bearing for the mean |
| ftsWeight→1 | 0.5692 | 18 | ftsWeight=7 load-bearing for the mean |
| vectorWeight→1 | 0.6297 | 15 | |
| sourceLambda→0.1 | 0.6601 | 10 | BETTER than 0.115 on both axes |
| docScoreFormula→max | 0.6563 | 12 | inert (matrix finding confirmed) |

Finding: no single knob causes the collapses; they are an interaction of the
FTS-dominant sharp-RRF configuration (ftsWeight=7 + rrfK=17) with weak
consolidation — same-file sibling chunks of the target ADR crowd ranks 1-2 and
push the exact target section to rank 3. Reverting any gain knob creates MORE
regressions elsewhere, and trial-31's sourceLambda=0.115 was pure harm (0.1 is
better on both axes).

## Method

1. **Reproduce + attribute**: trial-31 re-evaluated, 7 single-knob reverts
   (table above).
2. **Search** (Optuna TPE, 60 trials, frozen 22,511-entry copy): space = matrix
   safe windows only — rrfK [5,60] log, ftsWeight [1,3], vectorWeight [1,2],
   sourceLambda [0,0.1], consolidationThreshold [0.05,0.2], structureAlpha
   [0.5,0.75], docScoreFormula {max,sum} (7 free knobs). Selection post-hoc by
   the pre-registered rule: zero regressions + max mean, else min regressions,
   then min collapses, then max mean. A first run was invalidated by the
   inherited file-watcher (see environment note); the second run (frozen bank)
   is authoritative: drift check PASS, defaults byte-identical start/end
   (0.6105391530585856).
3. **Refine** around the lead config: two targeted passes of single-knob nudges
   + combos (~40 evals total), same selection rule.
4. **Staircase distillation** (owner feedback): from defaults (E048/E028 correct
   at 1.0) toward the lead config one knob at a time — the minimal step that
   flips the two stubborn queries. [pending]
5. **Validate** the pre-selected candidates on the three sets (test-10 held out,
   one-shot): eval-100 means + per-query regression table; test-10 grades by
   exact expectedHash rank; sextant-6 mean nDCG@5; drift check; pinned sextant
   probe; winner re-evaluated twice. [pending]

## Staircase distillation (owner feedback) — WHY the two stubborn queries collapse

From defaults (both correct at rank 1) toward the lead config, one knob at a time:

| step | mean nDCG@5 | n_reg | E048 (rating-fix/pow) | E028 (memory_get-by-hash) |
|---|---|---|---|---|
| defaults | 0.6105 | 0 | 1.0 @1 | 1.0 @1 |
| +rrfK 60→23 | 0.6309 | 9 | **0.5 @3** | 1.0 @1 |
| +ftsWeight 1→3 | 0.6022 | 12 | 1.0 @1 | **0.5 @3** |
| +consolidation 0.1→0.163 | 0.5946 | 18 | 1.0 @1 | 0.63 @2 |
| +structureAlpha 0.5→0.597 | 0.6039 | 2 | 1.0 @1 | 1.0 @1 |
| +docScoreFormula max→sum | 0.6105 | 0 | 1.0 @1 | 1.0 @1 |
| rrfK + fts together | 0.6384 | 13 | **0.0 —** | 0.63 @2 |
| lead (all five) | 0.6624 | 7 | 0.39 @5 | 0.63 @2 |

Distilled reason: **the two stubborn queries have different, complementary
sensitivities, and both are victims of the FTS-leg dominance that creates the
mean gain.**

- E048's target is the VECTOR leg's rank-1 choice; the FTS leg ranks same-file
  siblings higher. rrfK 60→23 sharpens RRF so the FTS leg's top ranks win the
  fusion → E048 falls. (k=60's spread lets the vector leg's rank-1 win.)
- E028's target is likewise the vector leg's choice; ftsWeight 1→3 (and
  consolidation 0.1→0.163, which drops fewer weak siblings) lets the FTS leg's
  wrong top rank win → E028 falls.
- structureAlpha and docScoreFormula are innocent for both (α alone: 2 minor
  regressions; doc alone: 0).

Consequence: keeping BOTH at rank 1 requires the fusion balance near defaults —
which forfeits the gain, because the gain IS the FTS-dominant balance. No
in-window configuration reconciles them; the lead config (below) is the
best-trade-off point found (0.6624, 7 regressions, 2 collapses, both stubborn
queries still retrievable in top-5).

## 1. Was it possible?

[pending — see 3-set validation]

## 2. New parameters

Best in-window config so far (from search + refine pass 1):

| knob | default | candidate |
|---|---|---|
| rrfK | 60 | 23 |
| ftsWeight | 1 | 3 |
| vectorWeight | 1 | 1 |
| sourceLambda | 0.1 | 0.1 |
| consolidationThreshold | 0.1 | 0.163 |
| structureAlpha | 0.5 | 0.597 |
| docScoreFormula | max | sum |
| candidateWindow | max3x100 | max3x100 |
| fusion | false | false |

[refine pass 2 + staircase may adjust this]

## 3. Performance on the 3 sets

### Eval set (100 queries, memory bank)

Same-session defaults (frozen copy, drift PASS): mean nDCG@5 0.6105, MRR@5
0.5677, hit@3 0.650, hit@1 0.470 — byte-identical to the recorded baseline.

| config | mean nDCG@5 | MRR@5 | hit@3 | n_reg vs defaults | collapses (1.0→) |
|---|---|---|---|---|---|
| defaults | 0.6105 | 0.5677 | 0.650 | 0 | 0 |
| trial31 (out-of-window) | 0.6563 | 0.6050 | 0.740 | 12 | 7 |
| best in-window (search+refine1) | 0.6624 | 0.6100 | 0.710 | 7 | 2 |

Remaining regressions at the best in-window config: E048 (1.0→0.387),
E028 (1.0→0.631), E074, E018, E039, E050, E056 — the two collapses are the
exact/identifier-heavy class (rating-fix pow question, memory_get-by-hash
question) the owner review flagged.

[refine2 + validation numbers pending]

### Test set (10 queries, graded)
[pending — one-shot]

### Sextant corpus (6 queries, drift guard)
[pending]

### Drift check
PASS (defaults identical at search start and end on the frozen bank).

### Determinism re-check
[pending]
