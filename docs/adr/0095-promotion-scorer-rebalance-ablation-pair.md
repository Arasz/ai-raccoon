# 0095. Promotion scorer rebalance, ablation pair only: rule-language gate, checklist counterweight, verified-contract

Date: 2026-09-03

Status: Accepted

Plan: `docs/work/2026-09-03-promotion-scoring-rules.md` (After half, proposal only — this ADR
enacts three rows of it and defers the rest).

## Context

The rules catalog diagnoses a mushy middle in the 2.0–3.0 score band: tone beats durability
there because `rule-language` tags on any single match (743/1000 rows) while its counterweight,
`imperative-checklist`, triggers late (3+ items, −0.30). The catalog's After half proposes a
broad rebalance plus queue plumbing, and recommends running the rule-language gate and the
verified-contract bump as an ablation pair first. Review M5 cut the scope to exactly that pair
— three constant changes, no plumbing — and this lane enacts them with TDD. Everything else in
the After half is deferred, not rejected (see Deferred below).

## Decision

Three constant changes in `src/AiRaccoon.Core/Memory/PromotionContentEvidence.cs`, each
preceded by a failing test run against unmodified code
(`tests/AiRaccoon.Tests/Unit/Memory/PromotionScorerRebalanceTests.cs`, 9 tests, each red
witnessed before its production edit). The `Clamp` structure is unchanged; only constants move.

| # | Rule | Before | After | Code ref |
|---|---|---|---|---|
| 1 | `rule-language` tag gate | any density above zero | density ≥ 0.5 per 100 words | line 61 |
| 2 | `rule-language` bonus | `Clamp(0.38*d − 0.20, −0.20, cap)` | `Clamp(0.30*d − 0.25, −0.25, cap)` | lines 17–20, 59 |
| 3 | `rule-language` default cap | 1.00 | 0.70 | line 18 |
| 4 | `rule-language` plan-channel cap | 0.45 | 0.35 | line 17 |
| 5 | `imperative-checklist` trip | ≥ 3 items | ≥ 2 items | line 148 |
| 6 | `imperative-checklist` penalty | −0.30 | −0.45 | line 150 |
| 7 | `verified-contract` gate | measure word + density ≥ 0.8 | measure word + density ≥ 0.6 | line 155 |
| 8 | `verified-contract` bonus | +0.35 | +0.50 | line 157 |

Net intent, in one sentence per term: one imperative sentence no longer tags or pays while
sustained rule prose still does (rows 1–4); the checklist counterweight triggers earlier and
hits harder (rows 5–6, proven biting by a combined rule+checklist exact-diff test); a rule with
a number behind it is priced like the head shape (rows 7–8).

## Consequences

- The `rule-language` tag count falls toward the durable-rule count; the middle spreads instead
  of bunching. Short checklist-shaped rows that mimicked rules now net negative against
  sustained prose on the same chunk.
- No routing, clamp, floor, sort, dedup, per-doc cap, limit, eviction, or queue-plumbing
  behavior changes: `PromotionScorer.cs`, `OrganicRefinement.cs`, `SharedExtractionService.cs`,
  `SharedExtractionRunner.cs`, and all queue/eviction/version code are untouched.
- The scorer version stamp is unchanged (still 2): the queue is not cleared and rows are not
  re-entered on merit. Whether this rebalance warrants a version bump belongs to the follow-up
  that runs the ablation (see waiver below).

## Alternatives considered

- **Enact the full After half now** (thin-cap 0.75/20 words, durable-cap 0.55, measured ceiling
  0.65, foreign-subject 0.30, organic real-measurements 0.65, doc status-vocabulary, queue
  dynamic floor / organic cap / id canonicalization). Rejected for this lane per review M5: the
  ablation pair settles the two highest-uncertainty moves first; the rest lands on measured
  evidence, not alongside it.
- **Retune the gate to 1.0 instead of 0.5.** Rejected: 0.5 is the catalog's number and keeps a
  two-sentence contract taggable; moving further without fixture evidence would be guessing.

## Deferred (follow-up, not this lane)

Scorer: thin-cap move, durable-rule cap 0.55, measured-values ceiling 0.65, foreign-subject
0.30, organic real-measurements 0.65, doc-channel status-vocabulary, auto-memory-note 0.5
density gate. Queue: dynamic floor near cap, organic per-project per-pass cap (null
`source_file` loses its exemption), jsaa identity canonicalization; sort, dedup, refresh,
limits, sweep, and version-clear handling stay as-is. Process: scorer Version 2→3 bump
decision, parity check against the C# port, and the full train/validation/holdout ablation.

## SHIPPED-WITHOUT-ABLATION waiver — OPEN, owner action required

This change ships on unit-level exact-diff evidence only. The corpus ablation the catalog calls
for (`score_round.py` over train/validation/holdout plus the parity check against the C# port)
was NOT run in this lane: the split fixtures are absent from disk (the eval tree carries only
`reference-labels.json` and the round2/round3 agent scorers, no train/validation/holdout
splits), and running it is outside this lane's allowed files. Merging without that ablation —
or supplying the fixtures first — is the owner's explicit call. This section must not be
marked granted by anyone but the owner.
