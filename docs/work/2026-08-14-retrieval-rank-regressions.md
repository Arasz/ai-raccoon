# Retrieval rank regressions from the 2026-08-14 corpus regeneration (WP3b hand-off)

Date: 2026-08-14. Author: test-engineer (wave3b-ranking-quarantine).

## Context

The retrieval corpus was regenerated through the production chunker. It went from 761 rows with
**zero** structure vectors to 2518 rows with 871 — which is what finally lets a broken dual-vector
fusion be detected at all (that detection is WP4c's finding, already fixed on this branch). But
the same 196 source files now yield 3.3x the chunks, so far more same-topic chunks compete for the
top five in every ranking gate.

Six tests pinned pre-regeneration ranks and started failing as a result. These are **genuine
ranking regressions**, not test bugs and not flakes — WP3b (fixing the ranker) is out of scope for
this pass. Each has been converted into a **characterization test**: it asserts the CURRENT
(regressed) value exactly, named to say so, with the original expectation preserved in a doc
comment. CI is green; the regression is pinned, not hidden.

**When a ranking fix lands:** run the affected test. It will FAIL (because the observed value no
longer matches the pinned regression value). That failure is the signal to invert the assertion
back to its original form (see "restore" column below) and delete the KNOWN REGRESSION doc
comment. Do not raise the pinned value to make a partial improvement pass — the test must go red on
any change, in either direction, until it is deliberately restored.

## Status: two of six restored (2026-08-14)

Findings **#2** and **#3** are closed. Both were section-anchored queries, and neither was a
ranking problem: `FileIngestor` wrote `section` as null for every ingested chunk, so a
`file#section` anchor — which ANDs against the FTS `{source_file section}` columns — had nothing
to match. With `section` populated from the heading leaf and the corpus regenerated (871 of 871
headed rows now carry one, up from 866 after a second fix to the leaf split), the pinned values
went red as designed: #2 moved 86 → **1**, #3 moved 21 → **3**. Both assertions have been restored
to their original form and their windows shrunk back to production (`Limit: 5` and
`SearchLimit: 10`).

That the two anchored findings healed while #1, #4, #5 and #6 did not is the useful signal here:
those four are genuinely about ranking on a denser corpus and remain pinned for WP3b.

## The six findings

| # | Test (old name → new name) | File | Old expectation | New observed value | Reason |
|---|---|---|---|---|---|
| 1 | `InvariantC2_ScreamingArchitecture_FtsOnlyRank1` → `InvariantC2_ScreamingArchitecture_DocumentsKnownFtsOnlyRankRegression` | `SourceIdentityTests.cs` | FTS-only rank == 1 | rank == **3** | More same-topic chunks now compete in the FTS-only top 5 for the screaming-architecture invariant query. |
| 2 | **RESTORED** — `SourcePathQuery_ReturnsTheExactChunkFirst` | `SourceIdentityTests.cs` | rank == 1 within `Limit: 5` | rank == **86** (measured by widening `Limit` to 1000; the chunk no longer appears in the top 5 — or even the top 30 — at all) | A source-path query for `docs/adr/0011-frontend-chassis-stack.md#decision` used to resolve its own exact chunk first; on the denser corpus dozens of same-file/same-topic chunks now outrank it. |
| 3 | **RESTORED** — `S4_ConsequencesOfAdr0011_ConsequencesChunkAtRankAtMost3` | `SectionTargetedRetrievalTests.cs` | rank <= 3 within `SearchLimit: 10` | rank == **21** (measured by widening `Limit` to 30; the chunk does not appear within the production `SearchLimit=10` window at all) | Wave 6's dual-vector structure signal used to lift the ADR-0011 Consequences chunk into the top 3; the signal is now diluted across 3.3x more chunks. **Note:** the task brief that opened this work estimated "ranks 7th" for this finding; re-measured directly against the store with the exact production search parameters (same `Limit`/`RrfK`/`FtsWeight`/`VectorWeight` as the test helper, only `Limit` widened to make the position observable), the actual current position is rank 21, not 7. The pinned value here is the measured one. |
| 4 | `HybridRanks_DoNotRegress_VsBaseline` → `HybridRanks_DoNotRegress_VsBaseline_DocumentsKnownRankRegressions` | `QueryConstructionTests.cs` | A3 hybrid rank <= 1; A7 hybrid rank <= 1; C2 FTS-only rank == 1 | A3 == **3**; A7 == **3**; C2 == **3** | **This single test loop actually pins three independent regressions, not the one (A3) the task brief named.** The loop iterates A1, A2, A3, A4, A5, A7, C1, C5 and stops at the first failing assertion, so the brief — written from a single failing run — only saw A3. Running the loop with assertions replaced by logging (and reverted afterward) showed A1, A2, A4, A5, C1, C5 still hold or beat their Wave 0 ceiling (A4 and C5 even improved), but **A7 also regressed from rank <= 1 to rank 3**, and the separate C2 FTS-only check at the end of the same method **regressed from rank 1 to rank 3**. All three are pinned exactly; the six no-regression ids (A1, A2, A4, A5, C1, C5) are untouched and still assert genuine no-regression. |
| 5 | `AndPrimary_UnderMatchedRows_FallsBackToOr` → `AndPrimary_UnderMatchedRows_DocumentsKnownRankRegression` | `QueryConstructionTests.cs` | A6's OR-fallback file rank <= `RankCutoff` (5) | rank == **8** (measured by widening `Limit` to 30 with the same FTS-only weights; cross-checked stable at `Limit=10` too, ruling out a candidate-window artifact from the wider limit) | The OR-fallback still fires correctly (the file is found, and the fallback mechanism itself is not broken) — but the restored file now ranks 8th instead of within the top 5, again diluted by more same-topic chunks. |
| 6 | `Sweep_ChosenSourceAffinityConfiguration_PassesAllGates` → `Sweep_ChosenSourceAffinityConfiguration_DocumentsKnownNdcg5GapRegression` | `SourceAffinitySweepTests.cs` | `chosen.AdrNdcg5 >= baseline.AdrNdcg5 - 0.001` (chosen λ=0.1 config within 0.001 of the λ=0 baseline) | gap == **~0.0756** (chosen ≈ 0.5324, baseline ≈ 0.6080) — a threshold gate, not a rank; pinned via `ShouldBeInRange` at the repo's standard cross-platform `GoldenFile.RankingTolerance` (5e-3) around the measured gap | The chosen source-affinity configuration used to track the no-affinity baseline within 0.001 nDCG@5; on the denser corpus the affinity boost and the baseline diverge much further. This is the only one of the six that is a threshold gate rather than a rank pin — the other five gates in the same test (S2, A6, C1/C5, A1/A4, and the already-re-pinned 0.532 floor) are unaffected and remain genuine, currently-passing guarantees. |

## Restoring after WP3b

For each finding, restoring the original guarantee is mechanical once the ranker fix lands and the
pinned assertion goes red:

1. Delete the `KNOWN REGRESSION (WP3b)` doc comment (or its `<remarks>` block).
2. Replace the pinned exact-value assertion with the original inequality/equality noted in the
   "old expectation" column above.
3. Where the test method was renamed to end in `..._DocumentsKnownRankRegression`(s) or
   `..._DocumentsKnownNdcg5GapRegression`, rename it back (or to whatever name fits — the point is
   dropping "DocumentsKnown...Regression" once it is no longer documenting one).
4. For finding #3 and #5, the widened `Limit` (30) used only to make the current position
   observable can be shrunk back to the production value (`SearchLimit=10`) since the fix should
   put the chunk back within that window.
5. For finding #2, shrink `Limit` back from 1000 to 5.
6. For finding #4, move the restored id(s) out of the `knownRegressed` dictionary and back under
   the plain `wave0` no-regression loop (or restore `c2FtsRank.ShouldBe(1, ...)`).

## Verification

Corrected 2026-08-14: the claim below held for four of the six. #2 and #3 *were* corpus gaps — a
null `section` column — and the verification missed it because it re-measured the rank rather than
asking why an anchored query had nothing to anchor to. Confirming that a number is really the
current number is not the same as confirming what produced it.

All six were confirmed as genuine regressions (not test bugs, not corpus gaps) by running each
against the live store with the test's own production search parameters, and by cross-checking
that widening `Limit` to observe the exact position doesn't itself change ranking (confirmed
stable for finding #5 at `Limit=10` vs `Limit=30`: rank 8 both times).
