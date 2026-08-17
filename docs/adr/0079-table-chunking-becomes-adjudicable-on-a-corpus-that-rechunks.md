# 0079. Table chunking becomes adjudicable on a corpus that re-chunks

Date: 2026-08-17

Status: Accepted

Builds the measurement [ADR-0077](0077-table-chunking-is-not-adjudicable-on-a-table-blind-corpus.md)
named as missing. That record refused to ship a chunking change and listed three things that would
have to exist first; this one supplies all three and records what they measured. Production chunking
is still unchanged — `MarkdownChunker` is untouched. Relates to
[ADR-0048](0048-a-chunk-is-a-well-formed-markdown-fragment.md) (the unbuilt header carry-over),
[ADR-0056](0056-a-retrieval-gate-measured-off-its-tuning-set.md) (a gate measured off its tuning
set) and [ADR-0042](0042-fixtures-are-built-by-the-product-not-beside-it.md) (fixtures are built by
the product). Raised by [#367](https://github.com/Arasz/ai-raccoon/issues/367).

## Context

ADR-0077 is a refusal, and it stated its own release condition: (1) a graded query set whose
expected documents contain tables, (2) a gate that re-chunks rather than reading a committed
fixture, and (3) a response variable that survives the change under test. Until (1) and (3) existed,
"a number produced here is evidence for refusing, not licence for shipping".

Two things since made the refusal worth revisiting rather than leaving standing. Replacement
semantics shipped, so the correctness properties are no longer blocked on ingest. And table-bearing
chunks turn out to be retrieved **1.48× more often** than others — 10.62% of them have ever been
accessed against 7.20% of the rest. That measure is partly circular, since access reflects what the
ranker already surfaced, but it establishes tabular content as retrieval-relevant rather than a
niche corner, which is what makes the cost of building a corpus worth paying.

## Decision

**Build the measurement, ship no chunking change.** Three pieces, each addressing one blocker.

**A corpus that can see tables.** Thirteen documents vendored from `ai-badger` and `arasz-home-page`
at pinned commits (`scripts/table-corpus-sources.json`, vendored by
`scripts/vendor-table-corpus.py`), with sixteen graded queries whose answers are table cells
(`scripts/table-corpus-queries.json`). Both sources are external to this repository, so no retrieval
parameter has ever been swept against them — the corpus is held out by construction rather than by
partition, which is ADR-0056's failure mode addressed at the source. The mix is deliberate: articles
carrying one table mid-prose (the exact chunk shape #367 complained about) and dense reference
tables.

**A gate that re-chunks.** `TableCorpusBank` ingests that markdown at test time through the
production `FileIngestor`, so chunk boundaries move under a chunking change. This is the direct
answer to ADR-0077's finding that `HeldOutRetrievalGateTests` reads a committed bank and therefore
"goes green having measured nothing", and it follows ADR-0042: the fixture is built by the product,
not committed beside it.

**A response variable anchored on the answer.** A chunk is relevant when it comes from the expected
document **and** carries the graded answer span. The ground truth stays defined when an arm deletes
the chunk id it used to be written in terms of — the defect that made four of ADR-0077's six arms
unscoreable.

## What it measured

At the production 254-token budget, 343 chunks: **mean nDCG@5 0.070683, mean MRR@10 0.089658**. Most
queries score exactly zero. A paraphrase of a table cell rarely retrieves the chunk holding it,
which is #367's complaint reproduced across a corpus rather than on one hand-picked chunk.

The anchor statistics say why. The chunk carrying a graded answer is on average only **49% table
content**, the rest unrelated prose. Where that chunk is table-dominated, a literal probe of the
answer text scores MRR 1.0; where it is prose-dominated it scores 0.0 **even though the chunk
contains the exact string being searched**. ADR-0077 reasoned this out from one chunk — "the row is
not what is packed; the *chunk* is" — and it now holds as a measurement over sixteen.

## The gate is not blind, and that is checked

Re-ingesting the same documents at a 128-token budget takes the corpus from 343 to 784 chunks and
moves 4 of 16 per-query nDCG@5 scores — T8 0.50 → 0.00, T11 0.00 → 0.39, T12 0.00 → 0.39,
T14 0.63 → 0.39. The committed jsaa fixture could not move at all. That comparison is itself a test
(`ADifferentChunkBudget_MovesTheScores`), so the blindness cannot return unnoticed, and
`TableCorpusIntegrityTests` holds the corpus to it: every document carries a table, every answer
span occurs exactly once and lives inside a table row, and no query quotes its own answer.

## Two findings recorded rather than smoothed over

**Reversal is not a valid discriminator on this corpus.** A reversed top-10 *outscores* the real
order — nDCG@5 0.161 against 0.071 — because when the answer-bearing chunk reaches the top 10 at
all, it usually sits in the bottom half. ADR-0077 saw the same on jsaa query A8, and ADR-0078 notes
the held-out tier contains a query a reversal improves. The gate uses **mispairing** instead:
grading each query against the next query's document, which collapses both means to exactly 0. A
perturbation that raises the score is not a floor test, and reversal must be measured before it is
adopted on any new corpus rather than assumed to degrade.

**A span anchor bounds mechanical inflation but does not freeze it.** The 48-token overlay copies a
span into adjacent chunks, so cutting the corpus 6.2× finer took the largest relevance set from 1 to
3. The defensible claim is boundedness, not constancy: span-anchored sets stayed ≤ 4 while
whole-file relevance — the metric this replaces, still used by `HeldOutRetrievalGateTests` through
`CorpusHashMap.FileHashes` — grew 5.9× over the same change. Any cross-arm comparison must therefore
report relevance-set sizes beside the scores, so an arm that gained by multiplying answer-bearing
units is visible rather than silently rewarded.

## Consequences

- **Positive:** ADR-0077's three release conditions are met. A table-chunking arm can now be scored
  against a corpus that contains tables, through a gate that re-chunks, on a response variable the
  arm does not destroy.
- **Positive:** the baseline is on record, so an arm has something to beat rather than a number
  invented alongside it.
- **Negative:** the gate embeds live rather than replaying pinned vectors, so its floors carry
  hardware variance the jsaa gates do not. They are pinned conservatively (mean nDCG@5 ≥ 0.050, mean
  MRR@10 ≥ 0.070) and the discriminating checks are the relational ones — mispairing, budget
  movement, relevance bounding — rather than the absolute floors.
- **Negative:** identity retrieval is 0.6875, not 1.0 — searching a chunk's own text does not always
  return that chunk first. It is gated as a smoke check (> 0.5) and is not a ranking claim.

  **Not truncation.** A chunk is built to fit `MaxContentTokens` (254 = the bundled model's
  256-token window less `[CLS]`/`[SEP]`, [ADR-0036](0036-engine-aware-chunk-token-budget.md)), so a
  chunk used as a query sits at or under the window, and
  [ADR-0071](0071-a-query-is-trimmed-deliberately-and-said-so.md) pins that a query exactly on the
  limit is untouched. The anchors here are 714–1151 characters; none is trimmed.

  **Not the read-path query guard.** The natural next guess, since a chunk of markdown pasted as a
  query resembles the machine output [ADR-0040](0040-read-path-query-guard.md) exists to refuse. It
  does not apply: `IQueryGuardService` and `QueryLengthGuard` are invoked only in
  `src/AiRaccoon/Tools/MemoryTools.cs`, the MCP tool layer, and this gate calls
  `SqliteMemoryStore.SearchAsync` directly, which references neither.

  **Not a narrow relevance set either.** Scored against the exact anchor hash alone, mean MRR is
  0.687500 — identical to the span-set figure, digit for digit. The anchor is simply outranked: in
  11 of 16 cases by a sibling chunk of the same document, in 3 (T1, T8, T9) by a chunk of a
  different file. That is consistent with the two mechanisms
  [ADR-0078](0078-the-no-fusion-regression-rule-is-an-order-and-ships-default-off.md) already
  measured — RRF paying consensus over a decisive single leg, and `SourceAffinityRanker` promoting
  adjacent siblings by roughly seven rank positions each — rather than anything about this corpus.
- **Unchanged:** no production behaviour. ADR-0077's two correctness properties (prose and tables
  never share a chunk; no header-orphaned body row) remain unbuilt. They are unblocked by
  replacement semantics, deterministic, and gateable without any of this — but this corpus is what
  would score the tuning arms once one is chosen.
