# 0133 — `ctx` is the vec0 partition key again

Date: 2026-10-18

Status: Accepted

Supersedes [ADR-0068](0068-ctx-is-a-vec0-metadata-column-not-a-partition-key.md).

## Context

ADR-0068 demoted `ctx` from vec0 partition key to metadata column, trading ~26.7 MB of chunk
allocation for +1.4 ms per KNN. It said what it did not settle: one corpus of 2,518 vectors, a
synthetic `ctx` spread, single-query latency only.

The unsettled part is what decides it. With `ctx` as a metadata column every KNN reads every
context's chunks and drops the rows that do not match. vec0 reads chunk vectors through SQLite's
incremental blob I/O, which goes straight to the file as `pread` and through the cipher on every
read, outside the page cache. A `sample` of the live server under 20 concurrent searches put most
of its time in that read path, almost all of it kernel time (issue #880); a 64 MB page cache barely
moved it (sys 79.0 → 72.6 CPU-s), because those reads never touch the cache.

## Decision

**`ctx` is the vec0 partition key on `vec_entries`, `vec_structure` and `vec_code`.**

```
vec0(ctx TEXT partition key, embedding float[N] distance_metric=cosine)
```

The query is unchanged: `WHERE ctx = @ctx AND embedding MATCH @vec AND k = @k` now prunes other
contexts' chunks before reading them. Ladder step **v19** rebuilds the three tables from
`entries.embedding`, `entries.structure_embedding` and `code_entries.embedding`, skipping a table
already partitioned, then vacuums. Fresh banks, `RebuildVecTableAsync` and the dimension reconciler
all create the table through one DDL (`MemorySchema.VecTableDdl`).

The default `chunk_size` stays: the measured growth below did not justify tuning it.

**Measured** on a copy of the live bank (995 MB, same machine, same ten queries, 1.59.3 against
this change, both Release):

| | metadata `ctx` (v18) | partitioned `ctx` (v19) |
|---|---|---|
| one search, wall | 0.23 s | 0.10 s |
| 20 concurrent, wall | 3.94 s | 1.21 s |
| 20 concurrent, server sys CPU | 76.4 s | 14.2 s |
| 20 concurrent, server user CPU | 12.9 s | 8.1 s |
| bank file | 995 MB | 1,051 MB |

## Consequences

- Concurrent search scales again: 20 at once costs about 12× one search instead of about 17×,
  and the kernel share drops from 85% to 64% of server CPU.
- The bank grows by about 56 MB (5.6%) on this corpus, the near-empty chunks of small contexts that
  ADR-0068 removed. A bank with many small contexts pays more.
- A search still pays one read per chunk of its own context. A context as large as the whole bank
  sees no gain; the in-process vector store in #880 is the fix for that case.
- The first open of a v18 bank rebuilds all three vector tables and vacuums, as v9 did.
