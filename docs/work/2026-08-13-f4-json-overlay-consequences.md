# Research: consequences of the missing JSON structural-chunk overlay (F4), current vs changed

**Date:** 2026-08-13
**Question:** For finding F4 (`overlayTokens` ignored on the JSON structural path), what are the
consequences of the current behavior vs the changed behavior proposed in D5 (doc comment only)?

Scope: review `docs/reviews/2026-08-13-architecture-review-ingestion-and-deps-refactor.md` F4,
plan decision D5 in `docs/plans/2026-08-13-architecture-fix-plan.md`, and the chunking code they
name. Nothing was run; every claim below is read from source or reasoned from what was read.

## Findings

### F1 — `overlayTokens` is forwarded to the fallback path, but never consulted by the structural grouping loop [READ]

The review says `ChunkObject`/`ChunkArray` "never read `overlayTokens`". At the letter, wrong:
both methods forward it into `ChunkFallback` — `JsonFileTypeChunker.cs:139` (oversized single
property), `:154` (empty result), `:183` (oversized array item), `:198` (empty result), and
`ChunkFallback` passes it through at `:221`. What never happens: the packing loops that group
whole properties (`ChunkObject`, `:124-147`) and whole array items (`ChunkArray`, `:169-191`)
never consult it when deciding what goes into the next chunk. So the material claim — "JSON
structural chunks are emitted with no overlap" — is correct; the mechanism description is
imprecise. The D5 comment should say "structural grouping is non-overlapping; `overlayTokens`
applies only on the fallback path", not "never used".

**Evidence:** `src/AiRaccoon.Infrastructure/Chunking/JsonFileTypeChunker.cs:112-199` (the two
grouping methods and their fallback forwards), `:221` (`ChunkFallback` pass-through).

### F2 — The 48-token overlay is caller-driven and documented as a chunker-wide behavior [READ]

`FileIngestor.cs:23` declares `DefaultOverlayTokens = 48`; `InsertChunksAsync` calls
`handler.Chunker.Chunk(content, chunkMaxTokens, chunkOverlayTokens)` at `:79` for **every**
handler, markdown and JSON alike (clamped to the model context at `:182-183`). The architecture
doc presents overlay as a general chunker property — "an overlay window for context continuity
between chunks" (`docs/explanation/architecture.md:216-217`) and "256 tokens per chunk with a
48-token overlay" (`:221-222`). The only scenario that *asserts* overlay is markdown-scoped
(`docs/work/features-native-memory/native-memory.feature:220-222`, "A markdown note is split
with token bounds and overlay"). So the caller believes it requested 48 tokens of overlap for
JSON; the structural path silently drops it — the "silent contract difference" F4 names is real.

**Evidence:** `src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs:23`, `:77-79`, `:167-184`;
`docs/explanation/architecture.md:216-222`; `docs/work/features-native-memory/native-memory.feature:220-222`.

### F3 — Markdown overlay re-includes whole previous lines; a unit that exceeds the budget is dropped, not split [READ]

`MarkdownChunker.BuildOverlay` walks the previous chunk's units (lines / fence blocks)
backwards, inserting each whole unit while `used + unit.TokenCount <= overlayTokens`, and breaks
the moment a unit doesn't fit (`MarkdownChunker.cs:48-70`). Two properties follow: overlap
granularity is the whole line, and a single oversized unit contributes *nothing* to the overlay
(the break happens before it is inserted). This is the mechanism a JSON "fix" would inherit.

**Evidence:** `src/AiRaccoon.Core/Chunking/MarkdownChunker.cs:25` (overlay applied at chunk
start), `:48-70` (`BuildOverlay`), `:60` (budget break).

### F4 — JSON structural units are whole properties/items; a markdown-style overlap would duplicate them wholesale [READ]

`ChunkObject` packs complete `JsonProperty` objects (`GetRawText()` of the value, `:126`,
re-emitted whole in `BuildObjectChunk` `:201-219`); `ChunkArray` packs complete array items
(`item.GetRawText()`, `:171`). A chunk is a valid JSON fragment assembled from complete
key/item units. There is no sub-unit the overlay could re-include: overlapping the structural
path means re-emitting whole previous properties or items at the head of the next chunk. D5's
"overlap would mean duplicating whole properties" is confirmed against the code, not just
plausible.

**Evidence:** `src/AiRaccoon.Infrastructure/Chunking/JsonFileTypeChunker.cs:124-147` (object
packing), `:169-191` (array packing), `:201-219` (`BuildObjectChunk`).

### F5 — Current vs changed, mechanically [INFERRED]

Reasoned from F2-F4. **Current:** JSON structural chunks carry no duplicated content; the cost
is lost cross-boundary context for retrieval — a query whose answer spans a property/item
boundary must rely on hybrid search retrieving both chunks independently, which it can (both
are in the bank and individually matchable), but nothing guarantees their adjacency in results.
**Changed** (someone "fixing" F4 with markdown-style overlap): every chunk after the first
starts by duplicating tail properties/items of its predecessor up to 48 tokens. Consequences:
(a) the same property text is embedded twice → near-duplicate embeddings and two near-identical
chunks returned for one query; (b) F3's unit-fit rule means any property/item larger than 48
tokens is dropped from the overlap entirely, so the overlap would be spotty and surprising
(small keys duplicate, big ones don't); (c) the dedup hash (`ContentHash.Of(path, chunk)`,
`FileIngestor.cs:94`) won't collide because the chunk strings differ, so nothing accidentally
dedupes the duplication; (d) ~48/256 ≈ 19% of each JSON chunk's budget would be spent on
repeated context whose value is lower than in prose, because a JSON property names its own
meaning while a markdown line is a fragment of a continuing thought. D5's "semantic difference,
not a silent bug" is the accurate characterization.

**Evidence:** reasoning from F2 (caller passes 48 to both), F3 (overlay unit semantics),
F4 (JSON unit granularity), and `FileIngestor.cs:94` (hash of path+chunk).

### F6 — The D5 comment is the right scope, but its planned wording would itself be slightly wrong [INFERRED]

D5 changes zero behavior and makes the contract explicit — appropriate for a NIT on new code.
But the plan's phrasing ("structural chunks are non-overlapping by design") would be inaccurate
as written, because oversized single properties/items *do* get overlay via `ChunkFallback`
(F1). The comment needs the exception clause, or the next reader will "correct" the behavior
in either direction. Also worth one sentence of why: units are whole keys/items, so overlap
duplicates content rather than restoring context.

**Evidence:** reasoning from F1 (fallback forwards overlay), F4 (unit granularity), and the
comment text proposed in `docs/plans/2026-08-13-architecture-fix-plan.md:68-73`.

### F7 — No test pins the JSON no-overlap behavior, in either direction [READ]

Every `JsonFileTypeChunkerTests` call passes `overlayTokens: 0`
(`JsonFileTypeChunkerTests.cs:22`, `:60`, `:71`) and none asserts anything about overlap on the
structural path. The overlay assertions live in markdown tests only
(`MarkdownChunkerTests.Split_WithOverlay_ReusesTailOfPreviousChunk` `:22`,
`TokenizerChunkerTests.Chunk_DefaultBounds256Overlay48_...` `:41`, BDD
`NativeMemorySteps.cs:1203-1216`). So today's no-overlap is unasserted: an accidental future
change either way would go unnoticed. The doc comment mitigates, but does not replace, an
assertion.

**Evidence:** `tests/AiRaccoon.Tests/Unit/Ingestion/JsonFileTypeChunkerTests.cs:22,60,71`;
`tests/AiRaccoon.Tests/Unit/Chunking/MarkdownChunkerTests.cs:22`;
`tests/AiRaccoon.Tests/Unit/Chunking/TokenizerChunkerTests.cs:41`;
`tests/AiRaccoon.Tests/BDD/NativeMemorySteps.cs:1203-1216`.

## Still open

- Whether the lost cross-boundary context measurably hurts JSON retrieval quality: would need a
  retrieval-harness run on a JSON corpus with overlapping vs non-overlapping structural chunks
  (nDCG comparison). Not run here — F4 is a NIT and the harness is the deciding tool.
- Whether Step 7 of the plan will adopt the corrected comment wording (fallback exception +
  why); it is a plan artifact, not yet code.
- Whether the "spotty overlap" property (F5b) actually bites on real JSON configs: depends on
  the distribution of property sizes in ingested files, unmeasured.
