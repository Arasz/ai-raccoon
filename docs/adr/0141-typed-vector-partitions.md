# ADR-0141: partition memory vectors by context and declared entry type

Status: accepted for the 2.1 implementation, subject to migration and API integration gates.

## Context

A type restriction applied after native KNN truncation can starve. On the pinned production bank, a context with 122 Memory vectors among 9,546 returned only one of ten requested Memory neighbors after baseline postfiltering. The same issue affects structure vectors. The requested entry-type parameter therefore requires native restriction before truncation in both modalities.

The existing `ctx` key encodes scope and ownership. It is shared with other storage logic and must retain its identity. A composite context/type key and a second native partition column both satisfy typed correctness, but require different unrestricted SQL.

## Decision

Use `ctx TEXT partition key, type TEXT partition key` on both memory content and structure vector tables. Use the declared entry type supplied by the injected classifier. A singleton query uses `ctx=@ctx AND type=@type`; an unrestricted Memory+File query omits the type predicate. Code keeps its independent corpus and layout.

Do not use plain partition `IN` as a globally ordered KNN list. The shipped sqlite-vec v0.1.9 probe returned per-partition lists concatenated beyond k. If a future subset requires IN, force the global order and limit in an outer query (`ORDER BY distance+0 LIMIT @k`), or explicitly merge independently bounded candidates before ranking. Current unrestricted memory queries need neither, because omitting type gives the native global context top-k.

Migration rebuilds native vectors from durable embedding blobs without re-embedding. Context/type changes must delete and reinsert vectors, since native partition keys cannot be updated. The schema implementation must preserve IDs, dimensions and both modalities, and prove replay and writer-path consistency separately.

## Evidence and costs

The [synthetic probe](../work/2026-10-22-typed-partition-probe.md) demonstrated starvation, IN ordering failures, equality/omitted-type correctness and tied boundaries. The [Release production-copy comparison](../work/2026-10-22-typed-partition-live.md) used 25,340 content and 10,547 structure vectors and exhaustive cosine oracles.

Two native columns preserved context identity and avoided the composite layout's forced unrestricted merge. In the largest content context, native unrestricted median SQL was 7.058 ms versus composite 8.785 ms, and twenty-concurrent median SQL was 33.081 versus 57.752 ms. Content table rebuild took 2.900 seconds versus composite 3.682 seconds. These observations support the representation choice rather than promising a general speedup.

Typed partition allocation grows 8.23% across the two modalities on this bank. Structure unrestricted median rises to 2.796 ms from the baseline's 1.974 ms, and native File median 2.832 ms trails composite 2.129 ms. Accept those measured costs for native typed recall and the simpler query contract. The final public API and migration gates remain required.
