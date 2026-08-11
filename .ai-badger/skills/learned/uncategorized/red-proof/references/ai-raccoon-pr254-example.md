# Worked example — AiRaccoon PR #254 promotion-candidate test (2026-08-09)

## Situation

Reviewing PR #254 ("fix watch source file delete"). The fix (commit f1ce8a46) changed two SQL statements in `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs` to match on
`path` instead of `source_file`. The PR's two tests covered the delete side; the second changed statement (`CaptureQueueRowsForSourcePath`, the promotion-queue round trip inside
`ReplaceFileAsync`) had no direct assertion — a regression there would silently drop a promotion candidate backed by a manual row. The review flagged it as a Low follow-up; the user asked to fix the test gap.

## The test

`ReplaceFile_KeepsPromotionCandidateBackedOnlyByManualRow` in
`tests/AiRaccoon.Tests/Integration/SqliteMemoryStoreIntegrationTests.cs`:

1. Write a file, `ScopeDataRootAsync()`, `_store.IngestFileAsync(project, file, null, ct)`
2. Manual write: `_store.WriteAsync(new MemoryWriteRequest("acme", "<unique>", SourceFile: file), ct)`
   — returns the entry whose `Hash` is the manual row's hash
3. Queue the candidate: `_queue.UpsertAsync("acme", [new QueueCandidate(manual.Hash, $"{manual.Hash}.md", manual.Value, file, 1.0, [])], ct)`
   where `_queue = new SqlitePromotionQueueStore(_factory, new FakeTimeProvider(FixedNow))`
   (new fixture field — hence the whole-class re-run, 22 tests)
4. Rewrite the file with different content, then `_store.ReplaceFileAsync("acme", file, "revised-hash", ct)`
5. Assert `(await _queue.ListAsync("acme", ct)).ShouldContain(r => r.Hash == manual.Hash, "...")`

Under the OLD SQL the candidate died: captured into `queue_restore` (source_file matched), its backing manual row deleted, restore check failed → dropped.

## RED-proof sequence (fix already committed)

```sh
# worktree at .ai-badger/worktrees/fix-watch-source-file-delete, on PR head
git checkout ff289716 -- src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs  # pre-fix SQL
dotnet test tests/AiRaccoon.Tests --filter "FullyQualifiedName~ReplaceFile_KeepsPromotionCandidateBackedOnlyByManualRow"
#   Failed! - Failed: 1 ... "a candidate backed only by a manual row citing the path must survive the digest replace"
git checkout HEAD -- src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs       # restore
git status --short   # only the test file modified
# GREEN:
dotnet test ... --filter "~ReplaceFile_...|~DeleteSourcePath_...|~ChangedFile_..."  # 3/3
# fixture changed → whole class:
dotnet test ... --filter "FullyQualifiedName~SqliteMemoryStoreIntegrationTests"      # 22/22
```

Committed as 9466c3e "test: promotion candidate backed by manual row survives digest replace" with the RED/GREEN evidence in the commit message.

## Notes

- The RED run failed on the intended assertion (the ShouldContain message), not setup — that is what makes the proof honest.
- Commit history already showed RED (ff289716) → fix (f1ce8a46) → bump (0898a013); the witnessed fail extended the same evidence to the follow-up test.
- Entries has NO unique constraint on (project_id, hash) — that UNIQUE constraint belongs to promotion_queue (F3 audit). Same-hash mirror/manual overlap is possible; the analysis holds either way.
