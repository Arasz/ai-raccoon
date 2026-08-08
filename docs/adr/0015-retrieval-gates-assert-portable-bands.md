# 0015 — Retrieval gates assert portable bands, not machine-exact pins

Date: 2026-08-08

Status: Accepted

## Context

The retrieval gates (`GoldenFileTests`, `SourceIdentityTests`,
`RrfParameterSweepTests`, `SourceAffinitySweepTests`) pinned machine-exact ranks
and a 1e-6 ranking tolerance, calibrated on osx-arm64. `ci: gate Speed=Slow on
every PR` (#977f5b1) discovered that the same code, same corpus, same model and
same native extensions produce rankings on ubuntu (linux-x64) that differ from
the osx-arm64 golden by 1e-4..3e-3 per hit — different SIMD paths through the
GGUF inference. Nightly CI (ubuntu) had therefore failed on every run it ever
had; that commit patched over it with an `Assert.Skip` on non-arm64 hosts,
which only hides the gap rather than closing it — pins calibrated once and then
run on a single machine is exactly how the four red ingest tests behind #126
merged unnoticed.

Measured on run 31240800975 (2026-08-08, main@0d92bdb):

- `GoldenFileTests.GoldenFile_MatchesFreshReferenceRun`: 680/680 hits differ —
  same hash and path, ranking moved by ~1e-4..3e-3 (e.g. expected 0.748943, got
  0.751670), plus adjacent swaps between hits within ~0.001 of each other, and
  k-boundary substitutions where a doc just outside the top-10 swapped with one
  just inside.
- `SourceIdentityTests.InvariantQueries_C1C5_HoldMeasuredHybridRanks` ("Are
  hardcoded secrets allowed?", expectedRank 5): measured rank 3 on linux-x64 —
  better than the pin, but the assert was equality.
- `RrfParameterSweepTests.Sweep_ChosenRrfConfiguration_PassesAllGates`:
  `chosen.C5ExactRank` measured 3, pinned to `ShouldBe(5)`. The sweep's own
  internal gate-selection logic (`RrfParameterSweepTests.cs:370`) already
  treats C5 as a ceiling (`C5ExactRank is null or > 5`) — only the final assert
  hardened it to equality.
- `SourceAffinitySweepTests.Sweep_ChosenSourceAffinityConfiguration_PassesAllGates`:
  A6 measured rank 8 on linux-x64 against a `<= 6` band calibrated on arm64.

## Decision

Retrieval gates assert portable bands:

- `GoldenFile.Differences` compares golden vs. fresh top-k by hash **set**, not
  position, with a ranking tolerance of `GoldenFile.RankingTolerance = 5e-3`
  (covers the measured 1e-4..3e-3 spread). A hash present on only one side is a
  difference unless its ranking sits within tolerance of the golden top-k's
  last (k-th) ranking — that absorbs boundary substitutions where a near-tie
  crosses the cut. Order is not asserted separately: rankings are the sort
  key, so set equality plus the ranking tolerance already constrain order
  except among genuine near-ties.
- The osx-arm64 `Assert.Skip` guard is removed; `GoldenFileTests` now runs on
  every platform.
- Rank assertions that were measured-value equalities become ceilings
  (`ShouldBeLessThanOrEqualTo`): `SourceIdentityTests.cs`,
  `RrfParameterSweepTests.cs`. A result better than the pin no longer fails
  the gate.
- `SourceAffinitySweepTests`'s A6 band widens from `<= 6` to `<= 8`, the
  measured cross-platform envelope (arm64 <= 6, linux-x64 8, 2026-08-08).

## Consequences

- **Negative:** a regression smaller than the band (5e-3 ranking, or a rank
  ceiling several positions wide) is invisible on the calibration platform —
  that resolution was the entire point of the original 1e-6 tolerance and
  exact-rank pins. The gates now catch structural regressions (wrong document,
  wrong engine/model, a rank moving outside its ceiling), not fine-grained
  ranking drift.
- **Positive:** one contract is green on every platform. Nightly CI (ubuntu)
  becomes a working backstop instead of permanently red noise, and the PR gate
  no longer needs a platform-conditional skip to stay green.

## Alternatives rejected

- **Per-platform pins** (a golden file per RID). Rejected — two references
  drift independently and nobody notices when they diverge further, the same
  failure mode the shared pin already had against reality.
- **Platform skips** (the interim fix in #977f5b1). Rejected — a gate that
  only runs on one machine is a gate that runs on one machine; that is
  precisely how the ubuntu drift went unnoticed until this investigation.

## Evidence

`tests/AiRaccoon.Tests/Unit/Retrieval/GoldenFile.cs` (`Differences`,
`RankingTolerance`); `tests/AiRaccoon.Tests/Unit/Retrieval/GoldenFileTests.cs`;
`tests/AiRaccoon.Tests/Unit/Retrieval/GoldenFileComparisonTests.cs` (portable
comparison unit tests); `tests/AiRaccoon.Tests/Integration/SourceIdentityTests.cs:169`;
`tests/AiRaccoon.Tests/Integration/RrfParameterSweepTests.cs:130-132,370`;
`tests/AiRaccoon.Tests/Integration/SourceAffinitySweepTests.cs:111-118`; CI run
31240800975 (2026-08-08, main@0d92bdb, ubuntu).
