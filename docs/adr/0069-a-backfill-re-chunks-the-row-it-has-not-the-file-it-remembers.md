# 0069. A backfill re-chunks the row it has, not the file it remembers

Date: 2026-08-15

Status: Accepted

## Context

WP3 step 4. Rows written before `memory_write` chunked (ADR-0064), or ingested while the budget was
not engine-aware (ADR-0063), hold more text than the embedding window — so the text past the window
is absent from that row's own vector. Measured on the live bank with the same tokenizer the chunker
uses:

| | |
|---|---|
| rows | 17,219 |
| window | 254 tokens |
| **over-window rows** | **6,240 — 36.2%** |
| worst single row | **7,376 tokens — 29× the window** |
| **tokens never embedded** | **377,431** |

**No existing path could repair this.** `IngestFileAsync` only *inserts*. Re-chunking changes
boundaries, so hashes change — re-ingesting would have **added** 6,240 rows beside the stale ones and
grown the bank while appearing to fix it. Only `ReplaceFileAsync` replaces, and it reads from disk.

## Decision

**Split each over-window row's own stored value. Never read the source file.**

The improvement plan preferred **document-level** re-chunking, and argued it well: *"a single chunk
cannot be re-chunked in isolation — re-chunking re-derives every boundary in its document."* That is
true, and it is the better shape when the document is available. It is not always available:

- **109 of 1,283** source files no longer exist on disk
- **33** over-window rows have no `source_file` at all
- re-reading a file that changed since ingest **silently rewrites content** rather than re-chunking it

So the boundaries a backfill produces are not the boundaries a fresh ingest would. In exchange it
depends on nothing outside the bank — not the disk, not the ingest scope allowlist, not files that
are gone — and both acceptance criteria hold.

Pieces are written **pending**; the existing embed pass owns embedding. Inlining it would hold a
write lock across 13,578 ONNX runs.

## Consequences

**Validated on a copy of the live bank**, with the same probe before and after:

| | before | after |
|---|---|---|
| rows | 17,219 | 24,557 |
| over-window rows | 6,240 | **0** |
| tokens unembedded | 377,431 | **0** |
| chars | 16,223,589 | 16,927,622 (+704,033, +4.3% from overlay) |

6,240 rows replaced by 13,688 pieces. The arithmetic does not close — `17,219 − 6,240 + 13,688 =
24,667` against an actual **24,557** — because **110 pieces hit `ON CONFLICT DO NOTHING`** against the
bucket unique index. Identical text appearing in two chunks dedupes. Stated rather than left as an
unexplained 110.

**The gate is a pair, and each half was watched fail.** The two acceptance criteria are opposites and
either alone is trivially satisfiable:

| Break | Result |
|---|---|
| delete without reinserting | **"nothing over the window" PASSES** — deleting satisfies it — only "loses no text" fails |
| refuse to split | three tests fail, "loses no text" passes |

One criterion alone would have shipped a backfill that deletes 36% of a bank and reports success.

**A row the chunker cannot split is left alone.** Replacing one over-window row with one identical
over-window row is churn, not a repair, and the report's own count then shows it was not fixed rather
than claiming it was.

**This runs as a once-ever maintenance job** (ADR-0070), so every user's bank heals rather than only
the one where the tool was run by hand.
