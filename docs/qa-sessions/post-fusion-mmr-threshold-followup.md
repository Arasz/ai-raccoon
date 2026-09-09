# QA session: post-fusion MMR threshold follow-up

## Main motive

The post-fusion MMR eval (6 arms × 5 queries, `docs/work/2026-09-08-mmr-postfusion-eval.md`)
showed threshold filtering (τ=0.95) working but moving only two slots in 40. This session
pressed on what comes next: whether filtering should iterate to a fixed point, what the
current test set actually is, and whether to scale evaluation to a per-project,
hundred-query corpus from a full DB copy.

## Refined questions

1. Threshold-iteration: should the τ-filter run repeatedly (backfill → re-check) until no
   duplicates remain or a hard iteration cap is hit, given a pruned slot pulls a new row up?
2. Test-set inventory: what exactly is the testing set behind the post-fusion numbers?
3. Corpus scale-up: can we build a fresh, unplanned, per-project query set (docs, code,
   misc content) from a full DB copy across all projects?
4. Corpus size: is n=100 queries adequate?

## Structured answers

1. **Iteration converges by construction; implement windowed+backfill for perf, not outcome.**
   The PoC's single greedy pass over the full fused pool (230–365 rows) already checks every
   kept row against every kept row above it, so re-running drops nothing — the promoted Q3/Q4
   rows survived because they were below τ, not because they escaped a check. Iteration with
   the proposed end conditions (zero-drop pass, hard cap / pool exhaustion) matters only for a
   cheaper top-k-windowed formulation, whose fixed point is byte-identical — a perf refactor
   verifiable by diff. Q4's 3→2 (RUBRIC.md for "agent instructions") is unfixable by more
   passes: a τ-vs-relevance trade needing per-query gating, not more filtering.
   Grounding: `MaximalMarginalRelevance.ThresholdFilter`, `SearchResultMerger.Merge`,
   post-fusion eval report.
2. **Five queries, k=8, live bank, no snapshot.** Q1/Q2 under project `jsaa` (faceted
   gmail/dossier queries), Q3–Q5 under `ai-raccoon`, plus one Q1 limit-50 probe. Rubric:
   A=1 iff the chunk answers; single grader, no blind. Caveats: n=5 hand-picked, no held-out
   discipline, reproducibility resting on one byte-identical re-run check.
   Grounding: `/tmp/mmr-py-eval.py` QUERIES, both off-spec JSONs, eval report.
3. **Yes — assembly, not invention.** Query generation from a read-only DB copy exists
   (`build_eval_corpus.py`: 75 file-targeted + 25 non-file; `benchmark_corpus.py`:
   doc-derived + cross-repo clusters). The price is judgments, not queries: mechanical
   metrics need none; expected-source auto-grades are free; keyword-verified are cheap;
   human A=1 on a sample is the only expensive tier. Generate before any tuning decision
   and reserve holdout files up front. Run each query under its own projectId.
   Grounding: both generator scripts, P0a-copy discipline + 13-run cost profile from the eval.
4. **100 suffices for the gate, not per-project bars, and not for full human grading.**
   Paired design (each query its own control) resolves ~±5-query swings — 5× the largest
   observed effect — and 800 slots pin a 5% drop rate to ±1.5%. Five projects → ~20 queries
   each: fine pooled, weak per slice. Human-grade a random 20–30 for calibration; the 75
   `expectedHash` queries auto-grade free. The PoC needed 5; the 100 is regression insurance.
   Grounding: corpus generator header, observed effect sizes from the eval.

## Open / unanswered questions

- Per-query gating for threshold application (when is a freed slot worth more than the
  dropped copy?): Q3 says yes / Q4 says no on n=2 instances — no decision rule proposed.
- Whether to actually commission the 100-query corpus (offered as scoped task; no go given).
- `p=0.001` ambiguity from the eval session (read as ρ=0.001; RBO-p literal meaning declined
  on the grounds it would judge rank 1 only) — confirmed implicitly, never explicitly.

## Source links

- `docs/work/2026-09-08-mmr-postfusion-eval.md`
- `src/AiRaccoon.Infrastructure/Sqlite/Memory/MaximalMarginalRelevance.cs` (worktree
  `.ai-badger/worktrees/air-mmr-postfusion-poc-eval-run`, branch `task/air-mmr-postfusion-poc`)
- `src/AiRaccoon.Infrastructure/Sqlite/Memory/SearchResultMerger.cs` (same worktree)
- `scripts/retrieval_tuning/build_eval_corpus.py`
- `scripts/src/benchmark_corpus.py`
- `/tmp/mmr-py-eval.py`, `/tmp/mmr-post-spec-off.json`, `/tmp/mmr-jsaa-spec-off.json`
- Raw arms: `/tmp/mmr-post-{off,r0001,r001,r003,r005,threshold}.json`,
  `/tmp/mmr-jsaa-{off,r0001,r001,r003,r005,threshold}.json`
