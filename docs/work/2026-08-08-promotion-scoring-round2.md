# Promotion scoring — round 2 (fable agents, 292-entry dataset)

Date: 2026-08-08. Follow-up to `2026-08-08-promotion-scoring-eval.md`; baseline = the shipped
scoring v2 (#182/#185, ADR-0018).

## Method deltas vs round 1

- Reference set grew 61 → **292** hand-labeled entries: the 61 original queue candidates, the 86
  previously-promoted organic entries (source_file null — the hard slice), and 145 fresh
  candidates from a limit-50 deep sweep including the new `arasz-home-page` project.
- Three isolated agents on the fable model (A: improve baseline, B: new design, C: freestyle),
  83-84 labeled training rows each.
- Selection on a **42-entry orchestrator-only holdout** no agent ever saw labeled, in addition to
  each agent's ~167 held-out rows.

## Scoreboard (Spearman ρ / nDCG@10)

| Scorer | secret holdout (42) | own held-out (~167) | full (292) |
|---|---|---|---|
| Baseline (scoring v2) | +0.545 / 0.621 | — | +0.456 / 0.605 |
| A — provenance recalibration | +0.653 / 0.733 | +0.453 / 0.700 | +0.586 / 0.749 |
| B — speech-act grammar, unfitted sign-sum | +0.364 / 0.541 | +0.510 / 0.486 | +0.545 / 0.594 |
| **C — channel-routed prior + bounded evidence** | **+0.690 / 0.828** | +0.590 / 0.725 | +0.665 / 0.775 |
| best fusion (A+C rank-sum) | +0.680 / 0.819 | — | +0.655 / 0.779 |

**Winner: Agent C, alone** — best on every uncontaminated comparison; no fusion beats it on the
secret holdout, so no combine. B's grammar-only design confirms provenance signal is load-bearing:
dropping it costs ~0.3 ρ on the secret set.

## What C adds over the shipped v2 (the v3 port spec)

1. **19 provenance channels** (vs 14 archetypes), covering the new families: `.remember/` status
   journals and `session-*` auto-memory dumps are hard noise (prior 0.30, no content rescue);
   `MEMORY.md` index rows 0.55; **named** Claude auto-memory notes 2.70 (curated-gotcha shape);
   dated `*-charter.md` routes to review coordination.
2. **Turn-mirror prose-prefix rescue**: entries with an appended tool-call transcript are scored
   on their prose prefix instead of being sunk wholesale.
3. **Evidence refinements**: first-person exclusions inside durable-rule detection ("I cannot"
   is not a contract), plan-channel rule-lift cap (plans quote gates as "must"), verified-contract
   combo bonus, status-recap openers checked after stripping markdown decoration.
4. Keeps v2's organic refinement spirit: short definitional facts floor at 2.4.

Robustness evidence: ±15% jitter on every constant keeps train ρ in 0.76–0.83; slice balance
within 0.09 (doc +0.808 / organic +0.865 / fresh +0.775 on train).

## Artifacts

`promotion-scoring-eval/round2/agent{A,B,C}/` — scorers + METHOD writeups. Reference labels and
eval harness unchanged from round 1 (fixtures stay out of the repo; they quote private docs).
