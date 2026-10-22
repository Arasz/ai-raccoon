# Typed native KNN layout probe, 2026-10-22

This experiment chooses a vector representation for 2.1. It does not change production or mutate a live bank. The production integration baseline must include PR #912's declared entry types and schema v20; its typed vector migration is a later version.

## Contract and test design

The target is the shipped sqlite-vec native KNN surface, characterized through `TypedVecPartitionProbe.Typed_partitions_preserve_minority_and_unrestricted_nearest_neighbors`. A typed query must retrieve the nearest requested entries before truncation; an unrestricted query must preserve global per-modality top-k within the context; sparse contexts must not leak rows. The independent oracle exhaustively computes double precision cosine over the source vectors and orders before taking ten.

| Failure mode | Probe | Red mutation |
|---|---|---|
| Minority entries disappear after truncation | File restricted dense context, both query modalities | Use baseline ctx KNN followed by File postfilter |
| Multi-partition results concatenate instead of globally sorting | Unrestricted composite and native IN queries | Remove outer global sort and truncation |
| Scope leaks or empty type returns other entries | Sparse context and absent File partition | Remove ctx/type predicate |
| SQL error masquerades as successful experiment | Nonzero successful-query count per layout | Supply both native k and LIMIT |

Filesystem uses a unique disposable TestData directory and an explicit optional artifact path. Randomness uses seed 2197. No network or live bank access occurs in the probe. Time uses Stopwatch for observations only, without timing assertions. There is no shared database state. The two modality labels use two distinct query vectors over the same generic native table; this proves native SQL behavior for either vector leg, not production wiring or structure-vector migration.

## Executed red evidence

Command prefix `AIRACCOON_TYPED_VEC_PROBE=1 AIRACCOON_TYPED_VEC_RED=1 dotnet test --project tests/AiRaccoon.Tests -- --filter-class '*TypedVecPartitionProbe'`:

```text
baseline/content/dense:0/file
actual should be [1400L, 700L, 2100L, 2800L] but was []
Test run summary: Failed! total: 1 failed: 1 succeeded: 0 skipped: 0
```

A second actual failure exposed composite IN behavior: `ctx IN (@memory,@file) AND embedding MATCH @vec AND k=10 ORDER BY distance` returned fourteen rows (ten Memory followed by four File) where the oracle expected ten. `ORDER BY distance LIMIT 10` in that same SELECT also failed with SQLite's `Only LIMIT or 'k =?' can be provided, not both` error. The corrected composite SQL forces SQLite to globally sort in an outer SELECT using `ORDER BY distance + 0 LIMIT 10`.

## Scope and limitations

The deterministic corpus has 3000 vectors of 384 dimensions, twenty contexts including thirteen contexts of five entries, and a one-percent File minority. This is labelled synthetic skew, not a sample of the live distribution. The raw JSON records its hash, every context/type count, provider and native versions, build durations, fixed chunk capacity allocation and twenty warm serial query samples after three warmups for each case. Allocation measures vector chunk bytes, not total database size. No assertion depends on elapsed time.

The experiment is a characterization gate invoked explicitly, not a CI performance threshold. Production migration duration, encrypted bank behavior, concurrency, actual caller latency, three-way type combinations involving Code and production dual-vector wiring belong to the integration and pinned-bank impact steps.

## Result and SQL recommendation

Provisionally prefer two native partition columns; the final selection requires the production-copy cost comparison. It preserves the existing context encoding and allows unrestricted searches to omit the type predicate. Both native omitted-type and singleton-equality queries matched exhaustive top-k for both query vectors and both dense/sparse contexts. Composite encoding was also correct after forcing the outer sort, but adds key encoding and always needs the multi-partition merge for unrestricted queries.

```sql
CREATE VIRTUAL TABLE vectors USING vec0(
    ctx TEXT partition key,
    type TEXT partition key,
    embedding float[384] distance_metric=cosine
);
-- Unrestricted Memory+File: omit the second partition predicate.
SELECT rowid, distance FROM vectors
WHERE ctx = @ctx AND embedding MATCH @vector AND k = @k
ORDER BY distance;
-- Singleton Memory or File: equality before KNN truncation.
SELECT rowid, distance FROM vectors
WHERE ctx = @ctx AND type = @type AND embedding MATCH @vector AND k = @k
ORDER BY distance;
```

Plain `IN` on the second native partition also returned fourteen rows instead of ten, just like composite IN. If a future caller needs a subset of more than one partition, force a global sort and truncate outside vec0:

```sql
SELECT rowid, distance FROM (
    SELECT rowid, distance FROM vectors
    WHERE ctx = @ctx AND type IN ('memory', 'file')
      AND embedding MATCH @vector AND k = @k
) ORDER BY distance + 0 LIMIT @k;
```

Current Memory+File requests should use omitted type instead. Code remains in its independent corpus.

Final correctness run passed one test (zero skipped), executing sixteen non-error cases per layout and twenty measured queries per case (960 serial timing samples total). The native and corrected composite candidates matched the oracle throughout. The baseline's dense File postfilter returned zero of four expected rows. Final command uses `AIRACCOON_TYPED_VEC_PROBE=1 AIRACCOON_TYPED_VEC_ARTIFACT=<absolute-json-path> dotnet test --no-restore --project tests/AiRaccoon.Tests -- --filter-class '*TypedVecPartitionProbe'`. Runner duration was 5.004 seconds; the test itself took 4.633 seconds. Build preflight passed five projects with zero errors and warnings.

These exploratory observations used Debug managed code, SQLite 3.54.0 and sqlite-vec v0.1.9 through Microsoft.Data.Sqlite on osx-arm64. They are useful for native feasibility and allocation comparison; release production impact remains the later pinned-bank measurement.

| Layout | Build ms | Vector allocation bytes | Dense unrestricted p50/p95 ms | Dense File p50/p95 ms |
|---|---:|---:|---:|---:|
| ctx baseline | 400.981 | 31,457,280 | 0.725 / 0.939 | 0.767 / 0.865 (incorrect recall) |
| composite ctx/type | 484.021 | 42,467,328 | 1.045 / 1.701 | 0.325 / 0.773 |
| ctx plus native type | 431.553 | 42,467,328 | 0.819 / 0.957 | 0.619 / 0.752 |

The typed layouts allocate seven extra chunks, a 35% vector-allocation increase on this deliberately sparse/skewed corpus. Two native partition columns did not reproduce the old four-column build slowdown, but unrestricted query overhead was visible and requires the production impact gate. Raw samples, exact SQL, row IDs, structure query results and corpus SHA-256 are in [the JSON artifact](2026-10-22-typed-partition-probe.json).

The resource scan found seven hits: three explicit environment controls, three Stopwatch observations and one artifact write. All are intentional experiment inputs/outputs documented above; there are no wall-clock assertions, network calls or shared database writes. The scanner cannot infer these controls and marks them unmitigated; this report reconciles them manually. Scope-leak predicate mutations and SQL-error-count mutations were not separately executed; their assertions are present but those individual red proofs remain unverified. The executed starvation and native/composite IN failures are the load-bearing representation evidence.

The final correctness revision also checks every native distance against the exhaustive cosine oracle with an absolute tolerance of 0.00001, includes the Memory singleton, and inserts fifteen equal-distance vectors competing for ten places. The tie oracle accepts any ten distinct eligible IDs with zero distance; it does not invent a row-ID tie breaker. Tie and distance assertions were added after review; their individual mutation proofs remain unverified. Allocation/build metrics exclude those fifteen tie-fixture rows.
