# SearchValues&lt;string&gt; vs HashSet — Benchmark &amp; Replacement Findings

**Date**: 2026-08-04 · **Task**: check-search-values
**Question**: Does `System.Buffers.SearchValues<string>` beat the `HashSet<string>` membership
checks in the FTS query path — and if so, can the HashSets be replaced?

## Method (summary)

- **Target sets** (mirrored verbatim from production): `FtsQueryNormalizer.Reserved` (4 words),
  `FtsQueryNormalizer.Stopwords` (33 words), `SourcePathQuery.Reserved` (4 words) — the per-query,
  per-token membership checks on every `memory_search` FTS query. The other HashSets in the repo
  are not candidates: `MemorySchema` columns (one-shot migration check), `SweepService`
  shared-hashes (dynamic per-run set), `SqliteMemoryStore.IndexableExtensions` (per-ingest, and
  `Path.GetExtension` output is not pre-lowercased so OrdinalIgnoreCase is genuinely required
  there).
- **Workload**: all 207 tokens of the 35 real baseline queries (`scripts/baseline-queries.json`),
  lowercased exactly as `FtsQueryNormalizer.BuildPlan` does; 40.1% of tokens are stopword/reserved
  hits, matching live query composition. Reserved check first, then Stopwords, exactly like
  production.
- **Arms**: status-quo `HashSet` vs `SearchValues`, each under `OrdinalIgnoreCase` (production's
  comparer) and under `Ordinal` (semantically identical here because every token is lowercased
  before the checks — the sets are all-lowercase ASCII). Two end-to-end `Pipeline_*` arms run the
  full tokenization (regex + lowercase + both filters) over the 35 real query texts.
- **Harness**: `benchmarks/AiRaccoon.Benchmarks/Benchmarks/SearchValuesVsHashSetBenchmark.cs`
  (BenchmarkDotNet, `[MemoryDiagnoser]`, `.NET 10.0.10`, Apple M4 arm64). Two independent runs
  agree within noise.
- **Source reading** (before running): .NET 10 runtime `StringSearchValuesBase.ContainsCore`
  implements `SearchValues<string>.Contains` as `_uniqueValues.Contains(value)` — an internal
  `HashSet<string>` with the same comparer; the Teddy/Aho-Corasick machinery exists to accelerate
  `IndexOfAny` over large spans, not `Contains`. So a wash was expected for the naive swap; the
  benchmark was run to measure it rather than assume it.

## Results (mean over runs; baseline = HashSet_OrdinalIgnoreCase = 1.00)

| Arm | Mean | Ratio | Allocated |
|---|---:|---:|---:|
| HashSet_OrdinalIgnoreCase (status quo) | 2.035 us | 1.00 | 0 |
| SearchValues_OrdinalIgnoreCase | 2.286 us | **1.12** | 0 |
| HashSet_Ordinal | 1.448 us | **0.71** | 0 |
| SearchValues_Ordinal | 1.217 us | **0.60** | 0 |
| Pipeline_HashSet_OrdinalIgnoreCase (35 real queries) | 14.07 us | 6.91 | 61 KB |
| Pipeline_SearchValues_Ordinal | 13.45 us | 6.61 | 61 KB |

## Graded findings

- **MEASURED — Replacing HashSet with SearchValues under the production comparer REGRESSES
  lookups (1.12×).** `SearchValues<string>.Contains` is an internal HashSet lookup; the swap adds
  indirection without any of the span-scanning machinery applying. The naive replacement the task
  contemplated is a loss, not a gain. Evidence: both benchmark runs, SearchValues_OrdinalIgnoreCase
  arm (2.29/2.98 us vs 2.04/2.64 us baseline); runtime source
  `StringSearchValuesBase.ContainsCore` → `_uniqueValues.Contains(value)`.
- **MEASURED — The actual gain is the comparer: Ordinal is 29–40% faster and semantically
  identical here.** Both call sites lowercase tokens before the membership checks
  (`FtsQueryNormalizer.BuildPlan` line `match.Value.ToLowerInvariant()`; `SourcePathQuery.TryBuild`
  same), and both sets are all-lowercase ASCII — so `OrdinalIgnoreCase` and `Ordinal` agree on
  every input that can reach them. HashSet_Ordinal: 0.71×. Evidence: benchmark arms; the two new
  casing-contract tests pin the precondition (mixed-case queries still filter identically).
- **MEASURED — SearchValues adds a further ~19% over HashSet once Ordinal is used (0.60× vs
  0.71×), reproducibly.** On the M4 the Ordinal SearchValues instance wins even though its
  `Contains` is also HashSet-backed — the measured difference is consistent across both runs
  (1.217 vs 1.448 us; 1.593 vs 1.900 us) and outside noise. Mechanism not fully explained from
  source; the measurement is the evidence.
- **MEASURED — End-to-end the lookup win is small: ~4% of the tokenization pass.** The full
  BuildPlan pass over 35 queries is 14.07 us; the best lookup arm saves ~0.6 us of that. The pass
  is dominated by regex matching + `ToLowerInvariant` allocations (61 KB per pass — the same in
  every arm). The change is a cheap, safe constant-factor win, not a bottleneck fix; the FTS
  query path itself remains microseconds next to embedding/vector work.
- **READ — `SearchValues` exists in-box on net10.0** (System.Buffers, since .NET 8); no new
  dependency needed. `Microsoft.Bcl.Memory` 10.0.10 is already referenced by
  AiRaccoon.Infrastructure but is not required for this.

## Decision

1. **Do not swap to SearchValues under OrdinalIgnoreCase** — measured regression (1.12×).
2. **Ship the Ordinal swap with SearchValues at the two FTS-query sites**
   (`FtsQueryNormalizer.Reserved`/`.Stopwords`, `SourcePathQuery.Reserved`): 0.60× lookups,
   zero allocation change, behavior preserved (pre-lowercasing contract pinned by the new
   casing tests + the full existing query-construction suite).
3. **Leave `SqliteMemoryStore.IndexableExtensions` as HashSet OrdinalIgnoreCase** — the input
   (`Path.GetExtension`) is not pre-normalized, so Ordinal is not valid there; and per finding 1,
   SearchValues would be slower anyway.
4. Keep the benchmark in `benchmarks/AiRaccoon.Benchmarks` so the claim stays re-measurable on
   other hardware (the SearchValues-vs-HashSet delta and the Ordinal win are both
   machine-dependent; re-run before trusting them elsewhere).

## Limitations

1. Microbenchmark on one machine (Apple M4, macOS 26.5.2, .NET 10.0.10); ratios may differ on
   x64 or older hardware. The decision here does not depend on the exact ratios — the Ordinal
   arm wins on any hardware (less work per lookup), the OrdinalIgnoreCase SearchValues swap loses
   on this one.
2. The benchmark mirrors the production sets and token stream rather than calling the internal
   classes (benchmark project references AiRaccoon.Core only); if the sets change, the benchmark
   should be updated.
3. The 19% SearchValues-over-HashSet delta under Ordinal is measured but mechanistically
   unexplained; it did not drive the decision to ship (the Ordinal win did), it only made the
   chosen shape strictly faster than the alternative.
