# 0048. A chunk is a well-formed markdown fragment

Date: 2026-08-14

## Status
Accepted

## Context

`HeadingPathParser.Parse` tracks fenced code blocks with a boolean that starts `false` and toggles
on every ``` / ~~~ line. It is called on **one chunk at a time** — by `FileIngestor.HeadingSection`
for the `section` column, and by `EntryEmbedder` for the `heading_path` column and the structure
embedding. When a chunk begins *inside* a code block, the opening marker is not in view, the parser
believes it is in prose, and the block's `#` shell comments become level-1 headings.

[ADR-0044](0044-section-fts-weight.md) recorded this defect as exposed but not fixed, and lowered
the `section` FTS weight from 16 to 4 to reduce the damage. The worst case on the committed corpus
(`tests/AiRaccoon.Tests/Resources/jsaa-memory.db`) is a `README.md` chunk whose `section` is the
99-character sentence `Functions host key; the CI end-to-end suite authenticates through it. Named
for the MCP server that` — a `curl` block's comment line, in an FTS-weighted column.

The parser cannot know it started inside a fence from the chunk alone. The information belongs to
whoever split the document, and [ADR-0036](0036-engine-aware-chunk-token-budget.md) is where it was
lost. To guarantee that no chunk exceeds the embedding window, that ADR made `MarkdownChunker`
**de-fence** any fence it could not keep atomic: an over-budget or never-closed fence was flushed as
bare lines, so a boundary could land inside a code block. It also abandoned a fence region the
moment the running estimate crossed `maxTokens`, which left the region's real closing delimiter to
be read as an *opening* one — inverting the fence parity of everything after it, so prose was
treated as code and code as prose for the rest of the document.

Measured with the production local-engine path (BERT tokenizer, budget 254, overlay 48) over this
repo's own `docs/**/*.md`: **70 of 4,126 chunks** ended in a different fence state than they began
— direct proof that a boundary fell inside a fence.

A length cap on `section` was rejected: it filters the symptom, and 2 of the 6 sections over 80
characters on the corpus are legitimate long headings, so a cap is both lossy and unsound.

## Decision

**A chunk is a well-formed markdown fragment.** Every unit `MarkdownChunker` builds is either an
ordinary line or a *complete* fence, so every chunk — and every overlay, which is a suffix of whole
units — opens and closes its fence state within itself. A consumer reading a chunk on its own can
no longer mistake fenced content for prose, whether that consumer is `FileIngestor`, `EntryEmbedder`
or one not written yet. This is a property of the text, not a parameter threaded through one call
path, which is why it is fixed in the chunker rather than by passing fence state to the parser.

> **Scope amendment — 2026-08-15, project-scope review.** The Decision above is precise: it claims
> every chunk *opens and closes its fence state within itself*, and that is what was built and
> measured (96/96 balanced on a 50 KB single fence). **The title generalises further than the
> guarantee does**, and a reader who takes "well-formed markdown fragment" at face value will be
> wrong in two measured cases: a 200-row table split at `maxTokens=150` yields 34 chunks of which
> **33 carry orphaned body rows with no header**, and a 20 KB single-line document at
> `maxTokens=100` puts **14 of 31 boundaries mid-word** — `AddUnitOrSplit`/`LargestPrefixWithinBudget`
> is a token binary search with no word awareness. The guarantee delivered is **fence balance**.
> Table-header carry-over and word-boundary awareness are unbuilt, not broken.

Three changes to `MarkdownChunker` carry it:

1. **An over-budget fence is re-fenced, not de-fenced.** `FlushAsSubFences` splits the region into
   consecutive bounded fences, each repeating the region's own opening and closing delimiter. Each
   content piece is sized against the delimiters it will be emitted with (binary search over the
   injected `TokenCount`, exactly as ADR-0036's split floor does), so a single piece always fits and
   the greedy packer's shrink step still terminates inside budget.
2. **A never-closed fence is closed, not abandoned.** The synthesized closer matches the opener's
   marker character and run length. ADR-0036's guarantee is unchanged — the region is still never
   one unbounded atomic unit — but it stays a fence while being bounded.
3. **The mid-region bail-out is removed.** A fence region now runs to its closing delimiter or EOF.
   This is what re-aligns fence parity for the rest of the document.

> **Scope amendment — 2026-08-23, #538 (revised on the gate's #538 follow-up, QA'd on PR #543).**
> A second guarantee joined fence balance without a title change: a deferral never trades away a
> chunk's only real content — `DeferOpenSection` refuses to cut a heading loose when nothing but
> other headings and blanks would remain (a lone heading is exactly as unindexable as a lone blank
> line). **Except** a section longer than any one chunk, whose heading opens only the first of its
> chunks and the rest continues headless (deferring it further would never terminate), and only
> heading levels 1–2 count as section openers to begin with, excluding the ingest `## Source:`
> provenance header — matching the headings `HeadingPathParser` keeps (docs/adr/0004), so a `###`+
> subheading or a `## Source:` line at a chunk tail never triggers a defer that shrinks the chunk
> without changing its label. A chunk can still *open* a section with no content of its own when
> the greedy pack itself — not the deferral rule — cannot fit any body beside the heading (an
> oversized next unit, or headers alone exhausting the budget), or at an overlay ratio that leaves
> room for one unit; the label problem that shape used to cause is closed one layer up (next
> amendment). `MarkdownChunker.DeferOpenSection`.

> **Scope amendment — 2026-08-23, #549/#550 (PR #543).** A chunk's section label no longer comes
> from re-reading the chunk's own text. `MarkdownChunker.ChunkWithHeadings` returns each chunk with
> the heading path in force at its first contentful new unit (overlay units excluded; `""` when the
> chunk has no contentful new unit at all) and the leaf of every section the chunk holds content
> for; `FileIngestor` stores those leaves as `section` (joined, since the column is an FTS column
> and a `file#section` anchor — docs/adr/0003 — matches by phrase) instead of calling
> `HeadingPathParser` on the chunk, and refreshes the column on the dedup path so an
> already-ingested bank gets the label without a re-embed. Every chunk of a section therefore
> satisfies that section's anchor — continuation chunks, the first chunk of a unit too large to sit
> beside its heading, and a one-chunk document holding several whole sections — and a chunk that
> merely opens a section claims none. Chunk text, boundaries and budgets are unchanged
> (docs/adr/0036 untouched), except that a whitespace-only chunk is no longer emitted. Measured on
> 2 000 seeded documents at 254/48 (design probe, PR #543): chunks with no section 22 636/29 605 →
> 0; a single first-unit label disagreeing with the section owning the chunk's last body line
> 173/29 604 (0.58 %, all straddle chunks) — which is why the column carries every section the
> chunk holds rather than one. `heading_path` and the structure embedding still come from
> `EntryEmbedder`'s re-parse and are unchanged; aligning them moves docs/adr/0004's gates and is
> tracked as #562. `MarkdownChunker.ChunkWithHeadings`, `HeadingStack`, `TextChunk`.

`AiRaccoon.Core.Chunking` stays infrastructure-free: all of this is built from the already-injected
`TokenCount` delegate.

**Cost, accepted deliberately:** the emitted text of an over-budget fence is no longer a byte-exact
substring of the source — it gains the repeated delimiters, and a mid-line split gains the newline
that the closing delimiter needs to start its own line. The fenced *payload* is preserved exactly,
in order, and that is what the tests pin. Chunk hashes for such regions change, so a re-ingest
inserts new rows rather than deduplicating against the old ones — the same consequence ADR-0036 had.

## Verification

Every gate below was watched RED against the pre-fix chunker before the change landed.

- `ChunkingCorpusGuaranteeTests.ChunkingDocsCorpus_EveryChunkIsFenceBalanced` — this repo's own
  `docs/**/*.md` through the production local path: **70/4,126 chunks unbalanced before, 0/4,143
  after**. This is the structural invariant, asserted on real documents.
- `ChunkingFeatureTests.IngestingNoteWithOversizedShellFence_NoChunkTakesItsHeadingFromInsideTheFence`
  — the defect itself, reproduced from the shape of the corpus `README.md` chunk.
- `ChunkingFeatureTests.IngestingNoteWithOversizedFence_EveryChunkIsFenceBalanced` — backtick,
  tilde and never-closed variants.
- `FileIngestorSectionColumnTests.IngestFileAsync_OversizedShellFence_NeverTakesTheSectionFromInsideTheFence`
  — the same document through the real ingest path, asserted on the stored `section` column.
- ADR-0036's own gates keep their budget assertions; three of them pinned byte-exact concatenation
  of a de-fenced region and now pin the fenced payload plus the balance invariant instead.

**Measured against the corpus's source documents** (job-search-ai-assistant at the pinned commit
`9397bbef504b5b30a31003c84e8c5c316641adb6`), re-deriving `section` for every `.md` file the corpus
contains, before vs after:

- **4 fabricated sections removed** — `README.md` ×2 (`Functions host key; …`, `used to be its only
  caller`), `infra/README.md` ×1 (`can reach it.`), and `owner-gate-review/references/result-template.md`
  ×1 (`Not answered`, a heading inside a ` ```markdown ` example block).
- **13 real headings recovered**, mostly where a large mermaid or shell fence had inverted the
  parity of the rest of the file: `docs/flows.md` ×8, `docs/data-model.md` ×2, `docs/architecture.md`,
  `README.md` (`Status`), `infra/README.md` (`Variables`), `.ai-badger/…/file-schemas.md`.
- **8 real headings lost and 1 fabricated one gained, all in `docs/how-to/deploy-the-application.md`.**
  That file has an unmatched fence delimiter at line 525, so its fence parity is genuinely inverted
  from there to EOF and a CommonMark renderer shows §7–§11 as code. The pre-fix bail-out
  accidentally resynchronized and recovered those headings; the new behaviour is faithful to the
  source instead. Papering over it would mean guessing which delimiters the author meant.

The committed corpus fixture was **not** regenerated: the source documents and the production
chunker were enough to measure the change without a 7-minute rebuild of a binary fixture that six
retrieval gates read.

## Consequences

- **Positive:** `section`, `heading_path` and the structure embedding are all fixed by one change,
  because the defect is removed from the text rather than from one caller. No interface changed.
- **Positive:** ADR-0044's weight reduction was a mitigation for this cause; it can be revisited on
  a corpus regenerated with this chunker.
- **Negative:** chunk text for an over-budget fence is no longer byte-identical to the source, and
  the committed corpus fixture is now stale with respect to the chunker.
- **Not done / follow-up:** a document with an odd number of fence delimiters is silently
  misinterpreted from that point on. Nothing detects or reports it. Flagging such documents at
  ingest — an observation, not a repair — is the honest next step, and would have named
  `deploy-the-application.md` instead of leaving its lost headings to be found by diffing.
- **Not done:** the corpus fixture regeneration and the retrieval-pin re-check that follows it.
