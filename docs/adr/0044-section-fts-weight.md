# 0044. The section column's FTS weight is 4, not 16

Date: 2026-08-14

Status: Accepted

## Context

`entries_fts` is an external-content FTS5 table over `(value, source_file, section)`, ranked with
`bm25(entries_fts, 1.0, 8.0, 16.0)` — a section match counted **16×** a body-text match. That
weight was chosen in `docs/plans/retrieval-improvement-c.md` §3 2c so that identifier and section
tokens outrank cross-referencing prose.

It was never exercised. `FileIngestor` wrote `section` as `NULL` for every ingested chunk, so on
any real bank the 16 applied to an empty column. The weight and the column were fixed together
without either being measured against the other: the only rows with a section came from
`memory_write`'s explicit argument (517 of 15,325 on the live bank).

Populating `section` from the heading leaf made the weight load-bearing for the first time, and
five ranking gates moved. That raised the question this ADR answers: is populating the column a
net win, and is 16 the right weight for it?

## Decision

**Keep `section` populated, and lower its bm25 weight from 16 to 4.**

Measured on the regenerated corpus (2518 entries, 871 with a section), over the 19 gradeable
queries of `scripts/baseline-queries.json`, via
`BaselineMetricsTests.RunBaseline_ComputesMetricsAndWritesReport`:

| arm | file-level nDCG@5 | MRR | recall@5 |
|---|---|---|---|
| section NULL (as shipped before) | 0.5703 | 0.7605 | 0.3329 |
| section populated, weight 16 | 0.5733 | 0.7737 | 0.3315 |
| **section populated, weight 4** | **0.5846** | 0.7518 | **0.3381** |

On the FTS-only path, where the weight actually bites, populating the column is clearly better
than not: nDCG@5 0.3529 → 0.3966, recall@5 0.5789 → 0.6316 at weight 16. On exact-chunk relevance
weight 4 beats both endpoints (nDCG@5 0.5559 → 0.5927, recall@5 0.6842 → 0.7368).

Weight 4 and weight 2 are metric-identical, so this is a plateau rather than a tuned point; 4 was
taken as the value inside it nearest the original intent.

The isolation was checked rather than assumed: `SearchByFilter` never selects or joins `section`,
and `SourceAffinityRanker` keys only on `SourceFile`, so the column reaches retrieval **only**
through FTS. Confirming that, vector-only top-5 is byte-identical across all 44 queries in both
arms.

## Consequences

- Ranking changes for every existing bank. This is a deliberate, measured change, not a drift.
- `AndPrimary_AtBoundary_A4DecisionChunkRestoredByFallback` returns to green: at weight 16 a
  mis-extracted section outranked the answer; at 4 it does not.
- Three gates are re-pinned to post-change measured values. All three fail on the **same single
  query, A1**, and its top-5 at weight 4 is *better* than its pin assumes — see below.

### A1's relevance label is incomplete (not fixed here)

A1 asks "Why was shadcn/ui chosen over gluestack.io?" and its `expectedSource` names only
`docs/adr/0011-frontend-chassis-stack.md#decision`. At weight 4 that chunk ranks 3, behind chunks
of `docs/explanation/frontend-architecture.md` — including its section
"3. The gluestack → shadcn/ui pivot", whose text reads "T21's original framing suggested
evaluating **gluestack.io** for the component layer. It was evaluated and **rejected**. This
section states the evidence plainly."

That is a correct answer to the question, arguably the better one. The gate scores it a regression
only because the catalog admits one source per query. The pins are moved to the measured values;
the catalog's one-source-per-query shape is the actual defect and is left open — fixing it means
an additive `alternativeSources` field and touching the five duplicated `BaselineQuery` record
declarations.

### Heading extraction can produce prose sections (not fixed here)

The `README.md` row that displaced A4's answer carries the 99-character section
`"Functions host key; the CI end-to-end suite authenticates through it. Named for the MCP server that"`.
Its chunk is a shell code block: the chunker split the document mid-fence, so the chunk text
begins inside an unterminated fence and `HeadingPathParser` reads the block's `#` comments as
level-1 headings. Three of 871 sections are affected. Lowering the weight reduces the damage from
16× to 4× but does not remove the cause; the fix belongs with the chunker's fence handling
(the same family as ADR-0036's unbalanced-fence finding).
