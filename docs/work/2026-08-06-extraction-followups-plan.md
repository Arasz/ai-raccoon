# Integrated implementation plan — extraction follow-ups S4/S5/S10/F3

**Date:** 2026-08-06 · **Task:** continue-improv · **Base:** origin/main @ a4b5ff9 **Source plans:** four independent
planning lanes (S4, S5, S10, F3), each verified against code.

## The four changes and their files

| Point                                           | Change                                                                                                 | Files (production)                                                                             | Tests                                             |
|-------------------------------------------------|--------------------------------------------------------------------------------------------------------|------------------------------------------------------------------------------------------------|---------------------------------------------------|
| **F3** (PRIORITY — owner: "index has priority") | Two partial UNIQUE indexes + on-open dedupe migration + `ON CONFLICT DO NOTHING` at all 3 insert sites | `MemorySchema.cs`, `MemorySql.cs`, `SqliteMemoryStore.cs`                                      | 10 (7 schema/migration RED + 3 concurrency gates) |
| **S5**                                          | OCE rethrow-filter before the per-project generic catch, keyed on the method's token                   | `ExtractionHostedService.cs` (1 catch block)                                                   | 2 (RED driver + boundary pin)                     |
| **S4**                                          | Propose mode logs per-candidate details (EventId 507, Information)                                     | `ExtractionHostedService.cs` (+ loop after Log.Pass)                                           | 2 (RED + guard)                                   |
| **S10**                                         | `SharedExtractionService.DefaultCandidateLimit = 20` single-sourced; both consumers rewired            | `SharedExtractionService.cs`, `ExtractionHostedService.cs` (:20/:104), `MemoryTools.cs` (:270) | 2 (drift pins) + optional CLI help fix            |

## Dependency map

```
F3 ──── storage layer only: MemorySchema/MemorySql/SqliteMemoryStore. ZERO overlap with S4/S5/S10.
S5 ──── ExtractionHostedService.cs:121-124 (catch) ──┐
S4 ──── ExtractionHostedService.cs:~119 (after Log.Pass) ──┤ same file, disjoint lines → parallel-safe,
S10 ─── ExtractionHostedService.cs:20/:104 + SharedExtractionService + MemoryTools ──┘ trivial rebases
```

- **No semantic coupling** between any pair. All four can land in one PR or separate PRs.
- **Ordering recommendation** (same-file edits): F3 first (priority, independent) → S5 → S4 → S10 (S5 first keeps the
  catch clean before S4 adds the loop; S10 last is the smallest).
- **No interface changes** anywhere → zero fake-store ripple (F3 verified: 9 fakes compile untouched).
- **No corpus re-pin** (F3 V9: committed jsaa corpus has zero duplicate bucket keys).

## F3 design (priority) — key decisions

1. **Index DDL** (dedupe-guarded, in `MigrateAsync`, NOT the raw `Ddl` const — a violating bank would brick on open):
    - `uq_entries_shared_bucket ON entries(path, hash) WHERE scope = 'shared'` — global across projects (closes S3's
      cross-project dup at DB level)
    -
    `uq_entries_committed_bucket ON entries(path, hash, project_id, scope, COALESCE(context_label, '')) WHERE scope IN ('project','custom')` —
    **scope in the key** (MoE amendment: COALESCE alone would merge a custom row with context_label='' into the project
    NULL-label bucket; scope restores exact BucketFor identity). COALESCE required because SQLite UNIQUE treats NULLs as
    distinct and context_label is NULL for project rows (empirically verified on scratch DB).
2. **Migration:** on-open, index-existence-guarded, dedupe-then-create in `BEGIN IMMEDIATE`, survivor = `MIN(id)`
   (earliest; content identical by construction), failure = rollback + warn-and-continue (bank never bricks; retries
   next open). FTS/vec0 triggers clean up on DELETE.
    - **MoE MUST-FIX placement:** `MigrateAsync` early-returns on healthy banks (`ftsRows == entryRows` → return at ~:
      227); the F3 block must run BEFORE that early return (restructure: the FTS-count check guards only the FTS
      rebuild; F3 runs last in its own BEGIN IMMEDIATE). Otherwise the indexes never land on the installed population.
    - **MoE SHOULD-FIX seed spec:** migration tests must seed the CURRENT-shape Ddl minus the two indexes (legacy
      triggers fire differently; the ancient `LegacyDdl` shape can make the dedupe DELETE fail on first open → heals
      only second open). Add one production-shape variant: open fresh → raw DROP INDEX both uq_* → raw-seed dups →
      reopen → assert dedupe + indexes (pins the index-existence guard as the trigger, not schema shape).
    - Dedupe SQL must replicate the index expressions exactly (GROUP BY
      `path, hash, project_id, scope, COALESCE(context_label,'')`) + `AND path IS NOT NULL AND hash IS NOT NULL` guard
      (NULL semantics differ between GROUP BY and UNIQUE; unreachable today but free).
    - No logger exists below the store: warn-and-continue = swallow silently (retry next open) with a comment, or thread
      a status out of EnsureAsync — pick swallow+comment (precedent deviation noted).
    - Sync's 4th insert path (`SyncService.cs:239` `INSERT OR IGNORE`) swallows new-index violations silently —
      document; converges on next write. Tombstone absence accepted (same hash; re-push converges).
3. **Insert path:** one SQL change — `InsertEntry` gains `ON CONFLICT DO NOTHING` (bare, since expression/partial
   indexes can't be conflict targets; empirically swallows ONLY UNIQUE/PK — NOT NULL/CHECK/FK still throw loudly); loser
   re-reads by bucket key:
    - `AddContentAsync` already re-reads by bucket key — **but must use a NEW project-agnostic shared re-read**
      (`SelectSharedEntryByPathAndHash`: `WHERE path=@path AND hash=@hash AND scope='shared' AND workspace_id IS NULL`,
      no project_id) for shared buckets — the global index's loser is the other project's row (MoE MUST-FIX; without it
      the cross-project race throws InvalidOperationException instead of deduping).
    - `WriteAsync` switches from `last_insert_rowid` to bucket-key re-read (WritePathFor is content-unique; pooled
      connections persist last_insert_rowid so the stale rowid is a real corruption risk — load-bearing change, needs
      its own concurrency gate).
    - `InsertChunksAsync` gains `SelectChunkIdByPathAndHashInBucket` re-select (closes the audited 14.2% multi-watcher
      chunk-dup race).
4. **Risks accepted:** sync replica may push a dup back → converges next write; same-path-different-content race is
   semantic conflict, not the audited duplicate; MIN (id) keeps the earliest row's OWN rating/access history (dup's
   history discarded, not merged — reworded claim); F3 tests are Slow/Integration → local full suite + nightly only
   (repo norm).
5. **New concurrency gates (MoE amendments):** `ShareAsync_ConcurrentSameHash_DifferentProjects_SingleSharedRow`
   (cross-project variant of gate 9) and `WriteAsync_ConcurrentSameContent_SingleRowNoThrow`.

## S5 design — Option A (guarded rethrow)

```csharp
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
catch (Exception ex) { Log.ProjectFailed(_logger, projectId, ex); }
```

- House idiom (matches ExecuteAsync :41-44 and ReadIntervalSafeAsync :62-65); narrower than the literal Option B (only
  OCE-with-cancelled-token changes behavior; B would re-route ALL exception types at shutdown from 503 to 505).
- Test 1 injects a BARE `OperationCanceledException` (no token) — pins that the filter keys on the METHOD's token, not
  the exception's. RED driver must cancel the token passed to RunOnceAsync BEFORE invoking (else the pin stays green
  forever). Baseline: 9 existing [Fact]s → 9/11 RED → 11/11 GREEN.
- Tests 1+2 don't discriminate A vs B (both pass under either) — the choice rests on the semantics argument (correct);
  noted, not a defect.

## S4 design — per-candidate propose logs

- EventId 507, Information, propose mode only (`if (mode == ExtractMode.Propose)`), one message per candidate: `Rank`
  (loop index+1 — list is pre-sorted by score; ShareCandidate carries no Rank and must not gain one), `ProjectId` (outer
  variable), `Path`, `Reasons` (pre-joined), `Preview` (reuse `ValuePreview`, already 300-char truncated).
- **No Core changes** — `ShareCandidate` carries no Score and must not gain one (would ripple the MCP tool contract).
- **Test seam (MoE MUST-FIX):** `NewStack(FakeLogger<ExtractionHostedService>)` — the NON-generic FakeLogger does not
  implement ILogger<T> (proven CS1503 in review); generic variant matches the ctor and exposes Collector. Fresh
  collector per instance → no cross-test pollution.
- **Rank ordering pin (MoE SHOULD-FIX, mandatory):** seed 2 qualifying rows; assert `#1` precedes `#2` in AllRecords
  order.
- Docs: one-line addition to the reference doc extract block; help/README/class doc already promise exactly this (option
  (a) makes them true). Security note: propose mode is the DEFAULT and logs 300-char memory excerpts at Information to
  stderr — acceptable for a local single-user tool IF stderr isn't shipped off-host (journald/Docker); one-line docs
  note covers it.

## S10 design — Option B (shared constant)

- `SharedExtractionService.DefaultCandidateLimit = 20`; delete `ExtractionHostedService.CandidateLimit`;
  `MemoryTools.cs` → **`int? limit = null` param default** + `limit ?? SharedExtractionService.DefaultCandidateLimit`
  (MoE amendment: the literal `= 20` param default shadows the constant — `null` default makes the constant the TRUE
  single source; behavior-identical today).
- Rejected Option A (settings key): adds operator-tunable divergence for a knob with no demonstrated need; B is
  drift-proof by construction.
- Bonus pre-existing drift found: `CliCommandTree.cs:191` interval help still says `default 60` (PR #58 changed to 30) —
  one-line fix in this PR.
- Tool-side per-project cap (8 projects × 20 = up to 160 returned) is pre-existing semantics, out of scope; one line in
  the PR body.

## Gates (per point)

- F3: RED 7 schema tests → GREEN → concurrency gates 3 → targeted (SqliteMemoryStoreTests, SchemaTests,
  ConnectionFactoryTests, RetrievalBaselineTests) + BDD + full suite + build 0 warnings
- S5: RED 9/10 → GREEN 10/10 on the extraction filter → full suite
- S4: RED 1 → GREEN → full suite
- S10: RED 2 drift pins → GREEN → grep `CandidateLimit`/`limit ?? 20` zero hits → full suite
- Final: full suite on the integrated result (review every join), fresh-install local dress rehearsal, one PR (or
  per-point PRs), review record update.

## Open items for the MoE review

1. F3: full scope (both indexes + 3 sites) vs minimal (shared index + AddContentAsync only)? Recommend full.
2. F3: keep-earliest MIN (id) dedupe rule OK? No tombstones on dedupe deletes OK?
3. S5: Option A (guarded rethrow) vs the finding's literal Option B (negated catch)? Recommend A.
4. S4: promote-mode Debug logs (EventId 508) or counts-only? Recommend counts-only.
5. S10: constant home `SharedExtractionService` vs `ExtractionConfigKeys`? Recommend SharedExtractionService (pipeline
   owner).
