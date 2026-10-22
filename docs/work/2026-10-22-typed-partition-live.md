# Typed partition impact on the pinned production bank

This follow-up compares native table representations using the actual stored content and structure vectors in an immutable production backup. It complements the [synthetic correctness experiment](2026-10-22-typed-partition-probe.md). It measures native SQL and table rebuild cost, not the public API or the complete production migration.

## Data and isolation

The source is `/private/tmp/air-2.1-impact/source/memory.db`, pinned by `memory.db.pin.json` with SHA-256 `e916783c35f370a0ef8823d5509522316e496f7771f3027ed2e3ea0eb09d555d`. The probe verifies that hash before measuring and opens this source with `Mode=ReadOnly` and `PRAGMA query_only=ON`. It creates each candidate table in a separate disposable database through the shipped connection factory. It never passes the source path to that factory.

The v19 source has no declared entry type. The historical classification follows PR #912's shape rule: File when `source_file` is nonempty and `path=source_file`; otherwise Memory. This is a measurement backfill rule, not a substitute for the injected classifier that production writers will use. Context keys come from the existing context expression. No entry source paths, content or vector arrays are included in the raw samples.

Both corpora have 384 dimensions. Content contains 25,340 vectors (22,972 File and 2,368 Memory); structure contains 10,547 vectors. Raw per-context/type counts are recorded separately for each modality. The comparison samples the largest three contexts and the smallest context for each modality. Each context supplies an actual query vector, and all/Memory/File requests get three warmups, twenty serial samples and twenty concurrent samples using separate connections.

## Correctness and runner

`TypedVecLivePartitionProbe.Pinned_vectors_preserve_typed_cosine_neighbors_across_native_layouts` independently computes exhaustive double precision cosine over every eligible source vector, before taking ten. Candidate counts, eligibility, distinct IDs and numeric distance (absolute tolerance 0.00002) must agree. At tied boundaries any eligible ID at the oracle's boundary distance is allowed. Baseline unrestricted queries use the same oracle. Baseline typed postfilter results are measured and reported, including starvation; they are not asserted correct.

The synthetic experiment already observed the behavior-focused red failures: postfilter starvation and concatenated IN results. This follow-up's first run failed for a probe defect instead: Dapper positional record materialization rejected the provider's schema for an empty result. A parameterless neighbor class fixed that defect; it is not counted as a correctness red proof.

Run from the lane root:

```sh
AIRACCOON_TYPED_VEC_SOURCE=/private/tmp/air-2.1-impact/source/memory.db \
AIRACCOON_TYPED_VEC_LIVE_ARTIFACT=<absolute-report-json-path> \
dotnet test --no-restore --configuration Release --project tests/AiRaccoon.Tests -- \
  --filter-class '*TypedVecLivePartitionProbe'
```

The provider is Microsoft.Data.Sqlite, with SQLite 3.54.0 and sqlite-vec v0.1.9 on osx-arm64. Measurements use a Release build. Serial samples time query execution/materialization on a warm existing connection. Concurrent SQL samples exclude opening a connection; concurrent caller samples include connection opening and native setup, starting inside each dispatched worker. They do not include scheduler queue wait before the worker starts and do not represent public API caller latency.

Table rebuild duration includes inserts and transaction commit from already loaded durable embeddings; no embedding is regenerated. Vector allocation is fixed chunk capacity times dimension times four bytes. Database bytes include the disposable bank's ordinary schema, so compare them across candidates rather than treating them as production migration disk growth. Timing values are observations, never assertions.

## Measured result and choice

Choose `ctx TEXT partition key, type TEXT partition key` for the production schema step. It keeps the existing context identity, accepts singleton type equality before KNN truncation, and preserves unrestricted ordering by omitting type. The composite layout also produced correct results with its forced outer sort, but its largest-content-context concurrent SQL was slower and it needs an additional context/type encoding plus an unrestricted merge. The two-column layout is the simpler correct choice supported by this comparison.

| Modality | Layout | Rebuild ms | Chunks | Allocated vector bytes | Disposable database bytes |
|---|---|---:|---:|---:|---:|
| content | baseline | 3284.7 | 139 | 218,628,096 | 220,880,896 |
| content | composite | 3682.0 | 148 | 232,783,872 | 235,134,976 |
| content | native | 2899.5 | 148 | 232,783,872 | 235,134,976 |
| structure | baseline | 726.3 | 19 | 29,884,416 | 30,638,080 |
| structure | composite | 641.1 | 23 | 36,175,872 | 36,970,496 |
| structure | native | 693.6 | 23 | 36,175,872 | 36,970,496 |

Content allocation rises from 139 to 148 chunks, an extra 14,155,776 bytes (6.47%). Structure rises from 19 to 23 chunks, an extra 6,291,456 bytes (21.05%). Combined vector allocation grows 20,447,232 bytes, from 248,512,512 to 268,959,744 bytes (8.23%). Composite and native type keys allocate the same bytes. The synthetic fixture's 35% growth was not representative of the natural distribution.

The following SQL latencies use the largest context for each modality. Values are p50 / nearest-rank p95 milliseconds from twenty samples, not confidence intervals.

| Modality | Layout | Unrestricted serial | Memory serial | File serial | Unrestricted concurrent SQL | Unrestricted concurrent caller |
|---|---|---:|---:|---:|---:|---:|
| content | baseline | 10.062 / 10.953 | 10.176 / 11.762 | 10.577 / 14.729 | 30.721 / 52.026 | 35.193 / 58.756 |
| content | composite | 8.785 / 9.834 | 0.394 / 0.774 | 8.058 / 9.990 | 57.752 / 72.129 | 72.535 / 83.475 |
| content | native | 7.058 / 9.501 | 0.588 / 0.817 | 7.344 / 9.179 | 33.081 / 43.579 | 34.656 / 60.772 |
| structure | baseline | 1.974 / 2.543 | 2.691 / 3.196 | 2.801 / 3.373 | 9.252 / 15.727 | 22.845 / 31.548 |
| structure | composite | 3.107 / 3.569 | 0.555 / 0.717 | 2.129 / 2.695 | 11.331 / 16.692 | 14.622 / 24.604 |
| structure | native | 2.796 / 3.257 | 0.519 / 0.651 | 2.832 / 3.337 | 9.540 / 15.493 | 19.449 / 32.155 |

The largest content context has 9,546 vectors, including only 122 Memory rows. Baseline postfilter found one of the requested ten Memory neighbors; both typed representations retrieved ten. The largest structure context has 3,032 vectors including three Memory rows. Baseline postfilter retrieved none of those three, while both typed representations retrieved all three. Across sampled requests, baseline postfilter had five content count mismatches and four structure count mismatches; neither typed candidate had a mismatch. Unrestricted and typed candidates all passed the exhaustive numeric cosine oracle.

Native content unrestricted serial was 7.058 ms median versus composite 8.785 ms; concurrent SQL was 33.081 versus 57.752 ms. The structure comparison has a cost: native unrestricted median 2.796 ms versus baseline 1.974 ms, and native File median 2.832 ms versus composite 2.129 ms. Neither candidate wins every query. The measured choice accepts this structure cost for correct typed retrieval, preserved context identity and the simpler unrestricted query contract. These are one-run observations with fixed candidate order and warm native caches; they do not prove a universal speedup.

Final Release runner passed one test, zero failed/skipped, in 23.497 seconds (test body 23.004 seconds). It executed 72 context/type cases with 1,440 serial and 1,440 concurrent SQL samples, plus 1,440 connection-inclusive caller samples. Empty partitions remain valid cases; the artifact includes actual/expected rows and every concurrent result count so those cases cannot be mistaken for successful nonempty retrieval. Both modalities and every layout also had nonzero result sets.

The resource scan found eleven intentional experiment-resource hits: two environment inputs, two file input/output operations and seven Stopwatch observations. The source file read uses the pinned immutable path, destination files use disposable roots, and timing is never asserted. The scan cannot infer these controls, so they are reconciled manually. No network, random query generation or shared mutable source state is involved.

The raw [JSON artifact](2026-10-22-typed-partition-live.json) contains source hash/pin, natural distributions, exact SQL, IDs/distances and timing samples. It contains no vectors or entry text. This step does not prove reindex migration replay, application query wiring, TTL semantics, public API latency, or production encrypted-bank performance; their gates remain with the implementation and final impact steps.
