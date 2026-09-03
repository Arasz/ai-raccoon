# Scorer ablation run — 2026-09-04 (P3 of air-promotion-scorer-rebalance-tdd-adr)

**Branch:** `task/air-scorer-ablation-p3` off `3cc35769` (post-#598 main).
**Question:** does the merged ablation pair move ranking quality on labeled data, and does the C# port match its prototype?

## What ran

Three arms of the round-3 lane-A prototype (`docs/work/promotion-scoring-eval/round3/agentA/scorer.py`):

- `before` — pristine copy (shipped v3/lane-A constants).
- `pair` — rule formula `0.30*d-0.25` cap 0.70 (plan 0.35) + checklist `>=2`/`-0.45` only.
- `after` — pair plus verified-contract `>=0.6`/`+0.50`.

The fork diff is 4 hunks, numeric only; organic, auto-note, priors, routing untouched. Forks live outside the repo (`/tmp/ablation/*.py`, machine-local).

Corpus: v1's 61 labels (`reference-labels.json`) rejoined to row bodies with `rebuild_fixture.py`. The live bank holds 11/61; the 1.8.0 backup (2026-08-13, closest in time to labeling) holds **53/61**. Dropped ids everywhere: 1, 20, 22, 24, 25, 26, 33, 44. No train/validation/holdout splits exist anywhere on disk, so this is the full-set v1-subset check, not the round-3 split ablation. That gate is still open.

## Numbers

Full set, n=53 (spearman / nDCG@10):

| arm | spearman | nDCG@10 |
|---|---|---|
| before | +0.6180 | 0.8907 |
| pair | +0.6206 | 0.8829 |
| after | +0.6112 | 0.9373 |

Movement: 43/53 rows change score (mean |d| 0.086, max 0.392). Rank moves by label: 0→1.32, 1→2.00, 2→1.40, 3→1.33, 4→0.33 places. Top-10 overlap 9/10 (id 8 out, id 11 in).

Bootstrap (2000 resamples, seed 42) on after-minus-before: d_spearman 95% CI [-0.046, +0.030], d_nDCG 95% CI [-0.021, +0.107]. Both contain zero.

C# parity: `PromotionScoringRealDataTests.ScoresCorrelateWithHandLabeledUsefulness` with a manifest pointing at the rebuilt 53 (`prototypeSpearman` 0.6112173452774508, tolerance 0.03) — **green**. The merged port matches the after-prototype within tolerance on these rows.

## Reading

The subset cannot tell efficacy apart from noise: spearman is flat, the nDCG gain is one top-10 swap. What it does clear is safety — no significant degradation anywhere, the head (label 4) is rank-stable, and movement concentrates in labels 0–2, which is the spread-the-middle mechanism working as specified. The efficacy question still belongs to the round-3 splits if they ever resurface.

## Reproduce

```
python3 rebuild_fixture.py reference-labels.json <bank.db> labeled.json
# fork round3/agentA/scorer.py with the 4 numeric deltas (2 arms)
python3 run.py   # score_with + measure per arm, move stats     (see /tmp/ablation/run.py)
python3 band.py  # rank moves by label, top-10 overlap, bootstrap CI
AIRACCOON_SCORING_EVAL_FIXTURE=/tmp/ablation/manifest.json dotnet test \
  --filter "FullyQualifiedName~PromotionScoringRealDataTests.ScoresCorrelateWithHandLabeledUsefulness"
```

Caveat: bodies come from a backup snapshot, so this measures the model on near-contemporary text, not the live bank. Nothing here leaves the machine except this report and the ADR note.
