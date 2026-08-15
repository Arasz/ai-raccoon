# 0068. `ctx` is a vec0 metadata column, not a partition key

Date: 2026-08-15

Status: Accepted

Supersedes the vec0 shape chosen in `docs/plans/2026-08-08-search-knn-perf.md` §3.1 (ladder step v2).
That decision stands on everything else it settled — the context key's encoding, the single KNN over
`ctx` instead of a post-filter — and is superseded only on whether `ctx` is declared `partition key`.

## Context

WP5 / H11. `vec0` chunks are fixed-capacity: the `size` column is 1024 on every row and each blob is
exactly 1,572,864 B (1024 × 384 × 4) whether the chunk is full or holds one vector. On the live bank
that cost **43,424,256 B ≈ 43.4 MB** of allocation beyond what the vectors need, recomputed from
`dbstat` page bytes rather than assumed.

The first draft of this package blamed the 1024 default and proposed pinning `chunk_size`. **That
would have shipped, passed its gate, and reclaimed about 2% of what it promised.** The cause is the
partition key: at 20 distinct `ctx` values, 13 of them holding under 10 rows, the same 21,985 vectors
need 49 chunks instead of 22. **98% of the waste is attributable to partitioning**, and partitioning
exists to prune before the `MATCH` — so the trade is size against latency and could not be settled by
the size number alone.

`Vec0PartitionKeyProbe` measured it on 2,518 real vectors under a partition distribution synthesised
to the live bank's measured shape:

| shape | chunks | chunk bytes | KNN @ k=10 |
|---|---|---|---|
| `ctx`-partitioned (v2) | 20 | 31,457,280 | **0.343 ms** |
| `scope`-partitioned | 3 | 4,718,592 | 2.301 ms |
| unpartitioned, no `ctx` — *not a replacement* | 3 | 4,718,592 | 2.274 ms |
| **`ctx` as a metadata column** | **3** | **4,718,592** | **1.734 ms** |

**Coarsening to `scope` is dominated** — identical size, worse latency — because it keeps paying the
partition-filter machinery while pruning nothing once every row shares a scope. It was struck.

**A correction worth recording, because it nearly set the decision.** The first run measured a table
with **no `ctx` column at all** and queried it with no context filter. That returns the *global*
top-k. It is not a replacement for the current behaviour, and its 1.952 ms was the latency of a wrong
query. The shape a correct replacement needs keeps `ctx` **filterable** — just not as a partition
key. Re-measured correctly it is 1.734 ms at the same 4.7 MB: the conclusion held, and improved.

## Decision

**Demote `ctx` from partition key to an ordinary vec0 metadata column. Never remove it.**

```
vec0(ctx TEXT partition key, embedding float[384] distance_metric=cosine)   -- v2
vec0(ctx TEXT,               embedding float[384] distance_metric=cosine)   -- v9
```

The query is unchanged — `WHERE ctx = @ctx AND embedding MATCH @vec AND k = @limit` — because a
metadata column is filterable with the same predicate. Ladder step **v9** rebuilds `vec_entries` and
`vec_structure` from `entries.embedding` / `entries.structure_embedding`, which is why those columns
are not deletable.

`vec_noise` keeps its partition key. It is dead by evidence (ADR-0039 amendment) and reaches only
legacy banks, so rebuilding it would be work with no measured return.

**Owner ruling, 2026-08-15**, on an explicit trade: **~26.7 MB against +1.4 ms per KNN**.

## Consequences

**The size win is real and so is the latency cost.** 85% of the chunk bytes go — 31.5 MB → 4.7 MB on
the measured corpus — and every KNN gets slower by roughly 1.4 ms. This is a deliberate trade, not a
free win, and anyone quoting one half of it is quoting half a decision.

**What this does not settle**, stated so nobody quotes it further than it goes: the partition
distribution in the probe is synthetic (the vectors are real, the `ctx` assignment is not), it is one
corpus at 2,518 vectors against a live bank of ~16k, and `k=10`. **Whether the 1.4 ms gap grows or
shrinks with corpus size is unmeasured.**

**This change is invisible in production until the store times its own phases.** Nothing in
`AiRaccoon.Infrastructure` measures a search's phases today, so a 1.4 ms move on the hot path cannot
be observed after it ships. That is the concrete argument for
`docs/plans/2026-08-15-performance-observability-design.md`, and this decision is its first customer.

**`MigrateToV2Async` now creates the demoted shape directly**, because it shares its vec0 DDL with
v9. The end state after a full ladder run is identical either way, and one DDL cannot drift from
itself. The ladder stays append-only: no step was renumbered or deleted.

**One test assertion was adjudicated rather than exempted.**
`EnsureAsync_OnAV1Bank_RebuildsVecEntries_…` asserted `sql.ShouldContain("partition key")`. That was
a *transcription* of the shape v2 happened to create, not the contract it was written to protect —
which is that a v1 bank leaves the ladder ctx-scoped, cosine, holding embedded rows only. All four
are still asserted; only the wording that named the old shape changed.
