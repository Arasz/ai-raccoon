# 0036. Engine-aware chunk token budget with a guaranteed split floor

Date: 2026-08-14

## Status
Accepted

## Context
`MarkdownChunker`/`JsonFileTypeChunker` counted chunk sizes with `O200kTokenizer` (`o200k_base`,
via `TiktokenTokenizer`), a proxy chosen for general budgeting. The bundled local embedding engine
(`OnnxEmbeddingGenerator`) tokenizes with BERT WordPiece instead and hard-truncates at
`MaxSequenceLength = 256`. These are different tokenizers over the same text, and their token
counts are not interchangeable.

A MoE codebase review (`docs/reviews/2026-08-14-moe-codebase-review.md`, RAG-F3/RAG-F4) measured
two concrete failures against this repo's own `docs/**/*.md` corpus, reproduced and re-verified
during this work:

1. **Token-unit mismatch.** Chunking at the o200k-counted budget of 256 produced entries whose real
   BERT WordPiece length regularly exceeded 256 — measured mean ratio 1.06, p95 1.22 on this
   corpus — silently truncated at embed time with no log, no counter, `embed_state='embedded''`.
2. **Unbounded fence atomicity.** `BuildUnits` treated any code fence as one atomic unit regardless
   of size, and an unterminated fence glued the rest of the document into a single chunk with no
   upper bound at all (measured: one document produced a 5621-token chunk, 95% of it truncated).

A stopgap of lowering the local-engine budget by a fixed ratio (e.g. 200 tokens, covering the
measured p95 of 1.217×) was evaluated and rejected: a ratio is a statistical property of a specific
corpus, not a guarantee. It has an unbounded tail — hex/base64 blobs, long identifiers, URLs and
CJK text tokenize at very different BERT/o200k ratios than English prose, and any fixed ratio still
silently truncates on content the ratio was not measured against, which is exactly the kind of
content this project stores (hashes, paths, config).

## Decision

**1. Count with the tokenizer that will actually embed the chunk, not a proxy.**
`IChunker.Chunk` gained an additive, optional `TokenCount? countTokens = null` parameter (default
= the constructor-injected counter, so all existing callers and the DI-registered o200k singleton
in `AppRegistrations.cs` are unaffected). `FileIngestor` resolves the bank's configured provider at
ingest time and, for `local`, supplies a real `BertTokenizer` (built via
`OnnxEmbeddingGenerator.CreateTokenizer`, sharing the exact tokenizer options the embedder itself
uses — no hand-duplicated config to drift) as that override. Other providers keep the o200k default
unchanged; the measured mismatch is specific to the bundled model's real tokenizer, not a general
problem with o200k as a budgeting proxy.

**2. The budget is derived from the engine's real window, not guessed.** The bundled model adds
`[CLS]`/`[SEP]` special tokens at embed time (`OnnxEmbeddingGenerator.Encode` calls `EncodeToIds`
with `addSpecialTokens: true`) and then truncates at `MaxSequenceLength = 256` — truncation can
drop the trailing `[SEP]`. The chunker budget for `local` is
`OnnxEmbeddingGenerator.MaxContentTokens = MaxSequenceLength - 2 = 254`: a chunk tokenizing to
exactly 254 real content tokens fills the 256-token window (CLS + 254 + SEP) without ever reaching
the truncation branch. `EmbeddingService.SafeChunkBudgetFor(provider, model)` is the single place
this is resolved.

**3. No unit can ever be emitted over budget — a guarantee, not a heuristic.** Three changes to
`MarkdownChunker` close this:
   - **Verify the joined chunk against the real tokenizer, not a sum of per-unit counts.**
     BPE/WordPiece token counts are not composable across a join (`tokens(a) + tokens(b) !=
     tokens(a+b)`); the previous algorithm trusted the sum. `BuildChunk` now assembles candidate
     chunks by the cheap summed heuristic, then re-tokenizes the actual joined text and sheds units
     (overlay first, then trailing new units) until it fits. A bug in this shrink step (it skipped
     verification entirely whenever the fast loop only added one new unit, so an overlay-plus-unit
     combination could silently exceed budget) was found and fixed during the corpus-scale gate —
     see Verification.
   - **Fence atomicity is capped, not special-cased.** A closed fence stays one atomic unit only
     while it fits maxTokens (re-verified exactly, not from the running estimate); an oversized or
     never-closed fence falls back to line-granular units. An unterminated fence at EOF is *never*
     atomic, regardless of size — it isn't a well-formed fence, so gluing the rest of the document
     to it was never correct.
   - **Any unit that is still oversized falls back to token-level splitting.** A single line, a
     minified-JSON blob, one very long word — anything that individually exceeds maxTokens is
     binary-searched (via the injected `countTokens`) down to the largest prefix that fits, and the
     remainder is split the same way. This always terminates (worst case: one character at a time)
     and is the floor beneath every coarser split. `JsonFileTypeChunker` gets the same guarantee via
     a final verify-and-fallback pass over its own grouped output.

   `Core.Chunking` stays infrastructure-free (no dependency on `Microsoft.ML.Tokenizers`): the
   guarantee is built from binary search over the already-injected `TokenCount` delegate, not from
   a tokenizer-specific offset API. `Tokenizer.GetIndexByTokenCount`/`GetIndexByTokenCountFromEnd`
   (available on both `BertTokenizer` and `TiktokenTokenizer`) were evaluated as a faster primitive
   for the same job and rejected for this wave only because binary search is simpler to keep inside
   `Core` without a new dependency — the API is a valid future optimization if profiling ever shows
   the binary search cost matters (it did not: full-corpus chunking completes in under a second).

**4. Truncation is observable, not silent.** `OnnxEmbeddingGenerator.Encode` is the last-resort
choke point for every chunk that reaches the local engine regardless of how it was produced
(ingestion, direct `memory_write`, etc.), so it logs (not just the chunker):
   - `EventId 414` when a chunk still gets truncated at embed time (should be provably zero after
     this change, on any content the corpus/hostile-fixture gate covers).
   - `EventId 415` (new, defense-in-depth) when a chunk's real content collapses to almost no
     tokens — see the known gap below.

## Known gap — recorded, not fixed this wave

**A long, punctuation-free, newline-joined run collapses to a single `[UNK]` token.** This
tokenizer's pretokenizer (`BertOptions` as configured for the bundled model) does not treat
newline, tab, or CR as word boundaries — only spaces and punctuation split words. A run of
~100+ characters with none of those (e.g. many SHA-256 hex lines joined by `\n`, with no spaces)
is treated as a single "word", exceeds WordPiece's per-word decomposition limit, and collapses to
`[UNK]` — reporting an implausibly *small* token count for real content. This is invisible to a
budget ceiling check (the offending chunk measures as tiny, not large) and is a different failure
mode from RAG-F3/RAG-F4: content that is misrepresented, not truncated.

Measured against the live bank (15,246 entries): **1 entry** contains a qualifying run, a 123-char
fragment inside a 3,843-char value — low current impact. `docs/**/*.md` in this repo does not
trigger it under the production chunk budget. `OnnxEmbeddingGenerator.Encode` now logs it
(`EventId 415`) so it is visible rather than silent; a characterization test
(`ChunkingCorpusGuaranteeTests.NewlineSeparatedHashList_StillCollapsesToUnknown_DocumentsKnownGap`)
asserts the *current, wrong* behaviour on purpose — it stays green, pinning the gap so a future
regression or fix is visible instead of silent. When the gap is addressed, that assertion must be
inverted; that inversion is the signal the fix landed. Chunker-side remediation (e.g. treating this
collapse as "doesn't fit" and forcing a finer split) was prototyped and deliberately not shipped —
at 1/15,246 it does not justify complicating the splitter's termination logic in the same change
that rewrote it.

## Verification

- `MarkdownChunkerTests`/`ChunkingFeatureTests`: fence-atomicity-under-budget, oversized-fence and
  oversized-line fallback, unbalanced-fence-no-chunk-over-budget — RED against the pre-fix chunker,
  GREEN after.
- `ChunkingCorpusGuaranteeTests.ChunkingDocsCorpus_WithRealBertTokenizer_NoChunkExceedsTheContentBudget`:
  chunks this repo's `docs/**/*.md` (3,884 chunks) through the production local-engine path with
  the real `BertTokenizer` and asserts a hard ceiling of 256 total tokens (254 content + CLS/SEP)
  — zero violations after the fix. Before the algorithm fix (BERT counting + budget 254, but
  without the joined-chunk verification and split floor), this same corpus produced chunks up to
  305 tokens; before *any* fix (o200k counting, budget 256, atomic fences), 1356 of 3636 chunks
  (37.3%) exceeded the model's window.
- `ChunkingCorpusGuaranteeTests.ChunkingHostileFixtures_...`: a hex/base64 blob, a minified-JSON
  line, a CJK paragraph, and an unbalanced fence — all within budget.
- `OnnxEmbeddingGeneratorLoggingTests`: EventId 414/415 fire on the conditions they're meant to
  and stay silent otherwise; each was broken on purpose and watched go red before being restored.
- `SqliteMemoryStoreChunkColumnMaintenanceTests`... other existing chunking/ingestion/embedding
  tests (146 total in the affected areas) pass unchanged.

## Consequences
- **Positive:** local-engine chunks can no longer silently exceed the model's window; the fence
  bug (RAG-F4) and the counting-unit mismatch (RAG-F3) are both closed by the same general
  mechanism rather than two special cases.
- **Positive:** the `[UNK]`-collapse failure family, though not fixed, is now visible via a
  dedicated log event instead of reporting a healthy `embed_state`.
- **Negative:** `IChunker.Chunk` gained a fourth parameter (additive/optional, but a wider surface);
  `FileIngestor` now knows how to build a real `BertTokenizer` directly rather than only going
  through DI-registered chunkers.
- **Negative / follow-up:** the `[UNK]`-collapse gap remains open; a future wave should decide
  between chunker-side remediation (treat the collapse heuristic as "doesn't fit") or a
  pretokenizer/normalization change, informed by whether real usage ever pushes past the
  measured 1/15,246 rate.
- **Not done:** JSON key-path chunking (RAG-F12) — explicitly deferred to Wave 5 per the plan.
