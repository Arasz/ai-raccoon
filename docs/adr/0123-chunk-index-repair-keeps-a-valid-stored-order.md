# 0123 — Chunk-index repair keeps a valid stored order an older chunking left

Date: 2026-09-28

Status: Accepted

## Context

`ChunkIndexRepair` ([ADR-0120](0120-chunk-boundaries-fall-on-whitespace.md)) re-derives a file's
(context, source file) partition from the file on disk: rows the current chunker reproduces take
their document positions, and every row it does not reproduce goes to `-1` — a guess is never made.
But rows can be unreproducible while their text is still in the file: a bank that ingested under an
older chunker holds rows cut at boundaries the current chunker never writes. Nothing about those
rows is stale — only the boundaries moved — yet the repair sends them to `-1`, and `memory_search`
then reports `chunkIndex` -1 and no adjacency for text that is right there in the file.

Measured on a snapshot of the owner's bank (4,027 partitions, 27,178 file rows): the repair sent
23,459 rows to `-1`, and 22,810 of them had their value verbatim in the file at repair time. Only
649 rows' text had actually left the file.

The stored positions of such a partition are not wrong. An older chunker cut the file and numbered
its pieces 0..n-1 in document order; the file has not renumbered anything since. What is unknown is
only where the current chunker's boundaries would fall.

## Decision

**A row the current chunker does not reproduce keeps its stored `chunk_index` when its text is still
in the file.** Only a row whose text has left the file goes to `-1` — that row is the one the repair
can no longer place, and `-1` stays "never a guess". A kept row keeps its exact stored position; a
gone row's slot becomes a hole, and a row already at `-1` keeps it — the keep rule never
resurrects a row, not even where the current chunker reproduces its hash again (only a repair pass
that ranks by the file gives one back a position). "In the file" is checked
against the file as the chunkers read it: the raw bytes, and the same bytes with `\r\n` and lone `\r`
normalized to `\n` — what `MarkdownChunker.NormalizeLineEndings` does before slicing — so a stored
row's bare-`\n` text on a CRLF file is present, not gone (line endings are not text leaving a file).

The keep applies to a whole partition and only when the stored order is one this rule may certify:

- the file is readable and has a handler, and the scan leaves at least one row unplaced (otherwise
  there is nothing to keep, and the repair ranks by the file as before);
- the stored positions run 0..n-1, where a row already unknown may stand at `-1` and keeps it. This
  is the numbering this rule writes, so the rule keeps its own output and is a fixed point
  (`ChunkIndexRepairTests.RunAsync_KeptOrderWithAnUnknownRow_IsItsOwnFixedPoint`); a gap or a
  duplicate is not a numbering to trust and declines to the old behaviour;
- the rows the current chunker reproduces are stored in document order, so the stored order agrees
  with the document wherever the two can be compared.

Otherwise the repair behaves exactly as before: reproduced rows take ranks in file order, rows it
cannot reproduce go to `-1`, notes citing the file follow its rows in stored order, and
`total_chunks` is the partition row count. It stays a pure UPDATE and runs only on
`repair chunk-index --apply`. `ChunkIndexRepair.UnplaceableAsync` holds the decision and
`ChunkPositionScanner.MovesKeepingStoredOrder` the writes.

Each certification has a test that goes red when the check is removed
(`RunAsync_OlderChunkingWithAGapInItsPositions_UsesTheFileOrder` for the numbering,
`RunAsync_OlderChunkingWithReproducedRowsOutOfOrder_UsesTheFileOrder` for document order,
`RunAsync_RowsFromAnOlderChunkingWhoseTextLeftTheFile_GoUnknownAlone` for the per-row text check),
verified by disabling each check in turn and watching the named test fail.

## Alternatives rejected

- **Keep or decline all-or-nothing per partition** (the first cut of this branch): keep only when
  *every* unplaced row's text is still in the file. On the same snapshot it leaves 4,714 rows at
  `-1` — one row whose text is gone drags its text-present siblings down with it. 195 partitions
  held 4,658 rows at `-1` for 648 gone rows between them (`results-f1.json`: 222 rows punished for
  3). The per-row rule is what "never a guess" allows: the gone row's `-1` is the guess-free part,
  the sibling's is collateral.
- **Renumber the kept rows contiguously in stored order instead of leaving holes.** It would rewrite
  the positions of rows that did keep their text (a "keep" that moves rows), a row already at `-1`
  would have to take a place among the kept rows — the guess this rule refuses — and it would make
  `SourceAffinityRanker`'s `|chunkIndex difference| == 1` adjacency pair rows across a gone row.
  Holes keep every stored value and keep adjacency honest.
- **Prove the kept rows' order from their offsets in the file** (each kept row's value matched at a
  strictly increasing offset). It would catch a file restructured under its partition — 2,230 rows
  in the snapshot's otherwise-kept partitions fail that check today — but it would equally decline
  partitions the repair has always kept, changing behaviour beyond this decision. Recorded as the
  limitation below instead.

## Consequences

- **Measured** on the owner's-bank snapshot (read-only dry run, 925 MB `memory.db`, 4,027
  partitions): 23,459 rows set to `-1` before, **706** after — 650 rows whose text is gone from the
  file, 55 rows in the 4 partitions whose stored order failed the document-order check, 1 row of an
  unreadable file — and 2,797 rows moved before, 21 after. 26,587 rows across 2,298 partitions keep
  their stored positions.
- **Left exactly as before, and counted.** A row whose text left the file; every row of a partition
  whose file is missing or unreadable, whose stored positions have a gap or a duplicate, or whose
  reproduced rows are stored out of document order. These are the 706's other 56 rows on the
  snapshot.
- **Known limitation (closed in 1.53.9, see the addendum below).** "Reproduced rows in document order" is the only order proof; the unplaced
  kept rows' order is certified by the 0..n-1 numbering alone. A file whose text was rearranged
  without changing any row's bytes can keep a stale order — the offset proof in the alternatives
  would close this for both this rule and the old behaviour, at the cost of declining more
  partitions.
- **Cost.** One `SELECT value FROM entries WHERE id IN (...)` per partition with unplaced rows, on
  the rows the scan already read. No schema change, no index.

## Addendum: a kept row must sit where its position puts it (1.53.9)

A review of the first release of this rule found two ways it kept a position the file no longer
supports. It asked only whether a row's text was *somewhere* in the file. A row whose own section was
deleted, but whose text survives as a copy elsewhere, was kept; so was a row whose text moved to
another part of the file. The limitation above ("the unplaced kept rows' order is certified by the
0..n-1 numbering alone") was the same gap.

**Each kept row is now located in the file.** `StoredOrderChain` (pure, `AiRaccoon.Core.Chunking`)
finds every offset at which each stored row's text occurs in the file, with line endings read as the
chunkers read them, and picks the heaviest chain of rows whose offsets strictly increase in stored
order. A row the current chunker reproduces weighs more than every other row together, so the chain
bends around the rows the file proves by hash. An unreproduced row outside the chain goes to `-1`
alone: its text left the file, moved, or survives only where its stored position cannot be. The
partition-level checks above are unchanged, and a reproduced row is never set to `-1` by this rule.

This is the offset proof the alternatives rejected, applied per row instead of per partition. The
rejection was about declining whole partitions (2,230 kept rows on the snapshot); per row, only the
rows that are actually out of place go.

**One pass over the file.** The text search runs once per partition over the file with a
`SearchValues<string>` multi-string search, not once per row. A per-row `IndexOf` is
O(rows × file size), which the 50 MB, 5,811-row log file on the owner's bank would have made
visible.

**Measured** on the same snapshot, read-only dry run: 742 rows to `-1` (706 under the first
release; the 36 more are rows whose text is out of place or moved), 21 rows moved, and the full
dry run takes 264 s against 246 s without the keep rule. Nearly all of that is re-chunking every
file.

**Where a copy is truly ambiguous.** Text that occurs at more than one offset is kept when some
occurrence fits between its neighbours, since the file cannot say which copy the row was cut from.
On the snapshot 52 of 26,314 located rows have text that occurs more than once, none shorter than
80 characters.

**Sync renumbers after a merge** (changed in 1.53.10, see the addendum below).

**Tests** (each failed before its change, or with its check removed):
`RunAsync_OlderChunkingRowWhoseTextMoved_GoesUnknownAlone` and
`RunAsync_OlderChunkingRowWhoseTextSurvivesOnlyOutOfPlace_GoesUnknown` for the chain (the second
also fails when reproduced rows carry no extra weight), `RunAsync_OlderChunkingWithADuplicatePosition_UsesTheFileOrder`
for the duplicate-position check, and `RunAsync_RowsFromAnOlderChunkingOnALoneCrFile_KeepTheirPositions`
for lone `\r` line endings. `RunAsync_AnUnknownRowTheScanReproduces_DoesNotUnsettleTheKeptOrder`
had seeded a row whose text sat before its stored position, which the chain now correctly sends to
`-1`, so its fixture uses text that follows the stored order.

## Addendum: sync keeps what this repair kept (1.53.10)

Sync's post-merge renumber, `RecomputeChunkColumnsBankWideKeepingOrder`, numbered every partition
0..n-1 in stored order and put rows at `-1` after the file's rows. It read `-1` as "a row the merge
just added", so on a bank that syncs, the first sync after `repair chunk-index --apply` closed the
holes this rule leaves and gave the rows it set to "position unknown" positions they cannot be proven
to hold.

`-1` now means one thing only on a file row: position unknown. Sync tells a row it added apart by its
id instead. It reads the highest id before the merge, and the merge INSERT writes `chunk_index = -1`
explicitly rather than relying on the column default. The renumber then treats a partition like this:

- A file row at `-1` with an id above that watermark is one the merge added. It goes after the
  partition's file rows, in id order.
- Every other file row keeps its position, holes and `-1` included, as long as those positions are
  one this ADR certifies: distinct, and each below the number of file rows. Otherwise, for example
  when a tombstone deleted a row and left a survivor past the end, the positioned rows are
  renumbered 0..k-1 in stored order (the gap still closes, as [ADR-0122](0122-note-chunk-positions-follow-text-order.md)
  describes). The rows at `-1` stay there.
- Notes citing the file follow its rows in stored order, as before, and `total_chunks` is the
  partition row count.

The one-time note-order repair runs the same renumber with no watermark (`long.MaxValue`), because it
adds no rows. Its notes are positioned exactly as before, and it no longer positions file rows at
`-1`. Writers that position file rows authoritatively (ingest, this repair) were never at stake.

**Tests** (each seen red with its change removed): `SyncServiceTests.MemorySync_Merge_KeepsTheHolesAndUnknownFileRowsARepairLeft`
(red when the renumber never keeps a numbering, and when it ignores the id watermark) and
`MemorySync_Merge_PositionsAFileRowItAdds_AfterTheKeptRows` (red without the watermark, and when the
merge INSERT leaves the column default, which the sync tests' own schema sets to 0).
