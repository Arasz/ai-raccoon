# Retrieval parameter tuning — adjustment matrix

Date: 2026-08-21
Task: `continue-testing-algorithm` (branch `task/continue-testing-algorithm-u1`)
Plan: `docs/work/2026-08-21-parameter-tuning-plan.md` (this document implements §3)
Status: **skeleton** — the logic-flow diagram, the stage→knob table and the per-knob
influence sections are in place; every influence verdict is a placeholder marked
**filled by the sweep run**. No numbers in this document are invented.

The raw sweep data the influence claims must cite lives in
`docs/work/2026-08-21-parameter-tuning-matrix.csv` (produced by
`scripts/retrieval_tuning/matrix.py` — do not fill this document by hand).

---

## 1. The retrieval algorithm — logic-flow diagram

Reproduced verbatim from plan §2 (source-verified 2026-08-21 against
`SqliteMemoryStore.cs`, `SearchResultMerger.cs`, `SourceAffinityRanker.cs`,
`ReciprocalRankFusion.cs`, `SqliteMemoryStore.Search.cs`, `ModalityCandidates.cs`):

```mermaid
flowchart TD
    Q[memory_search query\nprojectId, scope, query, limit, minScore] --> R[Resolve SearchParameters\nFromSources query > settings > constants\n2 batched settings SELECTs on the search connection]
    R --> W[Candidate window per modality\nCandidateWindowFor limit, mode\nmax3x100 default | max5x50]
    R --> A[QueryVector with Alpha = structureAlpha\ncontent embedding + structure arm]
    W --> F[FTS leg — FTS5 BM25\nskipped if no expression or ftsWeight=0]
    A --> V[Vector leg — cosine nearest neighbors\nskipped if empty query vector or vectorWeight=0]
    F --> FUS[Weighted RRF fusion per context batch\nscore = sum weight / (k + rank), normalized to max 1.0\nweights = ftsWeight, vectorWeight; k = rrfK]
    V --> FUS
    FUS --> NR{≥2 legs contributed\nAND fusion.noRegression.enabled.global?}
    NR -- yes --> RE[NoFusionRegression reorder\nreorder by best single-leg rank]
    NR -- no --> SK
    RE --> SK[Cross-context candidate merge\nModalityCandidates dedupe by content hash]
    SK --> U[Second RRF pass — unit weight, same k\ncommit 404ba926 'missing unit fusion']
    U --> SA[SourceAffinityRanker\n1 sibling boost: +lambda per adjacent chunk of same multi-chunk source\nchunkIndex >= 0 only, GH#371 guard\n2 doc-score aggregation per source: max | sum\n3 consolidation: drop weak adjacent siblings\nscore gap >= consolidationThreshold\n4 normalize by boosted max]
    SA --> FL[Filter Ranking >= minRelativeScore,\nTake limit]
    FL --> OUT[Ranked results\nhash, ranking, path, sourceFile, chunkIndex, snippet]
```

Stage → knob mapping (the 9 knobs in pipeline order, verbatim from plan §2):

| # | Stage | Knob | Home | Type / range | Default |
|---|---|---|---|---|---|
| 1 | Params resolution | (precedence) | — | query > settings > constants | — |
| 2 | Query vector | `structureAlpha` | settings `retrieval.structureAlpha` | float 0..1 | 0.5 |
| 3 | Candidate window | `candidateWindow` | settings `retrieval.candidateWindow` | "max3x100" \| "max5x50" | max3x100 |
| 4 | Leg gating | `ftsWeight` | MCP `ftsWeight` / settings `retrieval.ftsWeight` | int ≥ 0 (0 = leg off) | 1 |
| 5 | Leg gating | `vectorWeight` | MCP `vectorWeight` / settings `retrieval.vectorWeight` | int ≥ 0 (0 = leg off) | 1 |
| 6 | Weighted RRF | `rrfK` | MCP `rrfK` / settings `retrieval.rrfK` | int ≥ 1 | 60 |
| 7 | Post-pass | fusion flag | settings `fusion.noRegression.enabled.global` | bool | false |
| 8 | Source affinity | `sourceLambda` | MCP `sourceLambda` / settings `retrieval.sourceLambda` | float 0..1 | 0.1 |
| 9 | Doc-score aggregation | `docScoreFormula` | MCP `docScoreFormula` / settings `retrieval.docScoreFormula` | "max" \| "sum" | max |
| 10 | Consolidation | `consolidationThreshold` | MCP `consolidationThreshold` / settings `retrieval.consolidationThreshold` | float ≥ 0 | 0.1 |

Measured constraints that bound every tuning decision (plan §2, evidence in
`docs/work/2026-08-20-hybrid-retrieval-fusion-investigation.md` + the topology reference):

1. **Ranking is corpus-fragile** — RRF is rank-based; adding one entry flipped a target from rank 3 to rank 1. Every rank claim must state corpus + corpus size.
2. **The vector leg's similarity ordering is the binding constraint** — a target ranked 3rd-closest by the model can never reach rank 1 via any weight combination. The weights' ceiling is the pure-vector rank.
3. **The sibling boost is a second scoring layer RRF weights cannot counteract** — λ=0.1 makes adjacent chunks of the single multi-chunk source dominate small corpora.
4. **Fusion flag rescues only queries where one leg already had the right answer at rank 1**.
5. **Weights are integer-only over MCP** (`0.25` rejected); enums fail fast on typos; malformed settings fall back to constants, never crash.
6. **MCP weights cannot express fine ratios** — ladder points must be integers.

---

## 2. Sweep protocol (plan §3.1)

- **Baseline** = the 9 defaults, **written explicitly to the scratch server's settings**
  (never whatever the bank copy inherited — the live bank leaks
  `fusion.noRegression.enabled.global=true` and `retrieval.structureAlpha=0.5`).
- For each knob: sweep the ladder below with every other knob at baseline, evaluate over
  **both datasets** (sextant bank — 9 entries; memory-db copy — 22,509 entries).
- Recorded per config: mean nDCG@5, MRR@5, hit@3, hit@1, per-query ranks, category
  breakdown (file-targeted / non-file). Rows land in the matrix CSV.
- Driver: `scripts/retrieval_tuning/matrix.py` (exits non-zero if any knob's sweep is
  empty or the baseline row is missing — gate G3).

Ladders (default marked **bold**):

| Knob | Ladder |
|---|---|
| rrfK | 1, 5, 15, **60**, 120, 200 |
| ftsWeight | 0, 1, 2, 3, 5, 10 (with vectorWeight=1) |
| vectorWeight | 0, 1, 2, 3, 5, 10 (with ftsWeight=1) |
| sourceLambda | 0, 0.05, **0.1**, 0.2, 0.3, 0.5 |
| consolidationThreshold | 0, 0.05, **0.1**, 0.2, 0.5, 1.0 |
| docScoreFormula | **max**, sum |
| candidateWindow | **max3x100**, max5x50 |
| structureAlpha | 0, 0.25, **0.5**, 0.75, 1.0 |
| fusion flag | **false**, true |

Total ≈ 6+6+6+6+6+2+2+5+2 + 1 baseline = 42 configs × (7 sextant + 10 test + 100 eval) ≈ 4,900 searches.

---

## 3. Probe baseline (sextant, default config)

The probe run that guards the sweep against silent pipeline drift
(`scripts/retrieval_tuning/probe_sextant.py`, gate G1) reproduced the investigation
doc's recorded tables on the tuning build (1.28.0-dev @ 402c72ca, same bundled model
sha `4278337f…`, same 9-entry corpus): all pinned top-5 positions matched, including
the full §3a hash table for sextant-zero-overlap. The doc's §4 "near-miss" label at
astrolabe-original rank 3 was resolved by the first live run to review-note
`9648f7e6` and is now pinned with that provenance.

Every influence statement below inherits the measured constraints: rank claims are
sextant-corpus artifacts unless the memory-copy rows say otherwise.

---

## 4. Per-knob influence

For every knob below the same template applies (plan §3.3):

> **knob** — swept over [ladder] with others at baseline on [corpus, size N]. Mean
> nDCG@5 moved [x → y]; MRR [..]. Rank structure: [..]. Category affected:
> [file-targeted / non-file / both].
> Verdict: [no effect / marginal / strong, monotone / non-monotone / harmful beyond default].
> Evidence: matrix rows [ids]. Caveats: [corpus fragility, sibling-boost interference, ...].

### 4.1 rrfK

Ladder: `1, 5, 15, 60, 120, 200` (default **60**).

> **rrfK** — swept over [1, 5, 15, 60, 120, 200] with others at baseline on
> [sextant, 9; memory, 22,509].
> Verdict: *filled by the sweep run*.
> Evidence: *filled by the sweep run* (matrix rows `rrfK=*`).
> Caveats: *filled by the sweep run* (expect: RRF rank compression at k=60 — measured
> ladder vec=1→2→3 barely changed the order on the sextant bank).

### 4.2 ftsWeight

Ladder: `0, 1, 2, 3, 5, 10` (default **1**, with vectorWeight=1; 0 = FTS leg off).

> **ftsWeight** — swept over [0, 1, 2, 3, 5, 10] with others at baseline on
> [sextant, 9; memory, 22,509].
> Verdict: *filled by the sweep run*.
> Evidence: *filled by the sweep run* (matrix rows `ftsWeight=*`).
> Caveats: *filled by the sweep run* (expect: boosting helps only when the FTS leg
> already has the target near the top; useless for zero-overlap queries).

### 4.3 vectorWeight

Ladder: `0, 1, 2, 3, 5, 10` (default **1**, with ftsWeight=1; 0 = vector leg off).

> **vectorWeight** — swept over [0, 1, 2, 3, 5, 10] with others at baseline on
> [sextant, 9; memory, 22,509].
> Verdict: *filled by the sweep run*.
> Evidence: *filled by the sweep run* (matrix rows `vectorWeight=*`).
> Caveats: *filled by the sweep run* (expect: the model's similarity ordering is the
> binding constraint — a 3rd-closest target cannot be promoted past the pure-vector rank).

### 4.4 sourceLambda

Ladder: `0, 0.05, 0.1, 0.2, 0.3, 0.5` (default **0.1**).

> **sourceLambda** — swept over [0, 0.05, 0.1, 0.2, 0.3, 0.5] with others at baseline on
> [sextant, 9; memory, 22,509].
> Verdict: *filled by the sweep run*.
> Evidence: *filled by the sweep run* (matrix rows `sourceLambda=*`).
> Caveats: *filled by the sweep run* (expect: λ=0 disables the sibling boost — on the
> sextant bank the guide.md chunk pair tops every query at λ=0.1; the sibling-boost trap).

### 4.5 consolidationThreshold

Ladder: `0, 0.05, 0.1, 0.2, 0.5, 1.0` (default **0.1**).

> **consolidationThreshold** — swept over [0, 0.05, 0.1, 0.2, 0.5, 1.0] with others at
> baseline on [sextant, 9; memory, 22,509].
> Verdict: *filled by the sweep run*.
> Evidence: *filled by the sweep run* (matrix rows `consolidationThreshold=*`).
> Caveats: *filled by the sweep run* (expect: interacts with sourceLambda — it drops
> weak ADJACENT siblings, so it can only bite when a multi-chunk source produced them).

### 4.6 docScoreFormula

Ladder: `max, sum` (default **max**).

> **docScoreFormula** — swept over [max, sum] with others at baseline on
> [sextant, 9; memory, 22,509].
> Verdict: *filled by the sweep run*.
> Evidence: *filled by the sweep run* (matrix rows `docScoreFormula=*`).
> Caveats: *filled by the sweep run* (expect: only multi-chunk sources are affected —
> single-chunk entries aggregate identically under max and sum).

### 4.7 candidateWindow

Ladder: `max3x100, max5x50` (default **max3x100**).

> **candidateWindow** — swept over [max3x100, max5x50] with others at baseline on
> [sextant, 9; memory, 22,509].
> Verdict: *filled by the sweep run*.
> Evidence: *filled by the sweep run* (matrix rows `candidateWindow=*`).
> Caveats: *filled by the sweep run* (expect: no effect on the 9-entry sextant bank —
> every candidate fits any window; only the 22,509-entry memory copy can discriminate).

### 4.8 structureAlpha

Ladder: `0, 0.25, 0.5, 0.75, 1.0` (default **0.5**).

> **structureAlpha** — swept over [0, 0.25, 0.5, 0.75, 1.0] with others at baseline on
> [sextant, 9; memory, 22,509].
> Verdict: *filled by the sweep run*.
> Evidence: *filled by the sweep run* (matrix rows `structureAlpha=*`).
> Caveats: *filled by the sweep run* (expect: changes the query vector only — α=1.0 is
> the pure structure arm, α=0 the pure content embedding; sibling-boost interplay possible).

### 4.9 fusion flag

Ladder: `false, true` (default **false**).

> **fusion flag** — swept over [false, true] with others at baseline on
> [sextant, 9; memory, 22,509].
> Verdict: *filled by the sweep run*.
> Evidence: *filled by the sweep run* (matrix rows `fusion=*`).
> Caveats: *filled by the sweep run* (expect: rescues only queries where one leg already
> had the right answer at rank 1 — investigation §4 A/B: top-3 changed only for
> sextant-zero-overlap and sextant-richer; the live bank ships with this flag ENABLED,
> so the sweep's explicit baseline write matters).

---

## 5. Gate status

| Gate | Criterion | Status |
|---|---|---|
| G1 | probe top-5 hashes match investigation doc under defaults | **PASS** 2026-08-21 (see §3; run record: `/tmp/continue-testing-algorithm/runs/2026-08-21/sextant-probe/probe-output.txt`) |
| G3 | matrix runs end-to-end on both datasets; matrix.md contains diagram + per-knob influence statements citing matrix rows; RED witness: matrix.py fails when a knob sweep is empty | sweep **pending** (harness landed; `matrix.py --dry-run` lists the 42 configs; RED witness for empty-sweep/baseline-missing covered by `scripts/tests/test_retrieval_tuning_matrix.py::TestValidateSweep`) |
