# Research: why 0.26% of non-blank code lines are covered by no `code_entries` row

**Date:** 2026-09-24
**Question:** On the owner's bank, 0.26% of non-blank lines in code files are covered by no
`code_entries` row, in paths that lost no chunks to dedupe (issue #711). Is this a product defect
in the code-ingestion or watch pipeline, or is it expected given how and when those files were
last touched?

```chart:range
title: coverage-gap lines by cause, of 3,783 total (live-bank backup copy, corrected line split)
measurement-script artifact (H3, before fix): 0..4..4
watcher-lag / not-yet-re-ingested (H1, real): 3783..3783..3783
```

## Findings

### F1: Every remaining non-dedupe coverage gap is a file whose on-disk mtime is newer than its newest ingested row; zero exceptions once the measurement counts lines the way the chunker does [MEASURED]

Re-running the dedupe-independent gap scan from `docs/work/2026-09-24-followup-batch-measurements.md`
against the same read-only backup copy of the owner's live bank, counting non-blank lines by
splitting on `'\n'` only (the convention `CodeChunker.SplitLines`, `wc -l`, and every editor use)
instead of Python's `str.splitlines()`:

- 208 paths, 3,783 non-blank lines have `os.path.getmtime(path) > MAX(code_entries.updated_at)`
  for that path.
- 0 paths, 0 lines do not.

An earlier pass, the same day and a few hours earlier, had found 208 stale paths plus 2 non-stale
paths (`legacy_yaml_subset.py`, both copies, 2 phantom lines each, 4 lines total) that this pass no
longer sees; see F3. The stale-path count (208) is identical between both passes. The stale-line
count drifted from 3,774 to 3,783 because these are live, actively-edited files on a shared,
multi-agent dev machine, and roughly two hours passed between the two measurements.

Six of the largest-lag examples cluster around the watch's own creation timestamp
(`watches.created_at = 2026-08-22T14:20:34Z`), with the row untouched since and the file mtime
about 32 days later, for instance `ai-raccoon/scripts/src/bundle.py` (newest row
`2026-08-22T14:25:45Z`, mtime `2026-09-23T21:07:04Z`, lag 774.7h). For these specific paths, the
containing directory's own mtime exactly equals the file's mtime (`Sep 23 23:07` for both
`scripts/src/` and `scripts/src/bundle.py`). That equality is the OS's own signal that the
directory *entry* was added, removed, or renamed at that moment, not that the file's content was
edited in place. It fits a file having been replaced wholesale (a `git checkout` or branch switch
touching many files at once) rather than edited by a running editor.

**Evidence:**
```
python3 scratchpad/pd/gap_mtime_check.py scratchpad/pd/live-copy.db
  stale (mtime > newest row updated_at): paths=208 lines=3783
  NOT stale (mtime <= newest row updated_at): paths=0 lines=0

python3 scratchpad/pd/gap_examples.py scratchpad/pd/live-copy.db
  jsaa       gap_lines=24 newest_row_updated_at=2026-08-22T16:35:30Z file_mtime=2026-09-23T23:25:02Z lag_h=774.8  .../use-auth.ts
  ai-raccoon gap_lines=6  newest_row_updated_at=2026-08-22T14:25:45Z file_mtime=2026-09-23T21:07:04Z lag_h=774.7  .../scripts/src/bundle.py

stat -f "mtime=%Sm ctime=%Sc" -t "%Y-%m-%dT%H:%M:%S" scripts/src/bundle.py
  mtime=2026-09-23T23:07:04 ctime=2026-09-23T23:07:04
ls -la scripts/src/ | head -3
  drwxr-xr-x@ 17 arasz staff 544 Sep 23 23:07 .        (directory's own mtime matches the file's)
```
Scripts live under `scratchpad/pd/` in this task's scratchpad directory. `live-copy.db` is the
read-only backup copy of the owner's bank named in the task brief, not the live server.

### F2: The code-ingestion pipeline has no coverage-losing defect: an unchanged-hash chunk's position is refreshed correctly, and every non-blank line of a real edit is covered end to end through the actual watch pipeline, including an atomic-rename edit [MEASURED]

Three separate proofs, all run against production code with no test-only shortcuts:

1. **Existing unit-level coverage.** `CodeIngestorTests.Reingest_FileGainsLeadingLines_RefreshesPosition`
   already proves `CodeIngestor.IngestFileAsync`'s dedup-rediscovery path
   (`MemorySql.UpdateCodeChunkPosition`, `src/AiRaccoon.Infrastructure/Ingestion/CodeIngestor.cs:67-79`)
   refreshes an unchanged chunk's `line_start`/`line_end` when a file gains leading lines above it.
2. **New end-to-end coverage.** Added
   `WatchIntegrationTests.EditedCodeFile_InsertModifyAppend_CoversEveryNonBlankLineAfterEachEdit`
   and `...ViaAtomicRename_CoversEveryNonBlankLine` (`tests/AiRaccoon.Tests/Integration/WatchIntegrationTests.cs`).
   They drive a real `FileSystemWatcher` through the production `WatchPipeline`, `WatchDigestExecutor`,
   `CodeIngestor`, and prune chain over a real temp directory, then assert every non-blank line of
   the file's current on-disk content is covered by some `code_entries` row. That assertion runs
   after inserting lines at the top, changing a line in the middle, appending a block at the end,
   and after a write-temp-then-`File.Move(..., overwrite: true)` atomic replace, the idiom editors
   and git use.
3. **Mutation check.** Commented out the `UpdateCodeChunkPosition` call, kept only `continue`,
   rebuilt, and re-ran both the existing and the new test: both failed with the exact defect class
   #711 describes (`gaps: [4, 8, 12, 13, 14]` for the insert-at-top case; `line_start` stuck at `1`
   instead of `3` for the existing test). Reverted the change; both green again.

**Evidence:**
```
dotnet exec tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests.dll \
  --filter-class AiRaccoon.Tests.Integration.Storage.CodeIngestorTests
  total: 14  failed: 0  succeeded: 14

dotnet exec tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests.dll \
  --filter-class AiRaccoon.Tests.Integration.WatchIntegrationTests --filter-method "*EditedCodeFile*"
  total: 2  failed: 0  succeeded: 2

# mutation: continue-only branch in CodeIngestor.cs (position refresh removed)
  failed CodeIngestorTests.Reingest_FileGainsLeadingLines_RefreshesPosition
    (int)after.line_start should be 3 but was 1
  failed WatchIntegrationTests.EditedCodeFile_InsertModifyAppend_CoversEveryNonBlankLineAfterEachEdit
    gaps should be empty but had 5 items: [4, 8, 12, 13, 14]
# revert -> both green again (git diff on CodeIngestor.cs is empty after revert)
```

### F3: The one concrete non-stale gap found earlier, `legacy_yaml_subset.py`, 2 lines each of 2 copies, was a measurement-script artifact, not a chunker defect: the file embeds literal U+2028/U+2029 characters as data, and Python's `str.splitlines()`, not `CodeChunker`, treats those as line breaks [MEASURED]

`legacy_yaml_subset.py` has 339 real `\n`-terminated lines (`wc -l` reports 339, and
`code_entries`'s last chunk covers up to `line_end=339` for both copies of the file). The file's
own source defines `_LINE_SEP = "\u2028"` and `_PARA_SEP = "\u2029"`, meaning it contains the
actual Unicode LINE SEPARATOR and PARAGRAPH SEPARATOR characters as string-literal data (fittingly,
since legacy YAML 1.1 treats those two characters as line breaks, and this module emulates that).
Python's `str.splitlines()`, used by the original `prevalence.py` audit script to count non-blank
lines, also treats those two characters as line breaks, so it reports 341 "lines" for a file with
339 real lines. The two phantom lines at the tail (340, 341) sit outside any covered range because
they are not distinct source lines at all. `CodeChunker.SplitLines`
(`src/AiRaccoon.Infrastructure/Chunking/CodeChunker.cs:239-260`) splits on `'\n'` only and is
unaffected. This is a defect in the ad hoc counting script, not in the shipped chunker.

The brief's literal H3 wording, that the chunker drops a final line without a trailing newline, is
also directly false: `legacy_yaml_subset.py` ends with `\n`, and a new unit test proves an
unterminated final line is still covered.

**Evidence:**
```
python3 scratchpad/pd/find_linebreaks.py \
  ~/RiderProjects/ai-badger/skills/mcp-index/scripts/legacy_yaml_subset.py
  '\u2028' found at 6435 context: '...return text\n\n\n_LINE_SEP = "\u2028"\n_PARA_S'
  '\u2029' found at 6451 context: '...text\n\n\n_LINE_SEP = "\u2028"\n_PARA_SEP = "\u2029"\n_DOUBLE'
  count \n: 339
  splitlines len: 341

sqlite3 (live-copy.db, read-only): last code_entries row for both copies of the file is
  chunk_index=12, total_chunks=13, line_start=321, line_end=339

wc -l < .../legacy_yaml_subset.py   -> 339
tail -c 5 .../legacy_yaml_subset.py -> "rsed\n"   (the file ends with a trailing newline)

dotnet exec tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests.dll \
  --filter-class AiRaccoon.Tests.Unit.Chunking.CodeChunkerTests
  total: 12  failed: 0  succeeded: 12
  (includes the new Chunk_FinalLineHasNoTrailingNewline_StillCoversIt)
```

### F4: The retry/backoff design that can leave a whole watch unchecked is deliberate and documented; its stated recovery is a restart's catch-up scan, not a periodic re-scan while the watch stays active [READ]

`WatchRetryPolicy` keys its consecutive-failure counter per watch root
(`src/AiRaccoon.Infrastructure/Watch/WatchRetryPolicy.cs:10-14`, doc comment: "per-watch
consecutive-failure counter"), not per file. After 5 consecutive digest failures for that root, the
watch stops being checked at all, by design (feature rule 14), until the process restarts. At that
point `WatchHostedService.ReconcilePassAsync` runs `WatchCatchUp.EnqueueChangedSince` for every
watch transitioning to active (`src/AiRaccoon.Infrastructure/Watch/WatchHostedService.cs:170-176`).
Nothing re-arms a stopped watch, or re-scans a continuously active one, in between. That is the
explicit design ("a missed event goes straight to catch-up processing on restart"), and it is unit
tested as such (`WatchRetryPolicyTests.RecordFailure_FifthFailure_StopsCheckingForever`).

**Evidence:** `docs/plans/file-watcher-implementation.md:150,262`; `docs/features/file-watcher/file-watcher.feature:304-331`;
`src/AiRaccoon.Infrastructure/Watch/WatchRetryPolicy.cs:10-14,34-40`;
`src/AiRaccoon.Infrastructure/Watch/WatchHostedService.cs:161-177`.

### F5: `FileSystemWatcher` is a thin wrapper over the OS's own change-notification mechanism, which is known to under-report events during a burst of near-simultaneous filesystem changes, such as a `git checkout` or branch switch touching many files at once; nothing in this pipeline detects an event it never received [INFERRED]

F1's largest-lag examples (774+ hours, directory-entry-replace signature) are best explained by a
burst filesystem operation (a branch switch, `git stash pop`, or similar) that replaced several
files near-simultaneously on this actively shared, many-worktree dev machine, and during which the
OS delivered no net create/change event for that specific path to `FileSystemWatcher`. It does not
look like an AiRaccoon-side bug: the store's rows for these paths still hold their August 22
content (not deleted), which rules out "the delete event fired but the create event didn't"
(that would leave zero rows, not stale ones), and instead means neither side of the replace ever
reached the digest pipeline. This reasons from F1's directory-mtime-matches-file-mtime observation
and from `WatchEventSource`/`WatchDigestExecutor`'s code (read for F2/F4). No counter-evidence was
sought in the .NET runtime's own `FileSystemWatcher` source, so this stays inferred rather than
read.

## Verdict

No product defect. `code_entries` line coverage is complete for every case this investigation
could drive end to end (ordinary edit, atomic-rename edit, unchanged-hash-chunk reposition), with a
failing-first mutation proving the new regression tests actually catch the class of bug #711
describes. The residual gap, 0.26% of lines on the owner's bank, is entirely watcher lag on files
edited after their last successful digest. That is expected under the existing, documented
per-watch-root retry/backoff design and under FileSystemWatcher's own event-delivery limits during
bursty filesystem churn. Today it is recoverable only by restarting the server, which re-runs
catch-up, or by touching the file again.

## Still open

- Whether a periodic re-scan of an already-active watch, not just on the active-transition, would
  be worth adding. It would trade some CPU and IO for closing this gap without a restart. That is a
  design decision for the owner or architect, not attempted here, since the current
  recover-on-restart behavior is an explicit, tested design choice (F4), not an oversight.
- The exact filesystem operation that produced the six 774-hour-lag examples in F1 and F5 was not
  reproduced live. Doing so would need a comparable burst of near-simultaneous file replacements
  under a real `FileSystemWatcher`, confirming no event surfaces. F5 stays inference from static
  evidence, not a controlled repro.
- The `prevalence.py` and `gap_mtime_check.py` scripts live only in this task's scratchpad, not in
  the repository. Nothing here proposes changing them, since they are not shipped code.
