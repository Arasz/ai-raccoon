# 0122 — A note's chunk positions follow its text order

Date: 2026-09-27

Status: Accepted

## Context

A `memory_write` note that cites a source file (`Path != SourceFile`) carries chunk positions: it
shares that file's position partition, keyed by context and `source_file`, and
`RecomputeChunkColumnsForContext` fills each new row's `-1` sentinel by id order after the insert.
`SqliteMemoryStore.WriteAsync` inserted chunks 1..n-1 first and chunk 0 last, a leftover from
[ADR-0064](0064-memory-write-chunks-like-everything-else.md) with no reason recorded. So the note's
opening got the highest id and the last `chunk_index`. Positions were distinct but not in text order.

Reproduced on a 62-paragraph note citing `minutes.md`: six rows, and the row holding
"Opening marker zq71" sat at `chunk_index` 5. The misorder is visible to callers. `memory_search`
returns each hit's `chunkIndex`, and a search for `zq71` reported 5, not 0. `SourceAffinityRanker`
also treats `|chunkIndex difference| == 1` as adjacency, so it paired the opening with the note's
last chunk instead of its second.

Fixing the insert order does not fix the rows already stored, and it exposed a second problem. The
chunk-boundary repair ([ADR-0120](0120-chunk-boundaries-fall-on-whitespace.md)) found a note's text
order by assuming the opening holds the highest id. That was never true for every note:
`ChunkBackfill` split pre-ADR-0064 single-row notes into pieces inserted in text order, and every
note written from now on is in text order too. On such a note the old rule put the last row first,
and a seam check between the note's end and its start could glue the two together. Ids and
timestamps cannot tell the two layouts apart: both kinds of rows share one `created_at`.

The text itself can. `WritePathFor` names a note's path after the SHA-256 of its whole body, and the
markdown chunker builds every chunk from whole units of the body, repeating only a tail of the
previous chunk as overlay. Joining the rows in the right order, with each overlay repeat dropped,
gives back the body, and that hash check is exact.

## Decision

**`memory_write` inserts a note's chunks in text order.** The id-order position fill then gives the
opening the lowest position of the note's run. The returned entry still addresses chunk 0.

**`NoteTextOrder` (pure, `AiRaccoon.Core.Memory`) proves a note's order from its rows.** `Find`
tries every rotation of the id order and of the position order. That covers both write layouts, and
a chunk-boundary repair that left a run's pieces in the run's place. For each candidate it joins the
rows, trying each overlap where a row starts with the previous row's tail (longest first, capped at
256 joins per order), and accepts the order only when the joined text hashes to the path's stem.
No match means null; nothing is guessed. `Repositioned` hands a note's own positions, sorted, to
its rows in text order. It lists only rows that move and does nothing when any position is unknown
(`-1`), so it never invents a position and never touches another row of the partition.

**`note-chunk-order-v1`** (`NoteChunkOrderRepairJob` wrapping `NoteChunkOrderRepair`) runs once per
bank, right after `chunk-backfill-v2` and before `chunk-boundary-repair-v1`. It groups
source-citing note rows by context key and path, proves each group's order and moves its positions
in one transaction per note. It logs one line (event 446) with the notes it reordered and the notes
it left as stored because no order joins back. It creates no embed work. Because a note already in
text order moves nothing, the same repair is safe to run again, and **the sync post-merge pass runs
it** after `RecomputeChunkColumnsBankWideFromIdOrder`. Without that, the first sync would put every
note merged from a peer back in id order.

**The chunk-boundary repair orders a note by `NoteTextOrder`.** A note it cannot prove is skipped,
not re-chunked from a guessed order. When it renumbers a source file's partition after a re-chunk,
the repaired group's rows then take the group's positions in text order, so the renumber yields text
order whether or not `note-chunk-order-v1` ran first. File rows are ordered by position as before.

## Alternatives rejected

- **Keep the opening-last insert and permute positions after each write.** It fixes new writes but
  keeps two layouts on disk for good, and the repair still cannot tell a backfilled note from a
  written one without the hash check.
- **Tell the layouts apart by `updated_at`, `agent_id` or id gaps.** Backfilled and written rows
  carry the same shape of timestamps, and a guessed order re-chunked by the boundary repair would
  corrupt text.
- **Store an explicit text position on every note row.** It needs a schema change and a writer on
  every insert path, and the path hash already fixes the order.

## Consequences

- **Positive.** New notes are in text order at write time. Existing notes are put in text order on
  the first maintenance pass after upgrade, and a sync no longer undoes it. `memory_search` reports
  the opening chunk as `chunkIndex` 0, and adjacency in `SourceAffinityRanker` means textual
  neighbours.
- **Left as stored, and counted.** A note whose rows do not join back into its body: a body with
  `\r\n` line endings (the chunker normalises them, so the hash differs), an oversized fence the
  chunker re-fenced, a note with two identical chunks deduplicated to one row, or rows a past repair
  changed. Event 446 reports how many.
- **Unchanged.** Plain notes (no source file) carry no positions, so only the boundary repair reads
  their order. A file's own rows across a sync still take id order, the separate question
  `RecomputeChunkColumnsBankWideFromIdOrder` already records.
- **Neutral.** The repair reads every source-citing note row once per bank, and again on each sync.
  A note in either write layout proves on one of the first three candidate orders tried (id order,
  position order, id order with the last row first).
- **Tests.** `SourceCitingNoteChunkOrderTests` (write order and the `memory_search` chunk index),
  `NoteTextOrderTests`, `NoteChunkOrderRepairTests` (seeded opening-last note, a note already in
  order, an unprovable note, the job), `NoteChunkOrderRepairJobOrderTests` (registered before the
  boundary repair), `SyncServiceTests.MemorySync_Merge_LeavesASourceCitingNoteInTextOrder` and
  `ChunkBoundaryRepairTests` (text order after a renumber, a note stored in text order, an
  unprovable note left alone). Each failed before its change.
