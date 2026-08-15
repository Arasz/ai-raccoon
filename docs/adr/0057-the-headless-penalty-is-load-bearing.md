# 0057. The headless penalty is load-bearing

Date: 2026-08-15

Status: Accepted — **records a change that was built, measured and rejected.** `StructureFusion`
is unchanged.

## Context

Two review lanes found the same line independently, and the improvement plan made it WP12:

```csharp
return alpha * contentSim + (1.0 - alpha) * (structureSim ?? 0.0);
```

A row with no `structure_embedding` never appears in the structure KNN list, so `structureSim` is
**absent, not low** — and defaulting it to `0.0` caps that row at `alpha` (0.5) of what a headed row
can reach. The scale is not marginal: **10,311 of 16,145 rows (64%)** of the live bank have no
structure embedding, by design — `EmbedIfConfiguredAsync` computes one only when a heading parses.
ADR-0004's own consequence claims the opposite behaviour: *"banks without structure vectors degrade
to content-only ordering."* That holds when the **whole** bank is headless, because alpha then scales
every score identically. In a mixed bank it does not, and every real bank is mixed.

The finding is correct. The fix is not.

## What was measured

The gate corpus is representative, which had to be checked before any of this counted:
**1,647 of 2,518 rows (65.4%)** of `jsaa-memory.db` have no structure embedding, against the live
bank's 64%. The corpus can answer the question.

`Fused` was changed to return `contentSim` when `structureSim` is absent — per-row degradation to
content-only, exactly what ADR-0004 describes — and the retrieval gates were run.

| Gate | Before | After |
|---|---|---|
| S3 — ADR-0011 Alternatives chunk | 3 | **4** |
| S4 — ADR-0011 Consequences chunk | 3 | **6** |
| S6 — ADR-0060 What-is-lost chunk | 3 | **10** |
| A2 — hybrid rank (Wave 0 gate a) | 1 | **2** |
| Held-out A10 nDCG@5 | 0.1696 | **0.1461** |
| Held-out A8 nDCG@5 | 0.1312 | 0.1461 |
| Held-out mean | 0.2846 | 0.2818 |
| In-sample mean | 0.6732 | 0.6800 |

And the signal that settles it, from ADR-0056's reversal probe: **reversing the ranked list scores
0.610 against 0.282 unreversed.** After the change, the held-out ordering is anti-correlated with
relevance — destroying it more than doubles the score.

## Decision

**Keep `structureSim ?? 0.0`. The cap is the mechanism by which the dual-vector signal favours headed
chunks at all.**

Without it a headed row beats a headless row of equal content similarity only when
`structureSim > contentSim`, which on this corpus is rare. The lift ADR-0004 shipped — section-targeted
queries finding their section — is substantially *this bias*, not the structure similarity doing
independent work. Removing the bias does not reveal a better ranking underneath; it removes the
ranking.

That is worth stating plainly because it is not what ADR-0004 claims to be doing, and the next reader
will find the same line and reach for the same fix.

## What a real fix would need

Per-row **renormalisation** rather than per-row degradation: a headless row scores `contentSim` and a
headed row scores the blend, with alpha re-derived so the two are on one scale — which is a new
parameter and needs its own sweep. That sweep cannot be run on the 11 tuning queries (ADR-0056), so it
needs held-out capacity the catalog does not currently have. Recorded as the shape of the work, not
scheduled.

Rejected outright: tuning a non-zero constant for absent structure. It is a knob whose only available
tuning set is the one just shown to be in-sample.

## Consequences

- `StructureFusion.Fused` is unchanged. Its doc comment now carries the measurement instead of
  restating the formula.
- `Fused_MissingStructure_ContributesZero` is renamed
  `Fused_AbsentStructure_ContributesZero_WhichIsHowTheSignalFavoursHeadedChunks`. The behaviour was
  the same either way; what changes is that the assertion is now **adjudicated** — it was a
  transcription of what the code did, and it is now a contract with evidence behind it.
- A new gate, `Rank_HeadedRow_OutranksHeadlessRowOfEqualContentSimilarity`, pins the property the
  measurement showed matters. It goes red under the rejected change — watched, at the same time the
  three section gates did.
- ADR-0004's "degrade to content-only ordering" consequence is **narrowed**: true for a uniformly
  headless bank, false for a mixed one. Not superseded — the decision to ship the dual vector stands;
  one of its stated consequences was over-general.
- WP12 is closed as refuted in the improvement plan.

## Evidence

`tests/AiRaccoon.Tests/Unit/Embedding/StructureFusionTests.cs`, and the four integration gates above
(`SectionTargetedRetrievalTests`, `QueryConstructionTests`, `HeldOutRetrievalGateTests`), all run
against the built change before it was reverted.

**A note on what caught it.** The section gates would have caught the section regression on their own;
this was not a defect only the new gate could see. What ADR-0056's gate added was A10's per-query
regression and the reversal inversion — the fact that the *ordering* had gone anti-correlated, which
no other gate in the suite expresses.
