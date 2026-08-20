# Failing tests after the memory-store search refactor (36b766fe)

Date: 2026-08-20. Scope: adjust tests to the new search API introduced by
`36b766fe "refactor: clean up memory store search implementation"` — no implementation changes.
Full `dotnet test` run on the worktree; 32 unique failures observed.

## What was adjusted (compile/API level)

- `SearchTimings` gained a 10th field (Affinity split into Merge + Adjustment) —
  constructors and phase-value assertions updated in `SearchResultsTests`.
- `ModalityCandidates.ByBm25/ByCosine` now take the infrastructure `SearchResults`
  batch (vector/fts legs + `Indexes.ValueByHash`) instead of per-context lists.
- `ReciprocalRankFusion.Fuse` takes `WeightedResults` records instead of tuples.
- `SearchResultMerger.Merge` / `SourceAffinityRanker.Rank` take a single
  `SearchResult` instead of a list of batches.
- `SqliteMemoryStoreSizeRatchetTests` path updated for the file move to
  `Sqlite/Memory/SqliteMemoryStore.cs` (996 lines, under the 1066 cap).
- `NoHandRolledCryptoTests` content-addressing whitelist updated for the same move.

These two path fixes were refactor fallout and now pass. Everything below still fails after the
API adjustment and needs a decision — none of it is fixable from the test side alone.

## Failing tests — probable reason

### A. Second RRF pass removed from `SearchResultMerger.Merge` (9 tests)

The refactor deleted the internal re-fusion (the ADR-0058 "redundant second fusion"); Merge now
ranks the already-fused list directly. Tests that pinned the old two-pass behavior fail:

- `SearchResultMergerTests.Merge_RebuildsScoresFromRankPosition_DiscardingTheFusedScores`
- `SearchResultMergerTests.Merge_FloorComparesAgainstThePositionalCurve_NotMatchQuality`
- `ScoreInjectionTests.InjectedMagnitude_IsDiscardedByTheSecondFusion`
- `ReorderSurvivalThroughMergeTests.Merge_AdjacentChunkBoost_CanOverrideTheReorderedTopResult`
- `ReorderSurvivalThroughMergeTests.Merge_OverAReorderedList_ServesDistinctRankings`
- `ReorderSurvivalThroughMergeTests.Merge_OverAReorderedList_WithNoSiblings_ServesTheReorderedOrder`
- `SqliteMemoryStoreTests.Merge_RrfAcrossContextBatches_PromotesDualRetrievedDocs_AndNormalizesToMax`
- `SqliteMemoryStoreTests.Merge_SingleContextBatch_KeepsItsOrderAndNormalizesTopToOne`
- `SqliteMemoryStoreTests.Merge_AppliesMinScoreAfterNormalization`

Probable reason: scores are no longer rebuilt from rank position. At λ=0 the input list passes
through untouched (no normalization to 1.0, floor compares against the real fused scores), and
unit fixtures with all-zero rankings hit `0/0 = NaN`, which the min-score filter drops — hence
the empty served list in `WithNoSiblings`. The `Merge_RebuildsScores...` test documents itself
as the signal to delete when this pass is removed (ADR-0058).

### B. Search phase timings restructured (2 tests — RESOLVED)

- `SqliteMemoryStoreSearchTimingsTests.Search_AttributesEachPhasesElapsedTime_ToThatPhaseAndNoOther`
- `SqliteMemoryStoreSearchTimingsTests.Search_PopulatesEveryPhaseThatRan_AndLeavesTheSkippedModalityAtZero`

Probable reason (original): the pipeline now measures nine phases (open, embed, vector, fts,
fusion, merge, adjustment, snippets, bump) — the scripted provider scripted eight, shifting every
value (observed: Fts 22ms → 33ms); the per-context loop also swapped to vector-before-fts, and
`ResolveDeferredSnippetsAsync` returns `TimeSpan.Zero` on the empty-deferred path, so a search
with no results measured Snippets = 0 where the old code measured the span unconditionally.

Resolved 2026-08-20: both tests adjusted to the new pipeline — the scripted test now scripts nine
values in the measured call order (open, embed, vector, fts, fusion, merge, adjustment, snippets,
bump) and the "every phase that ran" test asserts the new Adjustment phase; both seed one matching
entry so the deferred-snippet path and access bump actually do work (their timings are only
non-zero when they have work). Class now 4/4 green.

### C. Retrieval quality gates (7 tests)

- `HeldOutRetrievalGateTests.HeldOutMean_HoldsItsFloor` (nDCG@5 0.257 vs 0.2796 floor)
- `HeldOutRetrievalGateTests.HeldOutQueries_HoldTheirPinnedNdcg5Floor`
- `HeldOutRetrievalGateTests.ReversedRanking_FailsTheHeldOutMeanFloor`
- `QueryConstructionTests.HybridRanks_DoNotRegress_VsBaseline_DocumentsKnownRankRegressions`
- `RrfParameterSweepTests.Sweep_ChosenRrfConfiguration_PassesAllGates`
- `SqliteMemoryStoreFusionFlagTests.Search_FlagEnabled_RanksTheSingleLegWinnerAtLeastAsWellAsItsBestLeg`
- `SqliteMemoryStoreFusionFlagTests.Search_FlagEnabled_RecordsTheDifferenceBetweenTheBaselineAndTheServedOrder`

Probable reason: the pipeline change alters served rankings (floor now applies to the true fused
scores, affinity boost adds to them directly). The held-out nDCG@5 floor dropped ~2.3 points —
the gates are doing their job, and the baselines were measured on the pre-refactor pipeline.
Re-pinning the floors without investigating the regression would hide it.

### D. Pre-existing, environment-dependent (12 tests — NOT caused by this refactor)

`bws missing at server start fails loudly`, `Network failure at server start fails loudly`,
`Interactive config persists project and secret ids`, `Config validates secret reachability`,
`The optional -t token is used for the run and never persisted`, `A wrong key is rejected`,
`An RSA private key is rejected`, `A passphrase-protected SSH key is rejected`,
`A bank encrypted with the env passphrase reopens with the derived key after rekey`,
`encryption bitwarden prints the rotation warning`, `encryption show prints the current source`,
`encryption unset returns to the env default`.

Probable reason: this host has `bws` installed with an invalid access token; the Bitwarden BDD
scenarios fail with `bws: invalid access token` during setup. Verified identical failures on the
pre-refactor commit `36b766fe^` (12/15 filtered tests fail there too).

## Observation: suite hang (resolved)

The full run stalled after ~3.5 min of test time with the host idle; a spawned
`AiRaccoon --data-root .../ai-raccoon-model-migration-crash/...` server process from
`ModelMigrationCrashRecoveryE2ETests` stayed alive. A targeted re-run of that class in isolation
afterwards PASSED (2 passed, 1 skipped, 0 failed, ~78 s), so the hang does not reproduce on its
own — likely a transient full-suite interaction (parallel collection load/port contention)
rather than a refactor-caused defect. Not re-verified inside a full run.

## Next step

All remaining failures need an implementation-side decision (restore normalization/behavior,
update the ADR-0058 tests to assert the new single-fusion contract, re-pin or investigate the
retrieval gates) — out of scope for "adjust tests to the new API".
