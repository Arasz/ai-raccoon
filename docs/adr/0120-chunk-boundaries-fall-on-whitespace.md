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
over the budget is affected.

## Decision

**When a line must be split, the cut backs off to the last whitespace inside the budget.** One pure
helper, `TokenBudget.SplitLength` in `AiRaccoon.Core.Chunking`, answers how many characters to split
off. It finds the longest prefix within budget by the same binary search as before, then walks back
to the last whitespace character in it and cuts just after that character. The whitespace stays at
the end of the earlier piece, so the pieces still concatenate to the original line.

- **Every split site uses it.** `AddUnitOrSplit` (prose and any over-budget line), `SplitForSubFence`
  (a long line inside a re-fenced code block, where identifiers matter just as much) and
  `CodeChunker.AddLineOrSplit` all call `SplitLength`. The private binary search in `MarkdownChunker`
  is gone; `TokenBudget.Trim` keeps its behaviour for query trimming and shares the search.
- **A word longer than the whole budget is still hard-cut**, at the budget, and only that word. No
  whitespace inside the budget means there is nowhere better to cut. That word stays unfindable by an
  exact keyword match, as it was before; it cannot fit in any chunk, so no boundary rule could fix it.
  Text with no whitespace at all (CJK prose, minified JSON, a base64 blob) therefore splits exactly
  as before.
- **Leading whitespace does not count as a cut point.** A cut after only leading whitespace would
  emit a whitespace-only piece, so the walk stops before the first non-whitespace character and
  falls back to the hard cut. `SplitLength` always returns at least 1, so every split loop still
  makes progress and terminates.
- **The budget holds.** The chosen prefix is never longer than the longest in-budget prefix, and
  token counts are non-decreasing in prefix length for every tokenizer here (the assumption the old
  search already made), so no piece can exceed `maxTokens`. ADR-0036's guarantee is unchanged, and
  so is ADR-0048's fence balance.

**No overlap is added.** Overlap would only help if a boundary could still cut a word, and after
this change the only word a boundary cuts is one longer than the whole budget, which no overlap
smaller than the budget could hold either. Overlap also costs every multi-chunk note extra rows of
duplicated text in both the keyword and the vector index. The existing unit-level `overlayTokens`
setting is untouched.

## Consequences

- **Positive.** Every whitespace-delimited word shorter than the budget appears whole in at least one
  chunk, so keyword search finds identifiers wherever they fall.
  `ChunkWordBoundaryTests` pins the rule for prose, plain text, overlay, a fenced line, a word longer
  than the budget and text with no whitespace. `MemorySearchRankingTests.Search_IdentifierInAMultiChunkNote_IsFoundByTheKeywordLeg`
  pins it end to end with the ten-repeat note from #695; both failed before the change.
- **Neutral: pieces can be shorter.** A split piece may now end up to one word short of the budget,
  and a short run of words before a very long token becomes its own small piece. Chunk counts on
  over-budget lines can rise by at most a little.
- **Neutral: no migration.** Only lines longer than the budget change their boundaries. Existing rows
  keep their chunks until the file is re-ingested or the note rewritten; the new boundaries apply to
  new writes and re-ingests only, and hash-keyed replacement on re-ingest already handles a changed
  chunk set. No re-chunk job is needed; an old mid-word row stays as findable as it was.
- **The ranking test keeps eight repeats.** `Search_AllTermsKeywordMatchThatWinsFusion_StaysFirstAboveBoostedNeighbours`
  asserts that the note's first row is the keyword leg's top hit. With ten repeats the note spans
  two chunks and the identifier sits in the second one, so that premise is about chunk count, not
  about the boundary. The ten-repeat case lives in the new test instead.
