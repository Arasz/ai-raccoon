# Promotion scoring evaluation — 3-agent experiment

Date: 2026-08-08. Part of the `otlp` task's memory-distribution review.

## Why

The shared tier had filled with noise: 86 entries, of which ~53 were Hermes conversation
turn-mirrors bulk-shared on 2026-08-07, plus doc chunks promoted because their text merely
mentions another project id. The owner wiped the tier (backup:
`~/.ai-raccoon/backups/shared-tier-backup-2026-08-08.json`) and commissioned an evaluation of
`SharedExtractionService`'s candidate scoring.

## Method

- `memory_share_extract` (propose) queued 61 candidates across ai-raccoon/ai-badger/jsaa/hermes-default.
- Each candidate was hand-labeled for shared-tier usefulness 0–4 (rubric: 4 = durable actionable
  cross-project fact; 0 = doc-index chunk / superseded review / turn-mirror). Labels:
  `promotion-scoring-eval/reference-labels.json`.
- Candidates were split 3 ways (stratified by label and project). Three isolated agents each got
  one labeled subset plus the full unlabeled set, and delivered a deterministic stdlib-only
  `scorer.py` (same I/O contract):
  - **Agent A** — improve the incumbent (stay mechanical/additive, portable to C#).
  - **Agent B** — new design from first principles (ran on a stronger reasoning model).
  - **Agent C** — free choice.
- Evaluation on each agent's ~40 held-out items: Spearman ρ and nDCG@10 vs the reference labels
  (`promotion-scoring-eval/eval.py`; harness verified with perfect/inverse scorers → ±1.0).

## Results

| Scorer | held-out ρ | held-out nDCG@10 | full-set ρ | full-set nDCG@10 |
|---|---|---|---|---|
| Incumbent (`SharedExtractionService`) | — | — | **+0.125** | 0.553 |
| Agent A (improved incumbent) | +0.390 | 0.727 | +0.496 | 0.870 |
| Agent B (archetype prior + evidence) | +0.615 | 0.690 | +0.713 | 0.857 |
| Agent C (linear content-shape model) | +0.427 | 0.671 | +0.526 | 0.758 |
| Rank-fusion A+B+C | — | — | +0.684 | 0.862 |
| **B + organic-measured override** | **+0.681** | **0.804** | **+0.754** | **0.914** |

## Why the incumbent fails

Its signals are near-constant on a multi-project bank: 61/61 candidates matched `cross-project`
(any mention of a sibling project id — filenames like `ingest-jsaa-docs.py` count) and 61/61
matched `recent`; 60/61 have a `source_file` so `organic-write` almost never fires. The score
collapses to three distinct values (2.5 ×50, 3.5 ×10, 4.5 ×1) — it measures nothing that varies.
Tuning its weights cannot help; the features themselves carry no information on this corpus.

## Recommended algorithm (for the C# port)

Agent B's two-stage shape, plus one override the other two agents both got right:

1. **Provenance archetype → prior.** Classify by source path/shape: organic write 3.45 > ADR 3.0 >
   explanation/architecture doc ~2.3 > measurement/sweep ~2.1 > reference ~1.5 > work note ~1.1 >
   changelog/plan/review ~0.8–1.0 > turn-mirror ~0.45 > doc-index ~0.25.
2. **Content-shape evidence, clamped [−1.6, +1.3].** Bonuses: generalisable-rule language
   ("never", "by design", "trap"), measured numbers with units, foreign project id near
   attribution language (proximity-gated, not bare substring). Penalties: markdown-link/table
   density, findings-register rows, in-flight coordination markers (`AC:`, `Gate:`, worktree),
   superseded markers.
3. **Organic-measured override:** an organic write (no `source_file`) containing ≥2 measured
   values floors at the organic prior — prevents the turn-mirror classifier from eating real
   measurements (Agent B's only material miss, candidate #21).

Keep `access_count` as a small log-scaled bonus (Agent C validated it), keep recency only as a
tie-break. All three scorers and methods are preserved under `promotion-scoring-eval/` for the
implementation task.

## Sorted candidate table (reference usefulness, incumbent score, winner score)

| # | id | proj | usefulness (ref) | incumbent | winner (B+org) | source |
|---|----|------|-----|-----|-----|--------|
| 1 | 61 | hermes-default | 4 | 3.5 | 3.96 | `docs/work/reviews/2026-08-07-api-deploy-guard-failure.md` |
| 2 | 21 | ai-badger | 4 | 4.5 | 3.40 | `(organic write)` |
| 3 | 47 | jsaa | 4 | 2.5 | 0.88 | `…assistant/docs/work/reviews/2026-08-07-full-review/PLAN-integrated.md` |
| 4 | 20 | ai-raccoon | 3 | 2.5 | 3.50 | `ai-raccoon/docs/adr/0009-otlp-export.md` |
| 5 | 1 | ai-raccoon | 3 | 3.5 | 3.19 | `ai-raccoon/docs/adr/0002-opentelemetry-observability.md` |
| 6 | 40 | ai-badger | 3 | 2.5 | 2.83 | `ai-badger/docs/work/2026-08-07-full-project-review-charter.md` |
| 7 | 17 | ai-raccoon | 3 | 2.5 | 2.59 | `ai-raccoon/docs/explanation/agent-memory-architecture.md` |
| 8 | 36 | ai-badger | 3 | 2.5 | 1.17 | `ai-badger/docs/work/2026-08-07-full-project-review-plan.md` |
| 9 | 19 | ai-raccoon | 2 | 2.5 | 2.66 | `ai-raccoon/docs/work/2026-08-04-wave3-source-affinity-sweep.md` |
| 10 | 3 | ai-raccoon | 2 | 3.5 | 2.58 | `…ccoon/docs/work/archive/2026-08-04-memory-model-research-synthesis.md` |
| 11 | 30 | ai-badger | 2 | 2.5 | 2.46 | `ai-badger/docs/framework-architecture.md` |
| 12 | 31 | ai-badger | 2 | 2.5 | 2.37 | `ai-badger/docs/framework-architecture.md` |
| 13 | 4 | ai-raccoon | 2 | 3.5 | 2.35 | `…ccoon/docs/work/archive/2026-08-04-memory-model-research-synthesis.md` |
| 14 | 18 | ai-raccoon | 2 | 2.5 | 2.22 | `ai-raccoon/docs/work/2026-08-04-wave4-rrf-sweep.md` |
| 15 | 53 | jsaa | 2 | 2.5 | 1.64 | `…assistant/docs/work/reviews/2026-08-07-full-review/PLAN-integrated.md` |
| 16 | 11 | ai-raccoon | 2 | 2.5 | 1.58 | `ai-raccoon/docs/plans/2026-08-08-embedding-perf.md` |
| 17 | 26 | ai-badger | 2 | 2.5 | 1.48 | `ai-badger/docs/skills.md` |
| 18 | 58 | jsaa | 2 | 2.5 | 1.37 | `…i-assistant/docs/work/reviews/2026-08-07-full-review/A6-scripts-ci.md` |
| 19 | 27 | ai-badger | 2 | 2.5 | 1.10 | `ai-badger/docs/skills.md` |
| 20 | 42 | jsaa | 1 | 2.5 | 1.82 | `job-search-ai-assistant/docs/reference/ci-workflows.md` |
| 21 | 28 | ai-badger | 1 | 2.5 | 1.73 | `ai-badger/docs/changelog/0.103.0-run-git-covers-where-it-matters.md` |
| 22 | 43 | jsaa | 1 | 2.5 | 1.67 | `job-search-ai-assistant/docs/reference/ci-workflows.md` |
| 23 | 5 | ai-raccoon | 1 | 3.5 | 1.42 | `ai-raccoon/docs/plans/memory-first-gate-implementation-plan.md` |
| 24 | 23 | ai-badger | 1 | 2.5 | 1.40 | `…ocs/changelog/0.113.0-the-agent-files-carry-the-rule-not-the-essay.md` |
| 25 | 39 | ai-badger | 1 | 2.5 | 1.15 | `ai-badger/docs/changelog/0.89.0-gate-stops-writing-to-home.md` |
| 26 | 32 | ai-badger | 1 | 2.5 | 0.92 | `ai-badger/docs/getting-started.md` |
| 27 | 51 | jsaa | 1 | 2.5 | 0.92 | `…assistant/docs/work/reviews/2026-08-07-full-review/PLAN-integrated.md` |
| 28 | 29 | ai-badger | 1 | 2.5 | 0.84 | `ai-badger/docs/changelog/0.103.0-run-git-covers-where-it-matters.md` |
| 29 | 59 | jsaa | 1 | 2.5 | 0.76 | `…i-assistant/docs/work/reviews/2026-08-07-full-review/A6-scripts-ci.md` |
| 30 | 25 | ai-badger | 1 | 2.5 | 0.70 | `ai-badger/docs/skills.md` |
| 31 | 2 | ai-raccoon | 1 | 3.5 | 0.62 | `…raccoon/docs/work/archive/2026-08-06-adoption-moe-report-a-systems.md` |
| 32 | 10 | ai-raccoon | 1 | 2.5 | 0.61 | `ai-raccoon/docs/plans/2026-08-08-embedding-perf.md` |
| 33 | 56 | jsaa | 1 | 2.5 | 0.50 | `…i-assistant/docs/work/reviews/2026-08-07-full-review/A6-scripts-ci.md` |
| 34 | 38 | ai-badger | 1 | 2.5 | 0.40 | `ai-badger/docs/work/2026-08-07-full-project-review-plan.md` |
| 35 | 37 | ai-badger | 1 | 2.5 | 0.27 | `ai-badger/docs/work/2026-08-07-full-project-review-plan.md` |
| 36 | 13 | ai-raccoon | 1 | 2.5 | 0.23 | `ai-raccoon/docs/work/2026-08-07-moe-g-scripts-build.md` |
| 37 | 48 | jsaa | 1 | 2.5 | 0.10 | `…assistant/docs/work/reviews/2026-08-07-full-review/PLAN-integrated.md` |
| 38 | 54 | jsaa | 1 | 2.5 | 0.02 | `…assistant/docs/work/reviews/2026-08-07-full-review/PLAN-integrated.md` |
| 39 | 8 | ai-raccoon | 0 | 3.5 | 1.63 | `ai-raccoon/docs/plans/scripts-refactor.md` |
| 40 | 60 | jsaa | 0 | 2.5 | 1.04 | `…i-assistant/docs/work/reviews/2026-08-07-full-review/A6-scripts-ci.md` |
| 41 | 14 | ai-raccoon | 0 | 2.5 | 0.99 | `ai-raccoon/docs/work/2026-08-07-moe-g-scripts-build.md` |
| 42 | 52 | jsaa | 0 | 2.5 | 0.96 | `…assistant/docs/work/reviews/2026-08-07-full-review/PLAN-integrated.md` |
| 43 | 16 | ai-raccoon | 0 | 2.5 | 0.85 | `ai-raccoon/docs/work/2026-08-07-moe-g-scripts-build.md` |
| 44 | 6 | ai-raccoon | 0 | 3.5 | 0.60 | `ai-raccoon/docs/plans/scripts-refactor.md` |
| 45 | 15 | ai-raccoon | 0 | 2.5 | 0.57 | `ai-raccoon/docs/work/2026-08-07-moe-g-scripts-build.md` |
| 46 | 7 | ai-raccoon | 0 | 3.5 | 0.52 | `ai-raccoon/docs/plans/scripts-refactor.md` |
| 47 | 49 | jsaa | 0 | 2.5 | 0.49 | `…assistant/docs/work/reviews/2026-08-07-full-review/PLAN-integrated.md` |
| 48 | 9 | ai-raccoon | 0 | 3.5 | 0.49 | `ai-raccoon/docs/plans/scripts-refactor.md` |
| 49 | 12 | ai-raccoon | 0 | 2.5 | 0.45 | `ai-raccoon/docs/work/2026-08-07-moe-c-host-mcp-cli.md` |
| 50 | 35 | ai-badger | 0 | 2.5 | 0.40 | `ai-badger/docs/work/README.md` |
| 51 | 57 | jsaa | 0 | 2.5 | 0.27 | `…i-assistant/docs/work/reviews/2026-08-07-full-review/A6-scripts-ci.md` |
| 52 | 46 | jsaa | 0 | 2.5 | 0.25 | `…assistant/docs/work/reviews/2026-08-07-full-review/PLAN-integrated.md` |
| 53 | 33 | ai-badger | 0 | 2.5 | 0.20 | `ai-badger/docs/work/README.md` |
| 54 | 50 | jsaa | 0 | 2.5 | 0.02 | `…assistant/docs/work/reviews/2026-08-07-full-review/PLAN-integrated.md` |
| 55 | 22 | ai-badger | 0 | 2.5 | 0.00 | `ai-badger/docs/changelog/README.md` |
| 56 | 24 | ai-badger | 0 | 2.5 | 0.00 | `ai-badger/docs/changelog/README.md` |
| 57 | 34 | ai-badger | 0 | 2.5 | 0.00 | `ai-badger/docs/work/README.md` |
| 58 | 41 | jsaa | 0 | 2.5 | 0.00 | `job-search-ai-assistant/docs/CHANGELOG.md` |
| 59 | 44 | jsaa | 0 | 2.5 | 0.00 | `job-search-ai-assistant/docs/work/reviews/README.md` |
| 60 | 45 | jsaa | 0 | 2.5 | 0.00 | `…earch-ai-assistant/docs/work/reviews/2026-08-07-full-review/README.md` |
| 61 | 55 | jsaa | 0 | 2.5 | 0.00 | `…-ai-assistant/docs/work/reviews/2026-08-07-full-review/A8-frontend.md` |