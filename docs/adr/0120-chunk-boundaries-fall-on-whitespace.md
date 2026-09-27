# 0120 — Chunk boundaries fall on whitespace

Date: 2026-09-27

Status: Accepted

## Context

A line longer than the token budget has to be split somewhere. Until now the split point was
whatever the budget allowed: `MarkdownChunker.AddUnitOrSplit` and `SplitForSubFence` binary-searched
for the longest prefix within budget (`LargestPrefixWithinBudget`), and `CodeChunker` did the same
through `TokenBudget.Trim`. None of them looked at the character they cut on.

[ADR-0048](0048-a-chunk-is-a-well-formed-markdown-fragment.md) already recorded the gap. On a 20 KB
single-line document at `maxTokens=100`, 14 of 31 boundaries landed mid-word, and the ADR called
word-boundary awareness "unbuilt, not broken". Issue #695 showed what it costs. A `memory_write`
note of about 1,180 characters (the village fete paragraph repeated ten times, ending
"Invoice reference vk83jq was filed with the parish council.") was cut into
"...Invoice reference vk" and "83jq was filed...". A `memory_search` for `vk83jq` then got no
keyword hit at all, because neither chunk holds the whole token. That is the case the keyword leg
exists for: an identifier, an error code, a path.

Any text that is not a file goes through `FileIngestor.ChunkToBudgetAsync`, which uses the `.md`
handler, so `memory_write` notes hit `MarkdownChunker`. `PlainTextChunker` delegates to the same
class. Short lines never reach the split path; the greedy packer joins whole lines, so only a line
over the budget is affected. Banks written before this change already hold such rows, and nothing
rewrites them: a note is never rewritten, and the watch digest skips a file whose content hash has
not changed.

## Decision

Three parts: a split rule for new chunks, a query rule for the one term no split can keep whole,
and a once-per-bank repair for rows already cut.

### Where a line is split

**`TokenBudget.SplitLength`** (pure, `AiRaccoon.Core.Chunking`) answers how many characters to split
off. It finds the longest prefix within budget by the same binary search as before, then picks the
cut in this order:

1. just after the last whitespace inside that prefix;
2. failing that, the last keyword-term boundary: a position next to a character outside letters,
   digits and `_`, the same set `FtsQueryNormalizer` builds query terms from. A long URL or a
   minified-JSON line with no spaces still keeps every search term whole. `_` counts as part of a
   term because a query for `call_handler_07` becomes one FTS phrase;
3. failing both, a hard cut at the budget. Only a single term longer than the whole budget gets
   here, as does text with no boundary at all (CJK prose, a hex blob).

The whitespace stays at the end of the earlier piece, so pieces still concatenate to the original
line. Leading whitespace is never a cut point. `SplitLength` always returns at least 1, so every
split loop terminates. A piece is whitespace-only only when the remaining text is all whitespace;
`ChunkWithHeadings` drops such chunks, as it always has.

**The budget holds.** The backed-off prefix is re-counted before it is used, because a tokenizer
with whitespace-run tokens need not be monotonic at a space. If it counts over budget, the next
rule is tried, and the hard cut (a prefix the binary search already proved fits) is the floor.
ADR-0036's guarantee is unchanged, and so is ADR-0048's fence balance.

`AddUnitOrSplit`, `SplitForSubFence` (a long line inside a re-fenced code block) and
`CodeChunker.AddLineOrSplit` all call `SplitLength`. The chunker's private binary search is gone.
`TokenBudget.Trim` keeps its behaviour for query trimming and shares the search.

### A term longer than the budget

A term longer than the budget cannot fit in any chunk, so it is stored hard-cut and no row holds it
whole. **`FtsQueryNormalizer` matches a query term longer than 64 characters by its first 64 as an
FTS5 prefix query** (`term[..64]*`). The term starts a piece (the split backs off to the boundary
before it), and that first piece is a full hard cut, hundreds of characters at any real budget
(254 memory, 510 code), so it always contains the prefix. A search for the whole term gets a
keyword hit on the row holding its start. The only cost is that two distinct terms sharing their
first 64 characters now match each other. No baseline query has a term that long.

I rejected indexing the whole term in an extra FTS column. It needs a schema migration, a new
column on every insert path and a fourth bm25 weight, and it buys nothing the prefix does not.

### Repairing existing banks

**`chunk-boundary-repair-v1`** (`ChunkBoundaryRepairJob` wrapping `ChunkBoundaryRepair`; renamed `chunk-boundary-repair-v2`, see the addendum) runs once
per bank from the maintenance job list, right after `chunk-backfill-v2` and before
`PendingEmbedJob`. It groups rows by bucket and path and orders each group in text order: a file by
position, a note with its first chunk last-inserted (highest id), as `memory_write` writes it. A
seam is two adjacent rows where one ends and the next begins with a term character
(`ChunkSeam.CutsATerm`). For a file it also counts the re-fenced form: a term just before a sub-fence
closer and just after the next opener (`ChunkSeam.MayCutAFencedTerm`).

- **A file row whose file is still readable is re-ingested** through `IMemoryStore.ReplaceAsync`,
  the unconditional replace the reingest repair and the watch digest use. The seam test only decides
  *whether*; the file decides *what*, so a false positive costs one correct re-ingest. This is what
  heals watched files whose unchanged content hash would otherwise skip them forever. The code
  corpus gets the same pass: a `code_entries` group with a seam has its file re-ingested, since
  `ReplaceAsync` re-ingests both corpora. The repair runs on its own once rather
  than behind `repair reingest --apply` (ADR-0075) because its candidate set is only files with a
  seam, not every file any past chunker change left unreproducible.
- **A note, or a file row whose file is gone, is re-chunked from its own rows.** Rows across a
  hard cut concatenate back to the original text, because the old chunker's cut piece was a full
  budget piece, larger than the 48-token overlay, so no overlay text was repeated after it. The
  repair checks exactly that before joining a seam. Each joined run is re-chunked with the current
  chunker; if the result is the same rows (a term still longer than the budget), nothing is
  written. Replaced hashes are tombstoned so a sync peer does not resurrect them. When the rows
  carry a source file (a file's own rows, or a note citing one), the whole position partition for
  that file is renumbered: untouched rows keep their order and a run's new pieces take the slot
  the run held. Leaving that to the id-order recompute would push pieces from a middle run after
  later rows and give two rows one position. New rows stay `pending` for `PendingEmbedJob` in the
  same maintenance pass. A note's first-chunk hash can change, so a hash an agent kept from before
  may no longer resolve; per-row rating and access counts on replaced rows are lost, as in every
  re-chunk.
- **Two cases are left as they are, both because repairing them would mean inventing text.** A
  mid-line cut inside a fenced block of a note, or of a file gone from disk: the old sub-fence split
  added a line break before the closer, and without the source text that break cannot be told from
  a real one. And code rows of a code file gone from disk: their line ranges cannot be re-derived
  from the rows, and the watch digest prunes them once it sees the file is gone. Every other seam
  is repaired.

**No overlap is added.** After the split rule, the only term a boundary cuts is one longer than the
whole budget, which no overlap smaller than the budget could hold either; the prefix query covers
it. Overlap would also duplicate text in every multi-chunk note's keyword and vector rows.

## Consequences

- **Positive.** Every term shorter than the budget appears whole in at least one chunk, and a
  longer one is still found by its prefix. Existing banks repair themselves on the first
  maintenance pass after upgrade, watched files included, with no user action.
- **Tests.** `ChunkWordBoundaryTests`, `TokenBudgetTests`, `ChunkSeamTests`, the `CodeChunkerTests`
  identifier case and `FtsQueryNormalizerTests` pin the rules; `ChunkBoundaryRepairTests` pins the
  repair on seeded banks (note, watched file, fenced file, code file, file gone from disk, overlay
  guard, a too-long term left alone). `MemorySearchRankingTests` pins both searches end to end with the
  bundled engine, and asserts as a premise that a budget-only cut really lands inside `vk83jq`.
  Each failed before its change.
- **Neutral: pieces can be shorter.** A split piece may end up to one word short of the budget, and
  a short run of words before a very long term becomes its own small piece.
- **Neutral: one full-table read, once.** The repair reads every row's value once per bank, the
  same cost `chunk-backfill-v2` paid.
- **The ranking test keeps eight repeats.** `Search_AllTermsKeywordMatchThatWinsFusion_StaysFirstAboveBoostedNeighbours`
  asserts that the note's first row is the keyword leg's top hit. With ten repeats the note spans
  two chunks and the identifier sits in the second one, so that premise is about chunk count, not
  about the boundary. The ten-repeat case lives in its own test.

## Addendum (1.53.7): files the chunker must cut, and colliding file positions

A manual run against a copy of a live bank showed two gaps in the repair (#788).

- **Some files were re-ingested on every run.** A file whose term is longer than the budget (such as
  whitespace-free JSON) is hard-cut by the current chunker too. The seam test flagged it, the
  re-ingest wrote back the same rows, and the next run flagged it again: 15 files on the first run
  of a bank copy, 14 on a second run of the same copy.
- **Colliding positions on file rows survived.** 186 file groups on the live bank held two rows at
  one `chunk_index`. Watch catch-up skips an unchanged file, and the bank-wide recomputes keep the
  existing order and only fill `-1`, so nothing ever corrected them.

Before a file group is re-ingested, the repair now re-chunks the file with the current chunker
(`ChunkPositionScanner`, the same scan `repair chunk-index` uses). When every stored row is one the
chunker still writes, a re-ingest would change nothing but positions. The group then takes the
positions and section labels the scan reports, in place, and the file is not re-ingested. That also
covers a file group whose only defect is colliding positions, which the repair now selects alongside
seamed groups, and applies to workspace file rows too, since it rewrites positions and never replaces
a row. A group holding a row the chunker no longer writes is re-ingested as before (a workspace group
is re-chunked from its rows instead, as re-ingest never touches workspace rows). A file gone from disk
keeps the old path. The code corpus has no position scan, so
a code file is still re-ingested when a seam is found, but it is counted only when its stored chunk
hashes changed.

The job is renamed `chunk-boundary-repair-v2` so a bank whose ledger already holds the v1 stamp runs
it once more. The repair is idempotent: a file already correct moves nothing. `ChunkBoundaryRepairTests`
pins the three new shapes (a file the chunker must cut, colliding positions only, colliding positions
plus a stale row) and the code-file case. Each failed before its change.
