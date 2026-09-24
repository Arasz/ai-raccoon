# ADR-0078's no-fusion-regression flag, measured on and off (issue #706)

**Date:** 2026-09-24
**Question:** Issue #706 asks whether a fusion change can fix A9-class queries (RRF trusting one
confident leg) without held-out regressions, so the A9 exclusion in `RrfParameterSweepTests` gate
(c) can be dropped. ADR-0078 already shipped exactly that reorder behind
`fusion.noRegression.enabled.global`, default off, because the offline corpus could not adjudicate
it. This record turns the flag on and measures it, rather than designing a new fusion algorithm.

## Findings

### F1 — Gate (c): the flag does not move A9, and it does not resolve the exclusion set [MEASURED]

Measured at the chosen production config (k=60, weights 1:1, minScore 0.0, candidate window
Max3x100, source lambda 0.1, consolidation threshold 0.1, DocScoreFormula.Max) over the 19
gradeable queries, hybrid exact-chunk rank vs. best single-leg rank, flag off then flag on:

| Id | Hybrid OFF | Hybrid ON | FTS | Vector | Best single | OFF holds? | ON holds? |
|---|---:|---:|---:|---:|---:|:--:|:--:|
| A1 | 3 | 1 | 6 | miss | 6 | yes | yes |
| A2 | miss | miss | 2 | miss | 2 | excluded | excluded |
| A3 | miss | miss | 3 | miss | 3 | excluded | excluded |
| A4 | 3 | 2 | 3 | 9 | 3 | yes | yes |
| A5 | 2 | 2 | 2 | 3 | 2 | yes | yes |
| A6 | 1 | 1 | 1 | miss | 1 | yes | yes |
| A7 | 3 | 5 | 8 | 8 | 8 | yes | yes |
| A8 | 4 | 4 | 3 | miss | 3 | excluded | excluded |
| **A9** | **3** | **3** | **2** | **miss** | **2** | excluded (pinned ceiling 3) | **still violates (3 > 2)** |
| A10 | 9 | 9 | miss | miss | (both miss) | excluded | excluded |
| S1 | 1 | 1 | 6 | miss | 6 | yes | yes |
| S2 | miss | miss | miss | miss | (both miss) | excluded | excluded |
| S3 | 1 | 1 | 2 | miss | 2 | yes | yes |
| **S4** | **1** | **3** | 2 | miss | 2 | yes | **no, new violation (3 > 2)** |
| **S5** | **3** | **7** | 6 | miss | 6 | yes | **no, new violation (7 > 6)** |
| **S6** | **2** | **3** | 2 | miss | 2 | yes | **no, new violation (3 > 2)** |
| C1 | 3 | 4 | 4 | 1 | 1 | excluded | excluded |
| C2 | 1 | 1 | 3 | 1 | 1 | yes | yes |
| C5 | 1 | 1 | 1 | 3 | 1 | yes | yes |

**Evidence:** `dotnet exec tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests.dll --filter-class AiRaccoon.Tests.Integration.FusionNoRegressionFlagGateTests`,
which sets `FusionConfigKeys.NoRegressionEnabledGlobal` to `"false"` then `"true"` via
`SqliteMemoryStore.SetSettingAsync` and re-runs the same hybrid/FTS-only/vector-only searches used
by `RrfParameterSweepTests.MeasureFusionAsync`, against the committed MiniLM corpus bank
(`docs-memory.db`) with the pinned query vectors (ADR-0050). Full per-query output captured
2026-09-24 (see the PR's test run logs).

A9 stays at hybrid rank 3 with the flag on: no improvement, but also no further regression, so its
existing pinned ceiling of 3 (RrfParameterSweepTests) still holds. Three queries the gate holds
without the flag (S4, S5, S6) regress into new violations. No excluded query drops out of the
exclusion set (A9 still needs it; A2/A3/A8/A10/C1/S2 are unaffected or still separately gated).

### F2 — Why A9 does not move: the reorder's own position bound already caps it at 3 [MEASURED]

Traced by running A9's query at k=60, weights 1:1, limit 20, with the flag off and on, plus
FTS-only and vector-only at the same config:

- FTS-only rank 1: hash `69d7961f…` (not A9's target).
- Vector-only rank 1: hash `ee2094b7…` (not A9's target, and different from the FTS rank-1 hash).
- A9's target (`5c62bdf1…`) is FTS rank 2, absent from vector's top 20.
- Hybrid, flag off: `ee2094b7…` (1), `69d7961f…` (2), target (3).
- Hybrid, flag on: identical order, the same three hashes at the same three ranks.

`NoFusionRegression.Reorder` sorts by `key = min(fusedRank, bestLegRank)`, ties broken by the
original fused rank. A9's target has `bestLegRank = 2` (from FTS; it never appears in vector's
candidate window), so its key is `min(3, 2) = 2`. But two *different* chunks already claim key 1:
the FTS leg's own rank-1 winner and the vector leg's own rank-1 winner are two distinct chunks,
each with `bestLegRank = 1`, and both already sit ahead of the target in the pre-reorder fused
order (that is exactly why they are ranks 1 and 2 already). With two items occupying key 1, A9's
target (key 2) can land no higher than position 3 among the remaining candidates, and it was
already sitting there before the reorder ran. The reorder is a structural no-op for this query,
not a disabled or misconfigured one: ADR-0078's own stated bound ("with L legs, up to L results
can claim rank 1... the promoted result lands at position ≤ L") generalizes past rank-1 winners to
rank-2 winners like A9, and the general form of that bound is what pins A9 at 3, not 2. Fixing this
would need a rule that outranks BOTH single-leg rank-1 winners on A9's behalf, which is a stronger
claim than "never below the best single leg" and is out of scope here (see Recommendation).

**Evidence:** a scratch trace test in `FusionNoRegressionFlagGateTests`, querying A9 at `Limit: 20`
with the flag off, then on, then FTS-only and vector-only, printing the top-5 hashes of each pass;
output captured 2026-09-24 (not committed; the trace, not the permanent gate).

### F3 — S4, S5, S6 regress because the reorder's output is re-merged through source affinity a second time [MEASURED]

ADR-0078 documents that the enabled path runs `SearchResultMerger.Merge` twice: once for the
baseline order, once on the reorder's output, to produce the served list. The second pass re-runs
`SourceAffinityRanker.Rank` (sibling-count boost, consolidation, document-first tie-break) over the
*reordered* list, which can move a result the reorder itself never touched. Re-measuring the same
19 queries with source affinity disabled (`SourceLambda = 0`, all else unchanged) changes the FTS-
only and vector-only rankings too (source affinity runs on every search, not just the hybrid one),
so it is not a clean isolation of the reorder from source affinity. It confirms the two interact
rather than proving the reorder alone is regression-free: with lambda at 0 a different set of
queries move (A2, A4, A6, S3, S4, S6 among others), not the same S4/S5/S6 set, which is consistent
with the interaction ADR-0078 already names ("source affinity wins, and by how much is
measurable") rather than with a bug in the reorder itself.

**Evidence:** same test class, `FixedSourceLambda` changed from `0.1` to `0.0` for one run, output
captured 2026-09-24 (not committed; a scratch measurement, not a permanent test).

### F4 — Held-out gate: the mean survives, six per-query floors do not [MEASURED]

All 19 gradeable queries (the full held-out set on this corpus, per ADR-0090/`HeldOutRetrievalGateTests`),
nDCG@5, `Limit = 10`, store defaults (the same call `HeldOutRetrievalGateTests.ScoreAsync` makes):

| Id | OFF | ON | Delta | Pinned floor | ON vs floor |
|---|---:|---:|---:|---:|---|
| A1 | 0.315648 | 0.553146 | +0.237498 | 0.315648 | holds |
| A2 | 0.339160 | 0.169580 | -0.169580 | 0.339160 | **fails** (0.169580 < 0.334160) |
| A3 | 0.277273 | 0.383566 | +0.106293 | 0.277273 | holds |
| A4 | 0.868795 | 0.722727 | -0.146068 | 0.868795 | **fails** (0.722727 < 0.863795) |
| A5 | 1.000000 | 1.000000 | +0.000000 | 1.000000 | holds |
| A6 | 0.722727 | 0.699215 | -0.023512 | 0.722727 | **fails** (0.699215 < 0.717727) |
| A7 | 1.000000 | 1.000000 | +0.000000 | 1.000000 | holds |
| A8 | 0.315648 | 0.315648 | +0.000000 | 0.315648 | holds |
| A9 | 0.868795 | 0.868795 | +0.000000 | 0.722727 | holds |
| A10 | 0.508740 | 0.315648 | -0.193092 | 0.508740 | **fails** (0.315648 < 0.503740) |
| C1 | 0.500000 | 0.430677 | -0.069323 | 0.500000 | **fails** (0.430677 < 0.495000) |
| C2 | 1.000000 | 1.000000 | +0.000000 | 0.500000 | holds |
| C5 | 1.000000 | 1.000000 | +0.000000 | 0.500000 | holds |
| S1 | 1.000000 | 0.868795 | -0.131205 | 1.000000 | **fails** (0.868795 < 0.995000) |
| S2 | 0.169580 | 0.169580 | +0.000000 | 0.169580 | holds |
| S3 | 0.636682 | 0.636682 | +0.000000 | 0.636682 | holds |
| S4 | 0.868795 | 0.868795 | +0.000000 | 0.868795 | holds |
| S5 | 1.000000 | 1.000000 | +0.000000 | 1.000000 | holds |
| S6 | 0.684352 | 0.722727 | +0.038375 | 0.684352 | holds |
| **Mean** | **0.688221** | **0.669767** | **-0.018453** | 0.627901 | holds |

**Evidence:** a scratch test mirroring `HeldOutRetrievalGateTests.ScoreAsync`, flag toggled via
`SetSettingAsync` before each pass, same store, same corpus, output captured 2026-09-24 (not
committed). The tolerance used for the "fails" column is `GoldenFile.RankingTolerance` (0.005),
matching the tolerance `HeldOutQueries_HoldTheirPinnedNdcg5Floor` itself uses.

The held-out **mean** stays comfortably above its floor with the flag on (0.669767 against a floor
of 0.627901): the delta from off to on is -0.018453, inside the ±0.03 band ADR-0058 names as the
smallest difference this size of held-out set can tell from noise. Read alone, the mean would say
"no clear regression." But six **per-query** floors break outright (A2, A4, A6, A10, C1, S1), by
margins from 0.02 to 0.19, well past the 0.005 tolerance. `HeldOutQueries_HoldTheirPinnedNdcg5Floor`
exists precisely to catch what a mean this small can hide, and it would go from 0 failures to 6 if
the flag shipped on today.

### F5 — Parity gate: the aggregate holds, individual queries move by up to ±0.23 in both directions [MEASURED]

`ParityGateTests`' own population (68 real-world queries, `ManagedHarness.DefaultPoint`, k=60,
weights 1:1), nDCG@10 against the vendored golden reference:

| | Mean nDCG@10 | Delta vs. reference |
|---|---:|---:|
| Reference (golden) | 0.625101 | — |
| New side, flag OFF | 0.666860 | +0.0418 |
| New side, flag ON | 0.666414 | +0.0413 |
| ON vs. OFF | — | -0.0004 |

Both flag states clear `ParityGateTests.NdcgParityDelta` (0.02) by a wide margin, and the flag
moves the aggregate by essentially nothing (-0.0004). Per-query movement is not nothing: the worst
regression is `doc-jsaa-adr-0023-local-otlp-telemetry-and-instrumentation-set` at -0.1577, and the
best improvement is `cluster-event-grid` at +0.2334, with a long tail of ±0.05-0.10 moves on both
sides that roughly cancel in the mean. The gate that measures the aggregate would pass; a gate
measuring individual queries (this corpus has none at that grain) would show real churn.

**Evidence:** a scratch test in the `managed-parity` collection reusing `ManagedHarnessFixture`,
flag toggled via `fixture.Harness.Store.SetSettingAsync`, same corpus and embeddings both passes,
output captured 2026-09-24 (not committed).

## New test

`tests/AiRaccoon.Tests/Integration/FusionNoRegressionFlagGateTests.cs`,
`Sweep_FlagEnabled_HoldsExceptTheMeasuredA9S4S5S6Regressions`: runs gate (c) with the flag forced
on and asserts every query outside the measured/pinned set still holds hybrid <= best single leg,
and that A9/S4/S5/S6 hold their exact measured ranks (3/3/7/3). Mutation: setting the flag to
`"false"` inside the test makes S4's pinned rank assertion fail (measured 1, expected 3), so the
test goes red when the reorder is disabled, and green when it runs. RED then GREEN pasted below.

RED (mutation: flag forced to `"false"`):

```
failed AiRaccoon.Tests.Integration.FusionNoRegressionFlagGateTests.Sweep_FlagEnabled_HoldsExceptTheMeasuredA9S4S5S6Regressions (2s 439ms)
  Shouldly.ShouldAssertException : pinnedRanks[id]
      should be
  3
      but was
  1

  Additional Info:
      S4: flag-enabled hybrid rank drifted from the 2026-09-24 measurement (3) -- re-measure and update docs/work/2026-09-24-fusion-no-regression-flag-measured.md
```

GREEN (flag forced to `"true"`, the committed state):

```
Test run summary: Passed! - AiRaccoon.Tests.dll (net10.0|arm64)
  total: 1
  failed: 0
  succeeded: 1
  skipped: 0
  duration: 1s 552ms
```

## Recommendation

**Keep the flag off pending telemetry. Do not change `FusionConfigKeys.DefaultNoRegressionEnabled`.**

This is not the hedge ADR-0078 already made for lack of held-out capacity: this record measured
the flag with a held-out set nineteen times the size ADR-0058 had (19 queries instead of 3), and
the answer it gives is negative on its own terms. At the chosen production configuration:

- It does not solve the problem it targets. A9 stays at hybrid rank 3, unchanged, because the
  reorder's own position bound already caps a rank-2 single-leg winner there once two distinct
  chunks claim rank 1 on the two legs (F2). Dropping the A9 exclusion on the strength of this flag
  would be dropping it on nothing: the exclusion still has to hold, because the number it excludes
  did not move.
- It breaks a gate that currently passes. `HeldOutQueries_HoldTheirPinnedNdcg5Floor` would fail on
  six queries it holds today (F4), by margins between 0.02 and 0.19, and the reason a query-level
  gate exists at all is to catch exactly what a comfortable mean-level delta (-0.018, inside the
  ±0.03 noise band) hides.
- It leaves the parity gate's aggregate unmoved (-0.0004, F5) while reshuffling individual queries
  by up to ±0.23 in both directions, which is consistent with ADR-0078's own admission that the
  rule can raise one result only by lowering another, not with a rule that is safe to flip.

Two things follow from this, not from a wish to close the issue anyway:

1. **The issue's premise does not hold for A9 specifically.** #706 asked whether *this* flag,
   measured, resolves the A9 exclusion. It does not (F1, F2), so the exclusion in
   `RrfParameterSweepTests` stays, unchanged, at its existing ceiling of 3.
2. **A fusion change that outranks both single-leg rank-1 winners for the sake of a rank-2 winner
   is a different, stronger rule than ADR-0078's**, and this task's scope was to measure the
   existing flag, not design a new one (see the task's own instruction: stop after the record if
   the flag does not get A9 to <= 2). That candidate is not designed or measured here.

The telemetry ADR-0078 shipped for (`search.fusion.top1_changed`/`top1_rank_delta`/`top5_moved`
joined against `search_quality` grades) is still the right instrument for deciding this on real
traffic, and nothing here substitutes for it. What this record adds is that the offline gates this
project already has are decisive enough, on this measurement, to say "not yet" rather than "we
cannot tell."
