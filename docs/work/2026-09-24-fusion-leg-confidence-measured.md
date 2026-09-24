# Confidence-weighted RRF, measured (issue #706 round 2)

**Date:** 2026-09-24
**Question:** Round 1 (PR #727, docs/work/2026-09-24-fusion-no-regression-flag-measured.md) showed
that ADR-0078's no-fusion-regression reorder cannot lift A9 (fts rank 2, vector miss, hybrid rank 3)
because both legs' own rank-1 winners are distinct chunks that already claim the two spots ahead of
it, and the reorder acts on rank alone. Confidence-weighted RRF scales each leg's RRF weight by how
decisively its own candidates separate between rank 1 and rank k, applied inside
`ReciprocalRankFusion`'s first pass so the resulting ORDER survives the second fusion (ADR-0058).
Does it do better, and without breaking what the gates already hold?

## Pre-declared parameterizations

Three, no more, chosen before any of them were run:

| | k | bounds |
|---|---:|---|
| P1 (shipped default) | 5 | [0.5, 2.0] |
| P2 | 3 | [0.5, 2.0] |
| P3 | 5 | [0.3, 3.0] |

P1 is what `FusionConfigKeys.LegConfidenceK` and `LegConfidence.MinWeight`/`MaxWeight` ship as.
P2 narrows the rank window a query's own confidence is judged over. P3 widens how far a decisive leg
can be trusted, and how far a flat one can be discounted.

## Findings

### F1: Gate (c) at P1: A9 reaches rank 2, and nothing else in the exclusion set moves, but A6 becomes a new violation [MEASURED]

Measured at the chosen production config (k=60, weights 1:1, minScore 0.0, candidate window
Max3x100, source lambda 0.1, consolidation threshold 0.1, DocScoreFormula.Max) over the 19
gradeable queries, hybrid exact-chunk rank vs. best single-leg rank, flag off then flag on at P1:

| Id | Hybrid OFF | Hybrid ON (P1) | FTS | Vector | Best single | OFF holds? | ON holds? |
|---|---:|---:|---:|---:|---:|:--:|:--:|
| A1 | 3 | 3 | 6 | miss | 6 | yes | yes |
| A2 | miss | miss | 2 | miss | 2 | excluded | excluded |
| A3 | miss | miss | 3 | miss | 3 | excluded | excluded |
| A4 | 3 | 2 | 3 | 9 | 3 | yes | yes |
| A5 | 2 | 2 | 2 | 3 | 2 | yes | yes |
| **A6** | **1** | **2** | 1 | miss | 1 | yes | **no, new violation (2 > 1)** |
| A7 | 3 | 7 | 8 | 8 | 8 | yes | yes |
| A8 | 4 | 5 | 3 | miss | 3 | excluded | excluded |
| **A9** | **3** | **2** | 2 | miss | 2 | excluded (pinned ceiling 3) | **reaches 2, the target** |
| A10 | 9 | 9 | miss | miss | (both miss) | excluded | excluded |
| S1 | 1 | 1 | 6 | miss | 6 | yes | yes |
| S2 | miss | miss | miss | miss | (both miss) | excluded | excluded |
| S3 | 1 | 1 | 2 | miss | 2 | yes | yes |
| S4 | 1 | 1 | 2 | miss | 2 | yes | yes |
| S5 | 3 | 3 | 6 | miss | 6 | yes | yes |
| S6 | 2 | 2 | 2 | miss | 2 | yes | yes |
| C1 | 3 | 1 | 4 | 1 | 1 | excluded | excluded |
| C2 | 1 | 1 | 3 | 1 | 1 | yes | yes |
| C5 | 1 | 1 | 1 | 3 | 1 | yes | yes |

**Evidence:** `dotnet exec tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests.dll --filter-class AiRaccoon.Tests.Integration.LegConfidenceFusionGateTests`,
which sets `FusionConfigKeys.LegConfidenceEnabledGlobal` to `"false"` then `"true"` via
`SqliteMemoryStore.SetSettingAsync` and re-runs the same hybrid/FTS-only/vector-only searches used
by `RrfParameterSweepTests.MeasureFusionAsync`, against the committed MiniLM corpus bank
(`docs-memory.db`) with the pinned query vectors (ADR-0050). Full per-query output captured
2026-09-24.

The OFF column here is identical to round 1's F1 "Hybrid OFF" column, as it must be: nothing about
the OFF path changed between the two rounds.

A9 moves from rank 3 to rank 2, meeting the target this round set out to test, unlike ADR-0078's
reorder, which left it at 3 in round 1. S4/S5/S6, the three queries the reorder broke, are untouched
here. But A6 was not previously excluded or pinned, and it drops from rank 1 (matching its FTS rank
exactly) to rank 2. One target reached, one new casualty.

### F2: Why A9 moves here and didn't under the reorder: the weighting acts before the rank collapse, not after it [READ FROM SOURCE]

ADR-0078's reorder sorts the already-fused list by `min(fusedRank, bestLegRank)`, a rank-only
operation applied after `ReciprocalRankFusion.Fuse` has already collapsed each candidate to a single
RRF score. Two legs' distinct rank-1 winners already occupy positions 1 and 2 by the time the reorder
runs, and no rank-based rule can move a rank-2 single-leg winner ahead of both without a stronger
claim than "never below the best single leg" (round 1, F2).

Confidence-weighted RRF instead changes the WEIGHT each leg's raw scores get summed with, before
`Fuse` ever runs (`SqliteMemoryStore.EffectiveLegWeights`, called from `SearchResultFusion` in
`SqliteMemoryStore.cs`). That changes which candidate has the higher summed score in the first
place. The result is a different ORDER, not a rank rewrite, and ADR-0058 already established that an
order set at this point is the one thing that survives `SearchResultMerger.Merge`'s second fusion
pass (it rebuilds scores from rank and so cannot un-set an order, only preserve or perturb it further
via source-affinity). A9's target is FTS rank 2; when the FTS leg's own rank-1..rank-k gap is decisive
enough relative to the vector leg's, the target's score can clear the vector leg's single-leg winner
without needing to out-rank anything on rank alone.

### F3: Why A6 regresses: a candidate common to both legs' windows narrowly overtakes an FTS-only target once vector gets upweighted [MEASURED]

Traced by running A6's query ("How does ranking stop a single document monopolising the top
results?") at k=60, weights 1:1, limit 5, flag off then on, plus FTS-only and vector-only at the
same config:

- FTS-only top 5: `4a6a14f6` (A6's target, rank 1) · `0954ddfa` · `0040bd0c` · `78503d2d` · `83f512d0`.
- Vector-only top 5: `dd0f39cc` (rank 1, unrelated to the target) · `c686e2d1` · `4f9a13f9` ·
  `e582c627` · `2a2553b5` (rank 5).
- Hybrid, flag off: `4a6a14f6` (1.0000) · `2a2553b5` (0.9763) · `12aba0c4` (0.9607) · `0954ddfa`
  (0.9605) · `64f8525b` (0.9453).
- Hybrid, flag on: `2a2553b5` (1.0000) · `4a6a14f6` (0.9922) · `0954ddfa` (0.9839) · `12aba0c4`
  (0.9714) · `64f8525b` (0.9683).

`2a2553b5` never appears in either leg's own top 5 by itself. It sits at vector rank 5 and somewhere
past FTS rank 5 in that leg's much larger candidate window (Max3x100 covers roughly 30 candidates
per leg at this limit), but it draws a score from both legs, and the flag-off order already put it a
close second behind the target (0.9763 vs. 1.0000). With the flag on, the vector leg's own
rank-1-to-rank-5 gap is decisive enough to raise its weight well above 1.0, and that is enough to
tip `2a2553b5`'s combined score a hair past the target's: 1.0000 vs. 0.9922, a margin of well under
one percent of the normalized range. This is not a case where confidence-weighting reasons badly
about A6. It is a near-tie that already existed before the flag, and the reweighting happens to land
on the wrong side of it.

**Evidence:** a scratch trace test added temporarily to `LegConfidenceFusionGateTests` (not
committed, the trace, not the permanent gate), printing each pass's top-5 hashes and normalized
scores; output captured 2026-09-24.

### F4: Held-out gate: the mean survives comfortably, six per-query floors do not [MEASURED]

All 19 gradeable queries (the full held-out set on this corpus, per ADR-0090/`HeldOutRetrievalGateTests`),
nDCG@5, `Limit = 10`, store defaults (the same call `HeldOutRetrievalGateTests.ScoreAsync` makes), at
P1:

| Id | OFF | ON (P1) | Delta | Pinned floor | ON vs floor |
|---|---:|---:|---:|---:|---|
| A1 | 0.315648 | 0.383566 | +0.067918 | 0.315648 | holds |
| A2 | 0.339160 | 0.339160 | +0.000000 | 0.339160 | holds |
| A3 | 0.277273 | 0.277273 | +0.000000 | 0.277273 | holds |
| A4 | 0.868795 | 0.684352 | -0.184443 | 0.868795 | **fails** (0.684352 < 0.863795) |
| A5 | 1.000000 | 1.000000 | +0.000000 | 1.000000 | holds |
| A6 | 0.722727 | 0.699215 | -0.023512 | 0.722727 | **fails** (0.699215 < 0.717727) |
| A7 | 1.000000 | 0.868795 | -0.131205 | 1.000000 | **fails** (0.868795 < 0.995000) |
| A8 | 0.315648 | 0.277273 | -0.038375 | 0.315648 | **fails** (0.277273 < 0.310648) |
| A9 | 0.868795 | 0.722727 | -0.146068 | 0.722727 | holds (exactly at its own floor) |
| A10 | 0.508740 | 0.508740 | +0.000000 | 0.508740 | holds |
| C1 | 0.500000 | 1.000000 | +0.500000 | 0.500000 | holds |
| C2 | 1.000000 | 1.000000 | +0.000000 | 0.500000 | holds |
| C5 | 1.000000 | 1.000000 | +0.000000 | 0.500000 | holds |
| S1 | 1.000000 | 0.722727 | -0.277273 | 1.000000 | **fails** (0.722727 < 0.995000) |
| S2 | 0.169580 | 0.146068 | -0.023512 | 0.169580 | **fails** (0.146068 < 0.164580) |
| S3 | 0.636682 | 0.636682 | +0.000000 | 0.636682 | holds |
| S4 | 0.868795 | 0.868795 | +0.000000 | 0.868795 | holds |
| S5 | 1.000000 | 1.000000 | +0.000000 | 1.000000 | holds |
| S6 | 0.684352 | 0.699215 | +0.014863 | 0.684352 | holds |
| **Mean** | **0.688221** | **0.675505** | **-0.012716** | 0.627901 | holds |

**Evidence:** a scratch test mirroring `HeldOutRetrievalGateTests.ScoreAsync`, flag toggled via
`SetSettingAsync` before each pass, same store, same corpus, output captured 2026-09-24 (not
committed). "Fails" uses `GoldenFile.RankingTolerance` (0.005), the same tolerance
`HeldOutQueries_HoldTheirPinnedNdcg5Floor` itself uses.

The mean holds with room to spare: -0.012716 against the ±0.03 band ADR-0058 names as the smallest
difference this size of held-out set can tell from noise, and well above the mean floor of 0.627901.
But six per-query floors break (A4, A6, A7, A8, S1, S2), by margins from 0.02 to 0.28, a different
set than round 1's six (A2, A4, A6, A10, C1, S1), with three in common (A4, A6, S1). A4 and A6
overlap with ADR-0078's own casualties; A7, A8, S2 are new ones this mechanism introduces on its own.

### F5: Parity gate: the aggregate holds well inside the delta, at every sweep point [MEASURED]

`ParityGateTests`' own population (68 real-world queries, `ManagedHarness.DefaultPoint`, k=60,
weights 1:1), nDCG@10 against the vendored golden reference, at P1:

| | Mean nDCG@10 | Delta vs. reference |
|---|---:|---:|
| Reference (golden) | 0.625101 | n/a |
| New side, flag OFF | 0.666860 | +0.0418 |
| New side, flag ON (P1) | 0.662669 | +0.0376 |

ON vs. OFF at the chosen point: -0.004191.

Both flag states clear `ParityGateTests.NdcgParityDelta` (0.02) with room to spare, and unlike
round 1's no-regression flag, whose aggregate barely moved (-0.0004) while individual queries swung
by up to ±0.23, this measurement checked the full 9-point sweep matrix (k in {10, 30, 60}, weights
in {(1,1), (1,2), (2,1)}), not just the chosen point: the worst delta across all nine was -0.008616
(k30, weights 1:1), the best +0.002789 (k10, weights 2:1). Every point stays well inside the 0.02
gate on its own.

**Evidence:** a scratch test in the `managed-parity` collection reusing `ManagedHarnessFixture`,
flag toggled via `fixture.Harness.Store.SetSettingAsync`, calling `SweepRunner.RunAsync` directly
(not through `EnsureSweptAsync`, which memoizes) so the same fixture could be swept twice with
different flag states; same corpus and embeddings both passes, output captured 2026-09-24 (not
committed).

### F6: P2 and P3 do not improve on P1's A6 regression; A9 stays at rank 2 in both [MEASURED]

Gate (c) only (the cheapest signal, and the one every parameterization already fails on): same
config as F1, flag on, LegConfidenceK/bounds swapped to each pre-declared point in turn.

| Parameterization | A9 hybrid rank | A6 hybrid rank | A6 vs. best single (1) |
|---|---:|---:|---|
| P1 (k=5, [0.5, 2.0]) | 2 | 2 | violates by 1 |
| P2 (k=3, [0.5, 2.0]) | 2 | miss | violates, drops out of top 10 entirely |
| P3 (k=5, [0.3, 3.0]) | 2 | 3 | violates by 2 |

**Evidence:** `FusionConfigKeys.LegConfidenceK` and `LegConfidence.MinWeight`/`MaxWeight` edited in
place to each point, `dotnet build` then `LegConfidenceFusionGateTests` re-run per point (assertions
not re-pinned, only the printed per-query output was read), reverted to the shipped P1 values
afterward; output captured 2026-09-24 (not committed as separate test runs).

A9 reaching rank 2 turns out to hold across all three points: the mechanism that helps it does not
depend on the exact window or bounds chosen. A6 is the opposite: P1 is the *least* damaging of the
three (a one-rank drop), P2 the worst (the target falls out of the top 10 altogether), and P3 is in
between. Narrowing the window (P2) makes a single leg's own confidence swing harder on less evidence;
widening the bounds (P3) lets that swing carry further. Neither direction helps, so the sweep stops
here. Three points were pre-declared, three were run, and the shipped defaults are already the best
of the three on the dimension that matters most (F1's gate-c regression). Running
the full held-out and parity measurement on P2 and P3 would not change the verdict: both are already
worse than P1 on the cheaper gate-c signal, and P1 already fails the keep rule on its own.

## New test

`tests/AiRaccoon.Tests/Integration/LegConfidenceFusionGateTests.cs`,
`Sweep_LegConfidenceEnabled_HoldsExceptTheMeasuredA9Regression`: runs gate (c) with the flag forced
on at the shipped parameterization and asserts every query outside the measured/pinned set still
holds hybrid <= best single leg, and that A9/A6 hold their exact measured ranks (2/2). Mutation:
setting the flag to `"false"` inside the test makes the pinned A9 assertion fail (measured 3,
expected 2), so the test goes red when confidence-weighting is disabled, and green when it runs. RED
then GREEN pasted below.

RED (mutation: flag forced to `"false"`):

```
failed AiRaccoon.Tests.Integration.LegConfidenceFusionGateTests.Sweep_LegConfidenceEnabled_HoldsExceptTheMeasuredA9Regression (2s 237ms)
  Shouldly.ShouldAssertException : pinnedRanks[id]
      should be
  2
      but was
  3

  Additional Info:
      A9: leg-confidence hybrid rank drifted from the 2026-09-24 measurement (2) -- re-measure and update docs/work/2026-09-24-fusion-leg-confidence-measured.md
```

GREEN (flag forced to `"true"`, the committed state):

```
Test run summary: Passed! - AiRaccoon.Tests.dll (net10.0|arm64)
  total: 1
  failed: 0
  succeeded: 1
  skipped: 0
  duration: 1s 578ms
```

## Recommendation

**Keep the flag off. Do not change `FusionConfigKeys.DefaultLegConfidenceEnabled`.**

Applying the keep rule this task set in advance, enable only if A9 <= 2, no gate-(c) regression,
held-out mean within ±0.03 AND no per-query floor broken, parity within 0.02, at the shipped
parameterization (P1):

- A9 <= 2: **yes** (F1). This is the first measured mechanism in this issue's two rounds that
  actually moves A9, and it does so at every parameterization tried (F6), not by luck at one point.
- No gate-(c) regression: **no** (F1, F6). A6 breaks at every parameterization, worst at P2 (a
  complete miss) and mildest at the shipped defaults.
- Held-out mean within ±0.03: **yes** (F4), but that is not the whole rule.
- No per-query floor broken: **no** (F4). Six of nineteen break, a similar count to round 1's
  no-regression flag though a different set, with A4 and A6 breaking under both mechanisms.
- Parity within 0.02: **yes**, comfortably, at every sweep point (F5).

Three of five conditions pass and two fail, and the rule was written as an AND, not a majority vote.
A9 moving to rank 2 is real progress, genuinely more than ADR-0078's reorder achieved, but it comes
by reweighting legs on a per-query basis that has no way to know which leg is right for THAT query,
only which one looks more confident in its own numbers. A6 is the demonstration: its FTS leg has the
correct answer at rank 1, held by essentially no confidence margin over a middling vector-leg
candidate, and confidence-weighting cannot tell "no margin, but correct" from "no margin, and
wrong." Six broken held-out floors say the same thing at a larger sample.

What follows from this:

1. **A9's exclusion in `RrfParameterSweepTests` stays**, at its existing pinned ceiling of 3. This
   flag reaches rank 2 but ships off, so the served system still needs the exclusion.
2. **The flag ships exactly as built**, default off, no CLI exposure added, no change to any
   existing default, because nothing here argues for flipping it, only for keeping the mechanism on
   record with its numbers pinned.
3. **The gap this leaves is real**: something that fixes A9 without A6 would need to distinguish
   "confident and right" from "confident and wrong" per query, which a leg's own score shape alone
   cannot do. ADR-0078's telemetry (`search.fusion.top1_changed`/`top1_rank_delta`/`top5_moved`,
   joined against `search_quality` grades) remains the right instrument for that. It is the only
   source of a per-query "was this actually right" signal this project has, and neither round of this
   issue has had it available.

This closes the round: two independently measured mechanisms (ADR-0078's reorder, and
confidence-weighted RRF) have now been tried against A9, one made no difference and one made real
but incomplete progress, and both stay off pending the telemetry that could tell them apart from a
mechanism that actually knows which leg to trust.
