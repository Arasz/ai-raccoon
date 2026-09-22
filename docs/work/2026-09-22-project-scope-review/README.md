# Project-scope review — 2026-09-22

Whole-codebase review of AiRaccoon at base `5bca1900` (product code identical to `v1.42.5`):
ten parallel lanes, an adversarial re-derivation, severity calibration against the live product,
a reviewed plan, and owner rulings. 72 findings; 30 confirmed and 8 corrected by the adversarial pass,
none refuted.

| File | What it holds |
|---|---|
| [REVIEW-ASSEMBLED.md](REVIEW-ASSEMBLED.md) | The graded record — every finding (`### F<n>`) with severity and evidence ([HTML view](REVIEW-ASSEMBLED.html)) |
| [GROUND-TRUTH.md](GROUND-TRUTH.md) | Phase 0 measurements the lanes were briefed with |
| [PHASE3-VERDICTS.md](PHASE3-VERDICTS.md) | Adversarial verification verdicts (details in [verify/](verify/)) |
| [PHASE4-CALIBRATION.md](PHASE4-CALIBRATION.md) | Severity calibrated against the shipped defaults |
| [PHASE5-PLAN-v2.md](PHASE5-PLAN-v2.md) + [addendum](PHASE5-PLAN-v2.1-ADDENDUM.md) | The implementation plan in waves, after the [gate audit and plan review](reviews/) |
| [OWNER-RULINGS.md](OWNER-RULINGS.md) | Every owner ruling and its implementation consequence |
| [CAMPAIGN-LOG.md](CAMPAIGN-LOG.md) | How the campaign ran, including the model-routing incident |
| [lanes/](lanes/) | Each expert lane's raw report |
| [lane-questions.md](lane-questions.md), [lane-still-open.md](lane-still-open.md) | Open questions and unresolved items the lanes raised |

Implementation PRs: #641, #642, #643 (Wave 1), #644 (Wave 2), #645, #646 and later (Wave 3).
The F72 evidence quotes a canary string containing `--password hunter2`; it is a planted test
value, not a credential.
