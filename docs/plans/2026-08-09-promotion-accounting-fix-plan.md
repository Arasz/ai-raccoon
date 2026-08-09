# Promotion accounting fix — implementation plan (2026-08-09, rev 2 after plan review)

Task: `promotion-accounting-fix` — fixes 1+2+3A of the promotion count-math finding.
Branch: `task/promotion-accounting-fix`. Version: **1.6.3** (the watch-fix lane owns 1.6.2).
Plan review: APPROVE-WITH-CHANGES (deleg_eba4704a) — rev 2 folds in all 6 required edits.

## Problem (measured 2026-08-09, live bank)

`memory_share_extract(mode=promote)` reported 99 `promotedHashes` but only 64 new shared rows
landed (108-candidate band; 9 correctly `skippedDuplicates`). The shared tier itself is
CORRECT — 65 rows, zero value-dup groups, every promoted hash traceable to a shared row. The
defect is accounting + silently dropped chunk claims:

1. **Multi-chunk files coalesce to ONE shared row, but every chunk is reported promoted.**
   The queue holds one row per chunk (jsaa ADRs = 3-5 chunks). `AddContentAsync`
   (SqliteMemoryStore.cs:639-645) pre-checks by path-in-bucket (project-scoped:
   `SelectEntryByPathInBucket` filters scope+project_id+context_label+workspace,
   MemorySql.cs:505-513): the first chunk of a file creates `shared/<filepath>`; every later
   chunk of the same file finds the existing row and returns it **silently**. `PromoteAsync`
   (PromotionQueueService.cs:84-94) cannot distinguish "created" from "existing" and counts
   every non-skipped claim as promoted. Measured: jsaa 70 band rows → 37 shared rows (47%
   absorption); adr-0029's 4 promoted chunks → 1 shared row. The absorbed chunks are claimed
   off the queue and dropped from the propose tier (project-scope rows remain; re-propose can
   recover them).
2. **Stale in-batch snapshot.** `PromoteAsync` takes `GetSharedIndexAsync()` ONCE (line 63)
   and never refreshes it, so within-batch same-file collisions are classified only at insert
   time (mechanism 1), and cross-call races are counted as promoted by both callers. The DB is
   safe for same-(path,hash) races — bare `ON CONFLICT DO NOTHING` on
   `uq_entries_shared_bucket (path, hash) WHERE scope='shared'` (MemorySchema.cs:213-216) —
   only the counters lie. NOTE (review): same-path DIFFERENT-content cross-project writes do
   NOT conflict (the unique key is path+hash; the path pre-check is per-project), so such a
   race genuinely creates two rows — "one row per file" is INTENT, not schema-enforced.
3. **Semantics are undocumented.** Shared tier = first chunk per source file in queue order.
   Nothing states this; shared rows' chunk metadata (`total_chunks=1`) is context-scoped and
   CORRECT for the shared group (RecomputeChunkColumnsAsync partitions by target context,
   MemorySql.cs:57-68 — verified in review), but reads as a 4-chunk ADR being a 1-chunk file.

## Design

### Fix 1 — small result record (f: confirmed)

New record in `AiRaccoon.Core/Memory/`:

```csharp
public sealed record AddContentResult(MemoryEntry Entry, bool Created);
```

- `IMemoryStore.AddContentAsync` returns `Task<AddContentResult>` (was `Task<MemoryEntry>`).
- `SqliteMemoryStore.AddContentAsync` — **Created from the actual insert outcome (review
  edit 1):** `var affected = await connection.ExecuteAsync(InsertEntry, …); Created =
  affected == 1;` (Dapper returns SQLite's change count; the ON CONFLICT DO NOTHING loser gets
  0). The pre-check branch returns `Created = false` unconditionally. The post-insert
  `InvalidOperationException` path is unchanged (re-read must find a row — the winner's).
  This is the ONLY formula that makes "promotedHashes contains only actually-created rows"
  hold for concurrent racers.
- `ShareAsync` returns `Task<AddContentResult>` (propagate; `ShareTools.cs:35-36` reads
  `entry.Context` → `.Entry.Context`).
- `WorkspaceService.ConsolidateAsync` (WorkspaceService.cs:61) ignores the result — compiles
  unchanged; keep its `promoted++` per kept hash (consolidate semantics are per-hash,
  unchanged behavior; add a one-line comment).
- **Fake/call-site scale (review correction):** ~17 `IMemoryStore` fakes implement
  AddContentAsync/ShareAsync, plus ~40 direct call sites assert on the returned entry and
  need mechanical `.Entry.` prefixes (SqliteMemoryStoreTests.cs:383-388/411-414/825,
  MemoryStorePortTests.cs:17-19, MemoryScopeSiblingTtlTests.cs:76-80,
  SqliteMemoryStoreIntegrationTests.cs:76-78, NativeMemorySteps, ManagedHarness, …). All
  mechanical, but budget for them; the fakes return
  `Task.FromResult(new AddContentResult(entry, true))` unless noted otherwise.

### Fix 2 — in-batch snapshot refresh + honest classification

`PromotionQueueService.PromoteAsync`:

- Maintain mutable local copies of the shared index: `HashSet<string>` of whitespace-stripped
  values and `HashSet<string>` of full `"shared/{row.Path}"` path strings — EXACTLY the
  formats `IsDuplicate` (PromotionQueueService.cs:205-207) and `SharedIndex.Paths` use
  (review edit 3).
- After each successful share where `Created == true`, add the new shared path string and
  whitespace-stripped value to the local sets.
- Classification per claimed row (before `ShareAsync`):
  - value already in shared (whitespace-stripped) → `skipped` (content duplicate). **This
    changes tier content, not just counters (review edit 4):** a same-batch different-path
    identical-value row (e.g. identical CLAUDE.md/HERMES.md sections) is currently inserted as
    a second shared row (stale snapshot misses it); with the refresh it becomes `skipped` —
    one copy in shared. Desired; must be tested.
  - `shared/{row.Path}` already in shared paths → `absorbed` (chunk of an already-represented
    file; 3A semantics)
  - else → `ShareAsync`; `Created ? promoted++ : absorbed++` (the cross-call race loser lands
    here, counted exactly via affected==1)
  - value-twin AND path-collision → `skipped` (value checked first — review confirmed this
    order is right).
- `PromoteOutcome` gains `int Absorbed` (positional, default `0` — all existing construction
  sites use exactly 3 positional args, verified: TestData.cs:83, ExtractionHostedServiceTests
  :117/331/349, MemoryToolsTests.cs:106/121/203, PromotionQueueService.cs:118; no
  deconstruction sites; the default hides no existing assertion).
- `ShareExtractResult` gains `int Absorbed { get; init; }` (default 0); `ShareTools.cs:98-100`
  maps it through. Response: `promotedHashes` (only actually-created rows), `absorbed`,
  `skippedDuplicates`, `failures`. Invariant: claimed = promoted + absorbed + skipped +
  failures. Note: `absorbed: 0` also serializes in propose-mode responses (shared record,
  ShareTools.cs:115) — cosmetic; the doc table says so (review edit 6).
- `ExtractionHostedService.cs:146-148` candidate-count log (EventId 502) gains
  `+ outcome.Absorbed`; note the log line's meaning shifts from "promoted+skipped" to
  "promoted+absorbed+skipped" (review trap).

### Fix 3A — semantics made explicit (no behavior change to the tier)

- Shared tier holds **the first chunk promoted per source file, in queue order
  (`score DESC, created ASC`, PromotionQueueSql.cs:31)** — chunks already promoted or evicted
  earlier don't participate (review edit 5 wording). This is INTENT, not DB-enforced: the
  path pre-check is per-connection sequential; concurrent same-path different-content
  promotes can still create two rows. The doc note must say "intends one row per file", not
  that the schema guarantees it (review trap).
- Document in: `docs/reference/agent-memory-server.md` (memory_share_extract /
  memory_promotion_list; current shape text at :80-90 pins "skippedDuplicates … by value or
  path" and MUST change), the memory-usage-guide prompt (`MemoryPrompts.cs` — keep pinned
  fragments: MemoryPromptsTests pins "memory_share", "workspace", "project_id", "scope=all",
  "Search memory first"…), and a one-line note in `docs/adr/0007-propose-tier.md`.
- The `absorbed` response field makes coalescing observable. Shared rows' chunk metadata left
  as-is (context-scoped by design; verified honest in review).

## Files owned

- `src/AiRaccoon.Core/Memory/IMemoryStore.cs`, new `AddContentResult.cs`, `PromotionQueue.cs`,
  `SharedExtraction.cs`
- `src/AiRaccoon.Infrastructure/Promotion/PromotionQueueService.cs`
- `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs` (AddContentAsync, ShareAsync)
- `src/AiRaccoon.Infrastructure/Workspace/WorkspaceService.cs` (compile-only)
- `src/AiRaccoon.Infrastructure/Extraction/ExtractionHostedService.cs` (counter arithmetic)
- `src/AiRaccoon/Tools/ShareTools.cs`
- `src/AiRaccoon/Setup/MemoryPrompts.cs` (guide wording, pinned fragments untouched)
- Tests: new `PromotionQueueServicePromoteAccountingTests`; `RecordingShareStore`
  (PromotionQueueServicePromoteRaceTests.cs:162-252) needs NON-mechanical extension —
  Created semantics, path-aware ShareAsync, seedable shared index (review trap; the
  alternative is hosting tests 1-2 on the real store in PromotionQueueServiceTests:179-232);
  `SqliteMemoryStoreTests` (concurrency test :814-831 gains exactly-one-Created),
  all ~17 fakes + ~40 call sites
- `docs/reference/agent-memory-server.md`, `docs/adr/0007-propose-tier.md`, README What's-new
  for 1.6.3, version marker (1.6.3): csproj PackageVersion/InformationalVersion/AssemblyVersion,
  `src/AiRaccoon/.mcp/server.json` (both version keys), `tests/.../VersionContractTests.cs`
  `ExpectedVersion` pin — the 1.6.1 bump missed the pin; your bump MUST include it.

**Must not touch:** `.ai-badger/state.json`, `Directory.Packages.props`, the watch
digest/delete path (the watch-fix lane owns it), anything outside this worktree.

## TDD test list (RED first, paste output)

1. `Promote_TwoChunksOfOneFile_OneSharedRowOneAbsorbed` — queue two chunks of one file
   (distinct hashes, same source path); promote limit=2 → 1 shared row, `PromotedHashes`
   count 1, `Absorbed` 1, `SkippedDuplicates` 0, queue drained. RED on current code: reports
   2 promoted, no absorbed.
2. `Promote_BatchValueTwin_SkippedNotSecondRow` (review edit 4) — two rows in ONE call,
   different paths, identical value: first shared, second `skipped`; exactly one shared row.
   RED on current code: two shared rows (stale-snapshot double insert).
3. `Promote_ConcurrentSameContent_ExactlyOneCreated` (review edit 2) — same path+hash
   promoted from two calls (or two projects): exactly one `PromotedHashes` entry, exactly
   one `Absorbed`, exactly one shared row — NOT merely "counts sum" (the sum passes on the
   broken implementation).
4. `AddContentAsync_ConcurrentSameBucket_ExactlyOneCreated` — extend the existing test
   (SqliteMemoryStoreTests.cs:814-831): among N concurrent same-(path,hash) inserts, exactly
   one `Created == true` (the DO NOTHING losers report false via affected==1).
5. `AddContentAsync_SecondSamePath_CreatedFalse` — same path re-add returns `Created=false`,
   same entry hash.
6. Workspace consolidate regression: `Consolidate_PromotesKeptHashes` (WorkspaceServiceTests
   :103) still passes (return-type ripple only).

## Acceptance criteria (gates)

1. Tests 1-5 written first, seen RED (paste), then GREEN with the fix (paste).
2. Full suite: `dotnet build` + `dotnet test` (redirected to a file), run ALONE in the
   worktree, 0 failures. Provision the embedding model first
   (`cp src/AiRaccoon/Models/{model_qint8_arm64.onnx,vocab.txt}` from the main checkout).
   Known pre-existing failures on base 4f5a4b7a (do NOT chase; report if present):
   OtlpTraceExportE2ETests.OtelTracesSamplerAlwaysOff_ProducesNoSpans (flaky, fails
   in-suite/passes alone), VersionContractTests ×2 (the 1.6.1 pin miss — your bump fixes
   them; verify they go GREEN in your worktree).
3. Live manual test (hard requirement): scratch bank (`--transport http --port <free>
   --data-root <fresh>`, `model set local` on the scratch root), ingest a 2-chunk file,
   propose, promote → response shows promoted=1, absorbed=1; SQL on the scratch bank shows
   exactly 1 shared row for that file; a second promote call for the same file's remaining
   chunk → absorbed. Paste the transcript.
4. `memory_share_extract` response shape: `promotedHashes` contains only created rows;
   `absorbed` present (0 in propose mode); E2E surface test (McpServerToolSurfaceE2ETests)
   green.
5. Version 1.6.3 per repo bump convention — INCLUDING the VersionContractTests pin.
6. Docs updated (agent-memory-server.md incl. the :80-90 shape text, guide prompt, ADR-0007
   note, README What's-new).

## QA plan

- Automated: tests above + full suite (alone).
- Manual: scratch-bank transcript (criterion 3). No real-bank promotion without user
  go-ahead.
- Join check: after `fix-promotion-algorithm` merges, rebase this branch and re-run the
  suite — both branches touch the queue service.

## Coordination

- `fix-watch-source-file-delete` lane (in flight): owns the watch delete path — no file
  overlap; its PR (#254) carries 1.6.2, this lane carries 1.6.3. Check the version marker at
  start; if 1.6.2 merged first, keep 1.6.3.
- `fix-promotion-algorithm` session (in flight, other worktree): owns queue machinery; expect
  a rebase + full-suite re-run at the join.
