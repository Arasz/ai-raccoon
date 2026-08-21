# Owner review — tuned parameters (plan §10 gate)

Date: 2026-08-21
Subject: `2026-08-21-tuned-parameters.json` (Optuna study retrieval-tune, best trial 31)
Review of: `2026-08-21-parameter-tuning-report.md` §1-§3
Verdict: **RECOMMENDATION ONLY — do NOT ship as new defaults.** The numbers ship; the
config does not.

## What the tuning achieved (eval set, 100 queries)

| metric | defaults | tuned | delta |
|---|---|---|---|
| mean nDCG@5 | 0.6105 | 0.6655 | +0.055 |
| mean MRR@5 | 0.5677 | 0.6108 | +0.043 |
| hit@3 | 0.650 | 0.730 | +0.080 |
| hit@1 | 0.470 | 0.480 | +0.010 |

Gain is concentrated in the non-file bucket (+0.078; hermes/shared transcripts); the
file-targeted bucket gains only +0.047 and its hit@1 drops 0.347 → 0.333.

## Why it does not ship

1. **14/100 eval queries regress** — plan §10's >5 flag. Several collapse from perfect to
   0.5 (E028, E049) or worse (E048: 1.0 → 0.387). The regressions cluster on exact /
   identifier-heavy queries (memory_get-by-hash, CLI exit codes, rating fix) — the FTS-heavy
   queries the tuned `ftsWeight=7` re-ranks away from.
2. **Independent test set is net-negative**: 1 improved, 2 worsened (TS-03, TS-08), 7
   unchanged. The one held-out signal available says the config does not generalize.
3. **Two knobs sit outside the matrix's measured windows** — the classic overfit signature:
   - `consolidationThreshold=0.422`: matrix optimum window 0.05-0.2; 0.5 is already −0.04.
   - `structureAlpha=0.990`: matrix plateau 0.5-1.0 on the eval set, but the test set
     collapsed at 1.0 (0.700 → 0.557) — the tuned value sits on that edge.
4. `docScoreFormula=sum` was chosen on an inert knob (matrix: max ≡ sum) — a coin flip.

## What IS worth acting on

- **rrfK = 17** (optimizer) and **15** (matrix best on the eval set) both beat the default 60
  on the big bank, and k=5-15 is far better on tiny banks. A **per-corpus-size rrfK default**
  (small ≤15, large ~15-60) is the one concrete, low-risk ADR this run supports — the
  default 60 dates from the pre-SearchParameters era.
- **fusion flag**: measured regression on both datasets (−0.06..−0.10) while the LIVE bank
  still carries `fusion.noRegression.enabled.global=true`. Revisit that row (separate
  decision; ADR-0078 already ships the flag off).
- **docScoreFormula**: annotate as "measured inert (2026-08-21)" in the parameter reference.
- Test-set TS-10 (shared-tier jsaa query) is just-wrong under BOTH configs — a genuine
  shared-tier retrieval gap worth its own investigation, independent of tuning.

## Accepted deviations / notes

- The tuned-parameters.json + report + matrix CSV are committed as MEASUREMENT ARTIFACTS
  (evidence-first), not as configuration. Shipping them as new defaults requires a follow-up
  ADR with a controlled live-bank experiment (plan §14).
- Drift check PASS (defaults identical start/end of the study) — the +0.055 is not a corpus
  artifact.
