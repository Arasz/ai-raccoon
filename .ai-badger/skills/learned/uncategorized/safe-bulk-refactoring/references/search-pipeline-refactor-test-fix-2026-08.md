# Search-Pipeline Refactor Test Fix — Worked Example (2026-08-20)

Refactor `36b766fe "refactor: clean up memory store search implementation"` (AiRaccoon) moved the search pipeline into `Sqlite/Memory/`, removed the second RRF pass from
`SearchResultMerger.Merge`, split `SearchTimings.Affinity` into `Merge` + `Adjustment`, and introduced a `SearchResult` record hierarchy (`Fts/Vector/Fused/Adjusted/Deferred/Merged`),
`WeightedResults`, and an infrastructure `SearchResults` batch collection (`Indexes` + `Vector`/`Fts` legs + `AddResults`). Task: adjust tests only; report the rest.

## API-adjustment moves that worked

- `SearchTimings` 10-arg constructor — update call sites AND the name→value assertions; sequential values (1..10) keep the mapping test meaningful.
- Same-named types in two imported namespaces (`AiRaccoon.Core.Memory.SearchResults` envelope vs `AiRaccoon.Infrastructure.Sqlite.Memory.SearchResults` batch) → `using SearchResults =
  ...` alias in the test file.
- Derived record with `required` members that are pipeline plumbing, not test inputs (`FusedSearchResult.VectorCandidates/FtsCandidates`) → pass the instantiable BASE record (`new SearchResult(list, TimeSpan.Zero)`) in unit tests instead of
  bloating every call site with required-member initializers.
- Tuple collection expressions → positional records (`(fts, 2)` → `new WeightedResults(fts, 2)`).

## The four runtime-failure categories (32 failures total)

1. **Compiler-invisible mechanical fallout** — `SqliteMemoryStoreSizeRatchetTests` hardcoded
   `Sqlite/SqliteMemoryStore.cs` (file moved to `Sqlite/Memory/`; 996 lines < 1066 cap — fix was the path alone). `NoHandRolledCryptoTests` content-addressing whitelist had the old path. Fixed as pure test adjustments.
2. **Behavior pinned by the old pipeline** — 9 tests pin the removed second RRF pass (ADR-0058's own delete-signal; the test doc comment literally says "when this pass is removed this test FAILS, and that failure is the signal to delete
   it"). All-zero-rank fixtures now hit 0/0 = NaN → filtered out → empty result (e.g. served list empty instead of reordered).
3. **Quality gates red = real regression** — 7 retrieval gates: held-out nDCG@5 dropped 0.2796 → 0.257; RRF parameter sweep + no-fusion-regression guarantees failed. Floors were measured on the pre-refactor pipeline; re-pinning without
   investigating hides the regression.
4. **Pre-existing / environmental** — 12 Bitwarden CLI BDD tests fail with
   `bws: invalid access token` (host has bws with a bad token). Verified identical on
   `36b766fe^` via a scratch worktree: `git worktree add /tmp/base-check 36b766fe^`, filtered
   `dotnet test`, 12/15 failed there too, `git worktree remove --force /tmp/base-check`.

Also: full-suite stall mid-run with an idle testhost and a spawned
`AiRaccoon --data-root .../model-migration-crash/...` server alive. Verdict via isolation:
run the named class alone (`--filter FullyQualifiedName~ModelMigrationCrashRecoveryE2ETests`)
→ 2 passed / 1 skipped / 0 failed. Passes alone = transient full-suite interaction, not a deterministic refactor defect.

## Handoff

User cut the gate short ("just merge it to main, I'll take over"): committed adjustments +
`docs/work/2026-08-20-tests-after-memory-store-refactor.md`, merged with `--no-ff` (main had moved by one docs commit; disjoint files), closed task tracking, reported the categories. The report, not a green suite, was the deliverable.
