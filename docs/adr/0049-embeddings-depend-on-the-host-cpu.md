# 0049. The bundled model's embeddings depend on the host CPU

Date: 2026-08-14

Status: Accepted

## Context

`SourceAffinitySweepTests.Sweep_ChosenSourceAffinityConfiguration_DocumentsKnownNdcg5GapRegression`
and `RrfParameterSweepTests.Sweep_ChosenRrfConfiguration_PassesAllGates` both assert
`chosen.AdrNdcg5 >= 0.526 - GoldenFile.RankingTolerance` (≥ 0.521). On macOS arm64 the value is
0.5260827785380623, bit-stable across repeated runs. On Linux CI the same commit failed in most
runs and passed in one, and an earlier failure measured the chosen arm as *better* than the λ=0
baseline — the number moved in both directions on identical code.

The gate had already been re-pinned twice (0.650 → 0.532 → 0.526) and widened once. Neither held.

### What was measured

A branch-scoped workflow ran six identical jobs on `ubuntu-latest` from one commit
(`work/ndcg-nondeterminism`, run 31828494356), each printing the host CPU, the ONNX query-embedding
fingerprint and the resulting metric. The split is exact:

| Sample | CPU | `avx512_vnni` in `/proc/cpuinfo` | AdrNdcg5 | Gate |
|---|---|---|---|---|
| 1 | Intel Xeon Platinum 8573C | yes | 0.48859561353453607 | fail |
| 3 | AMD EPYC 9V74 | yes | 0.48859561353453607 | fail |
| 6 | Intel Xeon Platinum 8573C | yes | 0.48859561353453607 | fail |
| 2 | AMD EPYC 9V74 | no (`avx avx2` only) | 0.5587755695473325 | pass |
| 4 | AMD EPYC 7763 | no | 0.5587755695473325 | pass |
| 5 | AMD EPYC 7763 | no | 0.5587755695473325 | pass |

Three arithmetic paths, three different numbers, each deterministic on its own host:

| Host arithmetic | AdrNdcg5 |
|---|---|
| macOS arm64 (NEON) | 0.5260827785380623 |
| Linux x64, no VNNI | 0.5587755695473325 |
| Linux x64, VNNI | 0.48859561353453607 |

The spread is 0.070 — fourteen times the gate's 5e-3 `RankingTolerance`. The value the gate pins
(0.526) is the arm64 one, and no Linux runner produces it.

The cause is visible in the embeddings themselves, not downstream of them. Every one of the seven
ADR query vectors differs between a VNNI and a non-VNNI host, in the third decimal place — for
example query A6's first component is `-0.08167766` (VNNI), `-0.075505406` (no VNNI) and
`-0.08283846` (arm64). That is a relative difference of ~8%, four orders of magnitude larger than
IEEE rounding.

`src/AiRaccoon/Models/model_qint8_arm64.onnx` contains 48 `MatMulInteger` and 48
`DynamicQuantizeLinear` nodes and no `QGemm`/`QLinearMatMul`. `DynamicQuantizeLinear` emits uint8
activations against int8 weights, so every matmul is **u8s8**. ONNX Runtime implements u8s8 on
x64 with `VPMADDUBSW`, which accumulates into int16 and can saturate, and on VNNI hardware with
`VPDPBUSD`, which accumulates into int32 and cannot. The two instructions are not two roundings of
one answer; they are two different answers. arm64 takes a third path again.

The rank consequences are small and land exactly on the top-5 boundary the metric reads. Query A6's
relevant chunks sit at ranks [4,5,7] on arm64, [4,6,7] without VNNI and [6,7,8] with VNNI — the last
scoring nDCG@5 = 0 and dragging the seven-query mean down by 0.0396.

### What was ruled out, with evidence

- **Model provisioning.** Both `model_qint8_arm64.onnx` and `vocab.txt` are committed to git (no
  LFS), and the sweep constructor throws unless `BundledModel.EnsureAsync` reports `AllPresent`.
  No CI log in either the red or the green run contains a download, sha-mismatch or missing-asset
  line. A degraded vector leg would also not produce a boundary-only shift.
- **ONNX thread partitioning.** Running the same token batch through sessions built with
  `IntraOpNumThreads` of 1, 2, 4 and 8 produced **bit-identical** 384-float vectors (0/384
  components differing).
- **Float-noise tie flipping.** Perturbing the query embedding by a relative epsilon and
  re-measuring: the metric is unchanged at 1e-8 through 5e-2 and only moves at 0.2. The smallest
  adjacent fused-score gap across the seven queries is 1.75e-4, most are ~1e-2. These are not
  near-ties, and ordinary rounding cannot cross them — which is why the difference had to be a
  materially different arithmetic path rather than a different rounding of the same one.
- **Unstable tie-breaking.** `ReciprocalRankFusion`, `SourceAffinityRanker` and the vec0/FTS SQL
  all carry ordinal `Path` tiebreakers; `CorpusHashMap.Build` reads `ORDER BY source_file,
  chunk_index`. The ground-truth map is deterministic given the fixture.
- **Cross-test interference.** Every consumer of `Resources/jsaa-memory.db` copies it to a private
  temp root before opening it, and `JsaaCorpusRegenerationTool` is env-gated and skips on CI (it is
  the one skipped test in both runs). Both sweep classes reported the *same* value to 17 digits in
  the same job, so the computation is deterministic within a host.

## Decision

**Record that retrieval output is a function of the host CPU, and treat the two sweep gates'
pinned `AdrNdcg5` as measuring the host, not the ranking configuration.**

This is a product property, not a test artifact. The bundled model is u8s8-quantized, so the same
query against the same bank returns a different ordering on a VNNI host, a non-VNNI x64 host and an
arm64 host. A bank whose document vectors were embedded on one machine and queried from another is
comparing vectors produced by two different implementations of the same model. For a memory server
whose contract is reliable retrieval, that is the finding — the red gate is how it surfaced.

No bound is widened here. Widening was tried twice and is what let the property stay hidden.

## Consequences

The two sweeps cannot assert a cross-platform `AdrNdcg5` constant while the query vectors are
recomputed per host. The corpus vectors are already a pinned fixture; the query vectors are the one
un-pinned input, and that asymmetry is what the gates are measuring. Three ways forward, none of
them taken in this ADR because each is a product decision with its own re-pinning cost:

1. **Pin the query vectors as a fixture.** The sweeps exist to compare λ, consolidation threshold,
   RRF k and weights — not embedding quality. Feeding them stored query vectors makes them
   deterministic everywhere and measure what they are for. Cost: they stop noticing an embedding
   regression, which needs its own gate.
2. **Re-quantize with `reduce_range`.** The standard ONNX Runtime remedy for exactly this: 7-bit
   weights cannot saturate `VPMADDUBSW`, so the x64 paths agree. Keeps int8 size and speed. Cost: a
   new model, a new pinned sha256, and every retrieval golden re-measured. Does not by itself make
   arm64 and x64 agree.
3. **Ship the fp32 model.** Removes the quantization path entirely; residual cross-ISA difference
   is ~1e-7, which the perturbation measurement above shows this pipeline is insensitive to. Cost:
   ~90 MB instead of 23 MB, and slower CPU inference.

### Chosen: none of the three — the property is accepted and documented (2026-08-14)

The owner's call, taken with the costs above in hand.

The gate half of the problem is already solved elsewhere: ADR-0050 gives the two sweeps committed
query vectors, so they measure ranking configuration rather than the host, and a green Linux run of
them now means what it says. What remains is the product property, and it is accepted rather than
fixed:

- **Re-quantizing with `reduce_range` was rejected** because it fixes only one of the two splits.
  Both x64 paths would agree, but arm64 and x64 would not — and that is the split between this
  project's development machine and both its CI and its likely deployment target. It costs a new
  model, a new pinned sha256 and a re-measurement of every retrieval golden to close half the gap.
- **Shipping fp32 was rejected on cost, not on effectiveness.** It would work: residual cross-ISA
  difference is ~1e-7, four orders below the 5e-2 perturbation this pipeline was measured
  insensitive to. But the model ships *inside* the NuGet package (`Pack="true"` in
  `src/AiRaccoon/AiRaccoon.csproj`), not downloaded at first run, so it is 22 MB → ~90 MB on every
  `dotnet tool install`, plus slower CPU inference on the write path.

What makes the accepted risk bounded: a bank embedded and queried on one machine is
self-consistent, the variance is in ranking *order* rather than in whether content is reachable,
and the FTS leg is unaffected — so hybrid fusion often rescues a vector-leg reordering.

**Revisit when cross-machine use becomes real.** The defect only bites when a bank's document
vectors were embedded on one host and queried from another, which is precisely what cloud sync
(`memory_sync`) exists to do. If sync moves from available to used, fp32 becomes worth its 90 MB
and this decision should be re-taken. The cheap first step then is a benchmark of fp32 inference
cost on the write path, since package size would be its only remaining objection.

`tests/AiRaccoon.Tests/Integration/PlatformNumericsProbe.cs` reproduces the table above; it is
env-gated on `AIRACCOON_PLATFORM_PROBE` and skips by default.

ADR-0015's "GGUF SIMD paths shift the margin per platform" and the per-platform golden assets in
`ReferenceAssets` recorded the same phenomenon for the previous engine. This ADR names its
mechanism for the ONNX engine and its size.
