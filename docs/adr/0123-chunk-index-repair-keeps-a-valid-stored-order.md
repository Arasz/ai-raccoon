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
`repair chunk-index --apply`. `ChunkIndexRepair.GoneFromTheFileAsync` holds the decision and
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
- **Known limitation.** "Reproduced rows in document order" is the only order proof; the unplaced
  kept rows' order is certified by the 0..n-1 numbering alone. A file whose text was rearranged
  without changing any row's bytes can keep a stale order — the offset proof in the alternatives
  would close this for both this rule and the old behaviour, at the cost of declining more
  partitions.
- **Cost.** One `SELECT value FROM entries WHERE id IN (...)` per partition with unplaced rows, on
  the rows the scan already read. No schema change, no index.
