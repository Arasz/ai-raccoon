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
tries the id order and the position order, each also with up to four of its last rows moved to the
front. That covers both write layouts (the opening-last one needs one move), and a chunk-boundary
repair that left a short run's pieces in the run's place. For each candidate it joins the rows,
trying each overlap where whole lines open a row and also close the row before (longest first), and
accepts the order only when the joined text hashes to the path's stem. Requiring a line start on the
row-before side is what keeps a lone blank line from matching at every junction, and it loses no real
overlay. `MarkdownChunker` copies whole units forward as overlay, and the only unit that does not start
a line is a later piece of a line `AddUnitOrSplit` cut because it was over budget. That piece can be the
overlay (a short last piece is), but it never shares a row with the piece before it: the two together
are the over-budget text that was cut, and `BuildChunk`'s recount sheds whichever would join them. So
in the row before, the overlay starts that row or follows a line end. `NoteTextOrderTests` checks this
over notes with long terms and long prose at five budget and overlay pairs, 600 notes in all, and
`NoteChunkOrderRepairTests` checks it for a term over the budget written through `memory_write`. The search walks the junctions with an explicit stack, not recursion, so a
long note cannot exhaust the thread's stack.

**The search has a fixed cost ceiling.** A note with more than 1,024 rows is not searched. One `Find`
call may spend 100,000 work units (one per overlay tried at a junction, one per row for each joined
body hashed), split evenly across the candidate orders so a wrong order cannot starve the right one.
Past the ceiling the note is unprovable and left as stored. Without it, 800 identical paragraphs took
17 seconds for one unprovable note; with it the same call stays inside the budget, and the test holds
it under 3 seconds.
`WritePathFor` hashes the body as sent, while the chunker stores it with `\r\n` and lone `\r`
turned into `\n`, so the check also tries the join with every `\n` restored to `\r\n`, then to `\r`.
No match means null; nothing is guessed. `Repositioned` hands a note's own positions, sorted, to
its rows in text order. It lists only rows that move and does nothing when any position is unknown
(`-1`), so it never invents a position and never touches another row of the partition.

**`note-chunk-order-v1`** (`NoteChunkOrderRepairJob` wrapping `NoteChunkOrderRepair`; renamed `note-chunk-order-v2`, see the addendum) runs once per
bank, right after `chunk-backfill-v2` and before `chunk-boundary-repair-v1`. It groups
source-citing note rows by context key and path, proves each group's order and moves its positions
in one transaction per note. It logs one line (event 446) with the notes it reordered and the notes
it left as stored because no order joins back. It creates no embed work. A note already in text
order moves nothing, so the repair is safe to run again.

**Sync keeps order instead of re-deriving it.** The post-merge renumber used to number every
partition by id (`RecomputeChunkColumnsBankWideFromIdOrder`), which undid text order on every sync
and would have forced a bank-wide note repair each time. It is now
`RecomputeChunkColumnsBankWideKeepingOrder`: each partition gets positions 0..n-1 in the order its rows
already hold, rows the merge added (still at the `-1` default) go after them by id, and only rows whose
values change are written. A tombstone delete still closes its gap. The one thing it cannot know is a
merged note's own order, so before the renumber sync lists the source-citing notes holding a `-1` row
and afterwards runs the note repair on those paths only. A sync that brings no notes repairs nothing.

**The chunk-boundary repair orders a note by `NoteTextOrder`.** A note it cannot prove is skipped,
not re-chunked from a guessed order, and counted: `ChunkBoundaryRepairReport.NotesUnproven`, logged
with the job's other counts as event 447. When it renumbers a source file's partition after a re-chunk,
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
  mixed line endings (only one ending used throughout can be restored), an oversized fence the
  chunker re-fenced, a note with two identical chunks deduplicated to one row, rows a past repair
  changed, rows from a chunker older than the one described above whose overlay began mid-line, a re-chunked
  head of more than four pieces, a note over 1,024 rows, or one that runs out of work budget. Event
  446 reports how many.
- **Unchanged.** Plain notes (no source file) carry no positions, so only the boundary repair reads
  their order.
- **Cost.** The once-per-bank job reads every source-citing note row once, and each note's proof is
  capped at 100,000 work units. On sync the extra cost is one scan for notes with a `-1` row, which is
  the same pass over source-carrying rows the renumber already makes, plus the repair of the notes it
  finds, normally none. No index was added: the listing query reads the rows the renumber's window
  reads anyway, and a partial index on `chunk_index < 0` would be churned by every write, which inserts
  at `-1` and is positioned straight after.
- **Tests.** `SourceCitingNoteChunkOrderTests` (write order and the `memory_search` chunk index),
  `NoteTextOrderTests` (including the row cap at its boundary and an 800-row repetitive note that must
  stop inside the budget), `NoteChunkOrderRepairTests` (seeded opening-last note, one written with
  `\r\n`, listed paths only, a note already in order, an unprovable note, the job),
  `NoteChunkOrderRepairJobOrderTests` (registered before the boundary repair),
  `SyncServiceTests.MemorySync_Merge_LeavesASourceCitingNoteInTextOrder` and
  `MemorySync_Merge_KeepsPositionsThatAreNotInIdOrder`, and
  `ChunkBoundaryRepairTests` (text order after a renumber, a note stored in text order, an
  unprovable note left alone). Each failed before its change.

## Addendum — unknown positions are placed before reordering, job renamed to v2 (#784)

The bank-wide repair could still leave a note out of order. A note whose rows were never
positioned (a merge in progress, or an old-writer note the first pass of this ADR never reached)
has every row at the `-1` sentinel. `NoteTextOrder.Find` can still prove such a note's text order
from an id-order rotation, but `Repositioned` refuses to act on it — it never invents a position —
so the note moved nothing and was neither reordered nor counted unprovable. `chunk-boundary-repair-v1`
ran right after in the same pass and renumbered every remaining `-1` in id order, which is exactly
the opening-last layout this ADR exists to fix: the note's opening chunk, holding the highest id,
landed last again. Reproduced on a seeded opening-last note with every row unpositioned: 354 notes
on the owner's bank were stamped `note-chunk-order-v1` and left this way.

`NoteChunkOrderRepair.RunAsync` (the bank-wide overload) now checks `UnpositionedNotePathsAsync`
first and, when it finds any, runs `RecomputeChunkColumnsBankWideKeepingOrder` before reordering —
the same fill `SyncService` already runs ahead of the path-scoped overload. Every row then holds a
real position, and the proof-then-reposition step it already ran can act on it. The path-scoped
overload is unchanged: sync always fills first, so a row it sees is never at `-1`.

`NoteChunkOrderRepairJob.JobName` is renamed `note-chunk-order-v2`. The runner's due-check reads
`maintenance_jobs` by name (ADR-0070), so a bank already stamped `note-chunk-order-v1` finds no row
under the new name and runs the fixed repair once more on its next maintenance pass — no manual
step. The repair stays safe to re-run: a note already in text order moves nothing.

**Tests.** `NoteChunkOrderRepairTests` (every row unpositioned; the pass-level sequence — this
repair then `ChunkBoundaryRepairJob`'s own recompute — leaves the opening first) and
`NoteChunkOrderRepairJobLedgerTests` (a bank stamped `note-chunk-order-v1` runs the renamed job
again). Each failed before its change.
