# Typed entry application impact

The application probe measured the integrated `SqliteMemoryStore.SearchAsync` path over a
read-only pinned bank copy. The source bank was 1,129,660,416 bytes with SHA-256
`e916783c35f370a0ef8823d5509522316e496f7771f3027ed2e3ea0eb09d555d` (user version 19).
Opening the disposable copy upgraded it in 5,980 ms. The upgraded database measured
1,145,184,256 bytes with 384-dimensional embeddings, 148 content-vector chunks and 23
structure-vector chunks. It contained 25,340 rows: 22,972 File and 2,368 Memory; 25,157 had
embeddings. SQLite vec was v0.1.9 on osx-arm64.

The Release probe used cached source vectors as query vectors, with no model inference or network
calls. It measured two project corpora, Memory, File, Memory plus File, and unrestricted selections,
with 20 serial calls and three waves of 20 concurrent calls for every case: 640 calls total. The
exhaustive double-precision cosine oracle passed for every case (10 returned of 10 expected,
recall 1.0). Results were grouped by value as the application merger does. The test also checked
all concurrent result lists against the serial result for that case.

Caller-visible latency is milliseconds, p50 / nearest-rank p95 from one warm run. The final two
columns show the corresponding internal `SearchTimings.Total` p50; the serial path includes opening
the bank for each call, and concurrency measures 20 callers per wave.

| Corpus | Selection | Eligible rows | Serial caller | Concurrent caller | Internal total p50, serial / concurrent |
|---|---|---:|---:|---:|---:|
| corpus-1 | Memory | 139 | 4.24 / 6.06 | 26.80 / 37.53 | 4.24 / 26.80 |
| corpus-1 | File | 9,424 | 8.64 / 9.72 | 83.98 / 105.19 | 8.63 / 83.97 |
| corpus-1 | Memory + File | 9,563 | 11.47 / 12.78 | 138.31 / 156.25 | 11.47 / 138.31 |
| corpus-1 | Unrestricted | 9,563 | 10.81 / 11.72 | 137.11 / 173.53 | 10.80 / 137.11 |
| corpus-2 | Memory | 271 | 16.72 / 18.44 | 285.85 / 303.69 | 16.71 / 285.84 |
| corpus-2 | File | 5,313 | 9.53 / 10.12 | 92.95 / 112.57 | 9.53 / 92.95 |
| corpus-2 | Memory + File | 5,584 | 22.39 / 34.99 | 348.13 / 365.32 | 22.38 / 348.13 |
| corpus-2 | Unrestricted | 5,584 | 19.09 / 20.21 | 176.95 / 368.17 | 19.09 / 176.95 |

These are observations from one machine and one run, not a general latency guarantee. This probe
isolates the Memory/File vector path at `alpha=1`, `ftsWeight=0`, and `vectorWeight=1`; the code
corpus is separate. It does not compare the public hybrid dispatcher or compare application latency
against the former layout. The natural-distribution SQL layout comparison, including both vector
modalities, post-filter starvation counts, rebuild cost, and vector allocation, is in the
[typed partition experiment](2026-10-22-typed-partition-live.md). Focused application integration
tests cover FTS, code dispatch, quality masks, scopes, and typed lookup.

The [raw application samples](2026-10-22-entry-type-impact.json) include only corpus ordinals,
counts, pass results, and timings; they omit project names, entry hashes, text, and vectors.
