# AiRaccoon search-pipeline refactor — test-fix session (2026-08-20)

Session record for fixing tests after the memory-store search refactor (`refactor: clean up memory store search implementation`, re-committed several times as 36b766fe → d91de787 → ab6a87e2). The pipeline was reworked into `Sqlite/Memory/`
partials:
`SearchResult` hierarchy, `SearchResults` batch collection, `ModalityCandidates`,
`ReciprocalRankFusion` with `WeightedResults`, `SearchResultMerger`, `SourceAffinityRanker`,
`ByHashIndex`, `ContextFilterProvider`, `FtsQueryNormalizer`, `SearchQueryExtensions`.

## The two code bugs found (both fixed; both are the "code bug, not test bug" class)

### 1. FTS OR-fallback re-query rebinds the primary AND expression

Symptom: a two-token query whose terms never co-occur in one chunk (e.g. BDD scenario
"search for 'fact finding'" over entries "committed fact" and "draft finding") returned **0 results** — the OR fallback never fired. Multi-token retrieval silently degraded.

Root cause: `DynamicParameters.SearchParameters(query, plan, ...)` binds `@query =
plan.Expression` (the AND). The fallback branch in `FtsSearch` re-created the SAME parameter set, so the "fallback" re-ran the identical AND query. `plan.Fallback` (the OR join) was computed but never executed. The old (pre-refactor) `SearchParameters(string ftsExpression,
...)` took the expression as an argument, so the fallback call site passed `plan.Fallback!`.

Fix: a dedicated binder that overrides `@query` with the fallback expression —
`DynamicParameters.FallbackSearchParameters(query, plan, queryVector, contextFilter)` — used only at the fallback re-query call site. Also thread the actually-used expression into
`QueryFtsBatchAsync` so `ByHashIndex.FtsQueryByHash[row.Hash]` carries the expression the row was matched by (snippet re-resolution `ResolveFtsSnippetsAsync` groups by that text and re-runs
`MATCH @query` against the survivors' rowids; storing the AND text for OR-matched rows would silently degrade native snippets to the C# fallback).

### 2. Merge convenience overload used the candidate-window size as the final limit

Symptom: `Limit: 3` searches returned up to 100 results (e.g. HybridSearchTests
"3 survivors out of 8 candidates" got all 8).

Root cause: the `SearchResultMerger.Merge(results, query, plan)` convenience overload passed
`searchQuery.LimitForCandidateWindow` — `max(limit*3, 100)`, the per-modality candidate depth — as the final `limit` into the core overload. The candidate window is a query-depth parameter, not the served result count.

Fix: pass `searchQuery.Limit`. Verify by searching any convenience overload of a fused method for `LimitForCandidateWindow` — it belongs in the SQL parameter builders (`DynamicParametersExtensions`), never in a final merge/limit.

## Phase measurement order (SearchTimings, nine phases)

`SearchAsync` measures phases in this GetTimestamp call order (per-context loop queries **vector before fts** — the refactor swapped the old fts-first order):

    open → embed → vector → fts → fusion → merge → adjustment → snippets → bump

`SearchTimings` is now a 10-field record (Open, Embed, Fts, Vector, Fusion, Merge, Adjustment, Snippets, Bump, Total). `PhaseNames` still labels index 5 "search.affinity" while `Phases()`
maps it to Merge — implementation quirk, don't "fix".

## Scripted phase-timing tests (SqliteMemoryStoreSearchTimingsTests)

- A scripted `TimeProvider` that scripts one delta per phase bracket shifts EVERY value when the pipeline gains a phase or reorders legs. The observed failure value reveals the true call order: with 8 scripted values on the 9-phase
  pipeline, Fts read 33ms (the value scripted for vector) instead of 22ms — i.e. vector is measured before fts.
- Phases that only measure when they have work read `TimeSpan.Zero` on an empty bank:
  `ResolveDeferredSnippetsAsync` early-returns `new DeferredSearchResult([], TimeSpan.Zero)`
  when no result has an unresolved snippet, and the access bump brackets no work. Fix the fixture, not the assertion: seed one matching entry so the deferred-snippet path and bump actually close their brackets.
- Seeding is script-safe when the write path uses `GetUtcNow` (base `TimeProvider`) rather than `GetTimestamp` — verify with a grep before trusting the script alignment.

## Merge contract after the user's fusion restore

`SearchResultMerger.Merge` re-fuses the single incoming list with RRF at `query.RrfK` (the owner chose to keep the ADR-0058 "second fusion" rather than remove it; unit tests pinning the positional closed form `(k+1)/(k+rank)` are expected
to pass again). Consequences:

- Tests written for the OLD multi-batch Merge (`Merge([shared, project], 10)` promoting a dual-retrieved doc) can no longer be expressed as a single list — concatenating batches double-counts shared hashes and changes the math (`62/123`
  assertions are stale). Rewrite them to fuse the legs first (`ReciprocalRankFusion.Fuse([WeightedResults(shared,1),
  WeightedResults(project,1)], k, 0, 10)`), then Merge the fused list, and recompute expected values.
- At the default k=60 the single-list positional scores 1.0, 61/62, 61/63 all clear a 0.9 min-score floor — assertions like "only the top two clear 0.9" (k=10 math) are stale.

## Environmental/pre-existing (verified on the parent commit, not refactor-caused)

All Bitwarden/encryption BDD scenarios fail with `bws: invalid access token` on this host (bws installed, stale token). Verified identical failures by running the filtered tests in a scratch worktree at the refactor's parent commit (12/15
failed there too). Categories:
`Category=Integration` runs are the right gate sweep; `Category=BDD` includes the bws family.

## Workflow notes

- Compile-error inventory: `dotnet build 2>&1 | grep -oE "tests/[^ ]*\.cs\([0-9]+,[0-9]+\):
  error CS[0-9]+" | sort -u` — deduped locations, fixes map 1:1.
- The user may re-commit/amend the refactor mid-session (HEAD moved three times here: d91de787 → ab6a87e2, with user fixes landed on top). Before committing, `git log` + `git status`; before patching a file, honor the patch tool's "modified
  since last read" warning — a duplicate method was created once because the user had already added the same helper.
