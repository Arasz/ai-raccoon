# Promotion accounting fix — implementation plan (2026-08-09)

Task: `promotion-accounting-fix` — fixes 1+2+3A of the promotion count-math finding.
Branch: `task/promotion-accounting-fix`. Version: **1.6.3** (the watch-fix lane owns 1.6.2).

## Problem (measured 2026-08-09, live bank)

`memory_share_extract(mode=promote)` reported 99 `promotedHashes` but only 64 new shared rows
landed (108-candidate band; 9 correctly `skippedDuplicates`). The shared tier itself is
CORRECT — 65 rows, zero value-dup groups, every promoted hash traceable to a shared row.
The defect is accounting + silently dropped chunk claims:

1. **Multi-chunk files coalesce to ONE shared row, but every chunk is reported promoted.**
   The queue holds one row per chunk (jsaa ADRs = 3-5 chunks). `AddContentAsync`
   (SqliteMemoryStore.cs:639-645) pre-checks by path-in-bucket: the first chunk of a file
   creates `shared/<filepath>`; every later chunk of the same file finds the existing row and
   returns it **silently** (the idempotent re-share path). `PromoteAsync`
   (PromotionQueueService.cs:84-94) cannot distinguish "created" from "existing" and counts
   every non-skipped claim as promoted. Measured: jsaa 70 band rows → 37 shared rows (47%
   absorption); adr-0029's 4 promoted chunks → 1 shared row (`chunk_index 0, total_chunks 1`).
   The absorbed chunks are claimed off the queue and dropped from the propose tier (the
   project-scope rows remain, so re-propose can recover them).
2. **Stale in-batch snapshot.** `PromoteAsync` takes `GetSharedIndexAsync()` ONCE
   (line 63) and never refreshes it, so within-batch same-file collisions are classified only
   at insert time (mechanism 1), and cross-call races (parallel promote calls on the same
   value/path) are counted as promoted by both callers. The DB is safe — bare
   `ON CONFLICT DO NOTHING` on `uq_entries_shared_bucket (path, hash) WHERE scope='shared'` —
   only the counters lie.
3. **Semantics are undocumented.** Shared tier = best chunk per source file. Nothing states
   this; the shared rows' chunk metadata (`total_chunks=1`) makes a 4-chunk ADR look like a
   1-chunk file.

## Design

### Fix 1 — small result record (f: confirmed)

New record in `AiRaccoon.Core/Memory/` (next to `MemoryEntry`):

```csharp
public sealed record AddContentResult(MemoryEntry Entry, bool Created);
```

- `IMemoryStore.AddContentAsync` returns `Task<AddContentResult>` (was `Task<MemoryEntry>`).
- `SqliteMemoryStore.AddContentAsync`: `Created = existing is null` (the pre-check branch
  returns `false`; the insert branch returns `true`). The post-insert
  `InvalidOperationException` path is unchanged (insert race loser re-reads; a row must exist).
- `ShareAsync` returns `Task<AddContentResult>` (propagate).
- `WorkspaceService.ConsolidateAsync` (WorkspaceService.cs:61) ignores the result — compiles
  unchanged apart from the return type; keep its `promoted++` per kept hash (consolidate
  semantics are per-hash, not per-created-row — unchanged behavior, note in code comment).
- All ~25 test fakes/sites updated mechanically (`Task.FromResult(new AddContentResult(entry,
  true))`); direct-call tests that assert the returned entry read `.Entry`.

### Fix 2 — in-batch snapshot refresh + honest classification

`PromotionQueueService.PromoteAsync`:

- Maintain a mutable local copy of the shared index (`HashSet<string>` values-normalized,
  `HashSet<string>` paths).
- After each successful share where `Created == true`, add the new shared path and
  whitespace-normalized value to the local sets.
- Classification per claimed row (before `ShareAsync`):
  - value already in shared (whitespace-stripped) → `skipped` (content duplicate)
  - `shared/{row.Path}` already in shared paths → `absorbed` (chunk of an already-represented
    file; 3A semantics made explicit)
  - else → `ShareAsync`; `Created ? promoted++ : absorbed++` (the cross-call race loser lands
    here)
- `PromoteOutcome` gains `int Absorbed` (positional, default `0` to keep existing test
  constructions compiling; new tests assert it explicitly).
- `ShareExtractResult` gains `int Absorbed { get; init; }` (default 0); `ShareTools`
  maps it through. Response becomes: `promotedHashes` (only actually-created rows),
  `absorbed` (chunk-coalesced + race losses), `skippedDuplicates` (value dups), `failures`.
  Invariant: claimed = promoted + absorbed + skipped + failures.
- `ExtractionHostedService.cs:146` candidate-count arithmetic gains `+ outcome.Absorbed`.

### Fix 3A — semantics made explicit (no behavior change to the tier)

- Shared tier holds **one row per source file: the highest-scored chunk that was promoted
  first**. Document in: `docs/reference/agent-memory-server.md` (memory_share_extract /
  memory_promotion_list), the memory-usage-guide prompt (`MemoryPrompts.cs` — keep the
  pinned fragments intact), and a one-line note in `docs/adr/0007-propose-tier.md` (or the
  current ADR for the propose tier).
- The `absorbed` response field makes the coalescing observable instead of silent.
- Chunk metadata on shared rows is left as-is (it is context-scoped by design); the doc note
  explains why a shared row reports `total_chunks=1`.

## Files owned

- `src/AiRaccoon.Core/Memory/IMemoryStore.cs`, new `AddContentResult.cs`,
  `PromotionQueue.cs`, `SharedExtraction.cs`
- `src/AiRaccoon.Infrastructure/Promotion/PromotionQueueService.cs`
- `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs` (AddContentAsync, ShareAsync)
- `src/AiRaccoon.Infrastructure/Workspace/WorkspaceService.cs` (compile-only)
- `src/AiRaccoon.Infrastructure/Extraction/ExtractionHostedService.cs` (counter arithmetic)
- `src/AiRaccoon/Tools/ShareTools.cs`
- `src/AiRaccoon/Setup/MemoryPrompts.cs` (guide wording, pinned fragments untouched)
- Tests: new `PromotionQueueServicePromoteAccountingTests` (or extend the existing
  `PromotionQueueServicePromoteRaceTests` sibling), `SqliteMemoryStoreTests`
  (concurrency test gains Created assertions), all fakes
- `docs/reference/agent-memory-server.md`, `docs/adr/0007-propose-tier.md`, README What's-new
  for 1.6.3, version marker (1.6.3)

**Must not touch:** `.ai-badger/state.json`, `Directory.Packages.props`, the watch
digest/delete path (the other lane owns it), anything outside this worktree.

## TDD test list (RED first, paste output)

1. `Promote_TwoChunksOfOneFile_OneSharedRowCreatedOneAbsorbed` — queue two chunks of one file
   (distinct hashes, same source path); promote limit=2 → 1 shared row, `PromotedHashes`
   count 1, `Absorbed` 1, `SkippedDuplicates` 0, queue drained. RED on current code:
   reports 2 promoted, no absorbed.
2. `Promote_BatchWithValueDuplicate_SkippedNotAbsorbed` — two rows, one value-twin of a
   pre-shared row → skipped 1; and same-file chunk → absorbed 1 (classification split).
3. `Promote_RaceLoser_CountedAbsorbed` — parallel same-path promotes (two projects or two
   calls, mirroring `AddContentAsync_ConcurrentSameBucket_SingleRowNoThrow`) → exactly one
   shared row, promoted+absorbed sums to claims, no exceptions.
4. `AddContentAsync_ConcurrentSameBucket_ExactlyOneCreated` — extend the existing test:
   among N concurrent inserts of the same bucket, exactly one `Created == true`.
5. `AddContentAsync_SecondSamePath_CreatedFalse` — same path re-add returns `Created=false`,
   same entry hash.
6. Workspace consolidate regression: `Consolidate_PromotesKeptHashes` still passes
   (return-type ripple only).

## Acceptance criteria (gates)

1. Tests 1-5 written first, seen RED (paste), then GREEN with the fix (paste).
2. Full suite: `dotnet build` + `dotnet test` (redirected to file), run ALONE in the
   worktree, 0 failures. Embedding model provisioned into the worktree first
   (`cp src/AiRaccoon/Models/{model_qint8_arm64.onnx,vocab.txt}` from the main checkout).
3. Live manual test (hard requirement): scratch bank (`--transport http --port <free>
   --data-root <fresh>`, `model set local`), ingest a 2-chunk file, propose, promote →
   response shows promoted=1, absorbed=1; SQL on the scratch bank shows exactly 1 shared row
   for that file; second promote call on the same file's remaining chunk → absorbed, not
   lost. Paste the transcript.
4. `memory_share_extract` response shape: `promotedHashes` contains only created rows;
   `absorbed` present; E2E surface test (`McpServerToolSurfaceE2ETests`) green.
5. Version 1.6.3 per repo bump convention (mirror 1.6.0/1.6.1/1.6.2 bumps).
6. Docs updated (agent-memory-server.md, guide prompt, ADR note, README What's-new).

## QA plan

- Automated: tests above + full suite (alone).
- Manual: scratch-bank transcript (criterion 3), then a live-bank dry round: propose on a
  test project only — no real-bank promotion without user go-ahead.
- Join check: after the `fix-promotion-algorithm` lane merges, rebase this branch and re-run
  the suite — the two branches both touch the queue service.

## Coordination

- `fix-watch-source-file-delete` lane (in flight): owns the watch delete path — no file
  overlap; its PR carries 1.6.2, this lane carries 1.6.3. Whichever merges second re-checks
  the version marker.
- `fix-promotion-algorithm` session (in flight, other worktree): owns queue machinery; expect
  a rebase + full-suite re-run at the join.
