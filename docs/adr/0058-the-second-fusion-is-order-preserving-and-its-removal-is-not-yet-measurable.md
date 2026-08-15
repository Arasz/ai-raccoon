# 0058. The second fusion is order-preserving, and its removal is not yet measurable

Date: 2026-08-15

Status: Accepted — **records a change that was built, measured four ways and not shipped.**
`SearchResultMerger` is unchanged.

## Context

`SqliteMemoryStore.SearchAsync` fuses the FTS and vector modalities with
`ReciprocalRankFusion.Fuse`, producing a list whose `Ranking` is the normalized RRF score. It then
hands that single list to `SearchResultMerger.Merge`, which **fuses it again**:

```csharp
var fused = ReciprocalRankFusion.Fuse(lists, rrfK, 0.0, int.MaxValue);
```

A one-list fusion rebuilds every score from rank position alone — `(k+1)/(k+rank)` after
normalization — and discards the modality scores that went in. Measured at the default `k = 60`, a
strong match set and near-orthogonal junk both come out as
`1, 0.9838709677419354, 0.9682539682539681`. The number `memory_search` returns as `ranking` carries
no information about match quality (H1 of the 2026-08-14 project-scope review, verdict item 3).

The owner ruled on open question 7 with *"can we do both?"* — delete the redundant pass **and** make
`ranking` carry a real score. This ADR records why that could not be delivered on this corpus.

## The measurement that changed the package

**At `λ = 0`, the code with the second fusion and the code without it are byte-identical:**
held-out mean nDCG@5 `0.269842`, in-sample `0.652739`, every per-query value equal.

`(k+1)/(k+rank)` is strictly decreasing in rank, so re-fusing an already-sorted list returns it in the
same order. **The second fusion has never changed a single result's position.** Its entire effect on
ranking is indirect: it compresses the score range from RRF's natural spread (~`[0.2, 1.0]`) into
`~[0.85, 1.0]`, and `SourceAffinityRanker`'s two swept constants — the sibling-visibility floor and
the consolidation gap, both **absolute distances below the max** — were calibrated by ADR-0005's sweep
against that compressed curve.

Deleting the fusion therefore silently re-scales two tuned constants. That is the coupling the
package did not anticipate.

## Four configurations, measured

Same store, corpus, pinned query vectors and defaults; held-out and in-sample per ADR-0056.

| Configuration | Held-out | In-sample | Reversal probe (floor 0.285) |
|---|---|---|---|
| **Today** | **0.2846** | 0.6732 | 0.164 — passes |
| Delete the second fusion | 0.2571 | 0.7134 | 0.290 — **fails** |
| …plus sibling floor as a rank window | **0.3333** | 0.6965 | 0.128 — passes strongly |
| …plus **both** constants as rank windows | 0.2818 | 0.7178 | 0.228 — passes |
| `λ = 0` (identical with or without the fusion) | 0.2698 | 0.6527 | — |

**The rank window is a derivation, not a re-tune.** ADR-0005 swept `0.1` as a distance below the max
on the positional curve; `61/(60+r) ≥ 0.9` solves to `r ≤ 7.78`, so the constant means *"siblings from
about the top 7 ranks"*. Restating it that way is scale-free and preserves what the sweep chose.

## Decision

**Ship no production change.**

The derivation applies to **both** constants — they encode the same rank window — and that
configuration measures `0.2818` against today's `0.2846`: inconclusive. The configuration that scores
best applies it to only one of the two, and there is no principle that distinguishes them.

**Selecting it because it scored 0.3333 would be tuning on the held-out set.** Three queries, chosen
from four attempts. That is the circular benchmark ADR-0056 exists to end, one level up, and it would
be a worse outcome than shipping nothing — the number would carry a held-out label it no longer
deserves.

The blocker is therefore **not** owner question 7, which is answered. It is that a 3-query held-out
set can detect a catastrophe (the reversal probe does) and cannot adjudicate a ±0.03 mean.

## What ships instead

Three characterisation tests pinning the defect exactly, in the shape this repo already uses for a
known regression it has decided not to hide:

- `Merge_RebuildsScoresFromRankPosition_DiscardingTheFusedScores` — a strong set and a junk set both
  leave as `(k+1)/(k+rank)`.
- `Merge_SecondFusion_PreservesTheIncomingOrder` — why the defect has never surfaced as a ranking bug.
- `Merge_FloorComparesAgainstThePositionalCurve_NotMatchQuality` — `minRelativeScore` 0.9 admits ranks
  1–7 however badly they matched, which is what ADR-0047's default of `0.0` is protecting callers from.

The first and third **go red the moment the redundant pass is removed** — watched, against the built
change. That is the signal to delete them and assert the fused scores instead.

## What would unblock it

Held-out capacity. Concretely: queries whose expected documents no parameter sweep has touched, in
enough number that a ±0.03 mean is a result rather than noise, and ideally spanning a generator family
the tuning set never saw (ADR-0056 shows there is none today). Until then every ranking package in the
plan — this one, and anything that follows it — is measurable only for catastrophe, not for
improvement.

## Consequences

- `SearchResultMerger`, `SourceAffinityRanker` and `SqliteMemoryStore` are unchanged.
- `ranking` still carries rank position plus a structural adjacency term, and the MCP surface still
  advertises it as a score. Not corrected here — correcting the value is the change that cannot be
  measured, and correcting only the documentation would describe a number nobody wants to keep.
- The `Merge(IEnumerable<IReadOnlyList<…>>)` multi-batch parameter remains dead in production
  (`ModalityCandidates` does the cross-context merge); it is exercised only by tests. Left alone
  rather than narrowed, so this PR touches no production line at all.
- WP4 moves from "follows WP11" to **blocked on held-out capacity** in the improvement plan.

## Evidence

`tests/AiRaccoon.Tests/Unit/Search/SearchResultMergerTests.cs`. The four-row table above was produced
by building each configuration and running `Category=Retrieval`; the `λ = 0` identity was produced by
a probe that ran the same query set through both code paths with source affinity off.
