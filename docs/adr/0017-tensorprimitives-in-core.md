# 0017 — TensorPrimitives in AiRaccoon.Core

Date: 2026-08-08

Status: Accepted (dependency and kernel land together; the Evidence section holds the
measured result — the kernel is 4.5× faster at the decisive case, so the ship condition
of plan WP-4 criterion 5 is met)

## Context

`AiRaccoon.Core` is the clean domain layer by shape: other projects reference it, it
references none of them, and — before this change — its only packages are
`FluentValidation` and `CommunityToolkit.Diagnostics`. `EmbeddingMath.MeanPoolAndNormalize`
(`src/AiRaccoon.Core/Embedding/EmbeddingMath.cs:16-63`) is domain math: it defines what an
AiRaccoon embedding *is* (FR-NM-3, sentence-transformers mean-pool + L2-normalize semantics),
which is why it lives in Core today. Its three scalar loops — per-row accumulate,
divide-by-count, divide-by-norm — are exactly the shape the .NET SIMD guidance calls out:
"prefer the already-accelerated `TensorPrimitives.Add`" over hand-rolled `Vector<T>`/
`Vector128<T>` element-wise float span math.

`System.Numerics.Tensors` ships stable in .NET 10 but is **not in-box** — per Microsoft's
"What's new in .NET 10 libraries," the tensor APIs still require an explicit
`PackageReference`; they are not part of the .NET 10 shared framework. The package is
already present in `AiRaccoon.Benchmarks`'s resolved graph at 10.0.10, pulled in
transitively via `Microsoft.Extensions.AI` 10.8.3, and is already used there at
`benchmarks/AiRaccoon.Benchmarks/Embedders/EmbeddingBackend.cs:53`
(`TensorPrimitives.CosineSimilarity`). That does not help `AiRaccoon.Core`:
`CentralPackageTransitivePinningEnabled` is `false` in `Directory.Packages.props`, so no
`PackageVersion` entry exists for it and Core will not resolve the type without one added
explicitly.

`EmbeddingMath.` has exactly one call site in `src/` —
`src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs:89-91` — so this decision
affects one production caller.

## Decision

Add `System.Numerics.Tensors` as a `PackageReference` on `AiRaccoon.Core`, pinned via
`Directory.Packages.props` at `10.0.10` (matching the version already resolving
transitively in the benchmarks graph, so no version split is introduced across the
solution), and vectorize `EmbeddingMath.MeanPoolAndNormalize`'s three scalar loops with
`TensorPrimitives.Add`/`Divide`/`Norm`.

`TensorPrimitives` is pure computation — no I/O, no HTTP, no persistence, no serialization —
so it is not in the class of dependency the clean-layering rule forbids (ASP.NET Core, EF
Core, Azure SDKs, `System.Net.Http`, transport/serialization namespaces). The precedent is
exact: ADR 0001 added `FluentValidation` to Core on the same reasoning — "`AiRaccoon.Core`
gains one third-party dependency (pure logic, no I/O); the clean-layering rule requires this
ADR for that change." This is the same shape of decision and gets the same treatment.

The clean-layering invariant asks that a new dependency on the domain layer be recorded
wherever this project records decisions; it does not yet have a build-time enforcement
mechanism. **No ArchUnitNET domain-purity test exists in `tests/AiRaccoon.Tests` today** —
the rule is advisory, checked at PR review, not by CI. Nothing mechanically stops a future
dependency from landing in Core; wiring that check is a separate task, made more pressing
now that a second package has crossed the boundary (tracked as a reported finding in the
performance plan, not worked here).

## Consequences

- **Positive:** the domain math that defines an AiRaccoon embedding gets the
  runtime-maintained, already-vectorized implementation instead of either staying scalar or
  being hand-rolled; `MeanPoolAndNormalize`'s norm computation moves off a manual
  double-accumulated `lengthSquared` loop onto `TensorPrimitives.Norm`.
- **Negative:** `AiRaccoon.Core` — the layer this repo deliberately keeps thin — now carries
  two third-party packages instead of one. This is a real, ongoing cost: every future
  reviewer of Core's dependency list has one more entry to justify, and the layer is
  measurably less "framework-free" than it was.
- **Positive:** the win is measured, not assumed — 4.5× at the decisive case
  (`SeqLen = 256, MaskDensity = 1.0`; see Evidence), with an unchanged allocation profile
  (1.52 KB per call, the returned `float[]`, in both arms).
- **Neutral:** the clean-layering rule's ArchUnitNET enforcement remains unwired (see
  Decision); this ADR is the only thing on record stopping a silent third dependency from
  landing next.

## Alternatives rejected

- **(b) Move `MeanPoolAndNormalize` to `AiRaccoon.Infrastructure`, leave `AiRaccoon.Core`
  scalar or empty of this method.** Mechanically viable —
  `OnnxEmbeddingGenerator.cs:89-91` is the only production caller of `EmbeddingMath`, so
  relocating it breaks nothing else. Rejected because it relocates domain math purely to
  dodge a numerics package, which is the clean-layering rule's "don't extend the boundary to
  fit the SDK" read backwards: mean-pool + L2-normalize defines what an embedding *is*
  (FR-NM-3), and that belongs in the domain layer regardless of which package computes it.
  It also *costs* rather than saves — `AiRaccoon.Benchmarks` references only
  `AiRaccoon.Core` today, so the benchmark would need a new `AiRaccoon.Infrastructure`
  project reference plus an `InternalsVisibleTo` grant (Infrastructure currently grants that
  only to `AiRaccoon.Tests`) to keep exercising the kernel. **This is the named fallback if
  the owner rejects the `AiRaccoon.Core` dependency** — nothing else in the plan changes
  except the file paths in WP-4.
- **(c) Hand-roll `Vector<T>`/`Vector128<T>` in Core to avoid the package.** Rejected — the
  .NET SIMD guidance argues against this directly for exactly this shape of code (prefer
  `TensorPrimitives` over hand-rolled vector types for element-wise float span math), and it
  means writing and maintaining lane-width dispatch and tail handling for a ~15-line kernel
  that the runtime team already wrote, tested, and ships as a stable API. "Ask if a simpler
  shape would do" points at the package, not at reimplementing it.
- **Math.NET Numerics.** MIT-licensed and capable, but buys nothing over `TensorPrimitives`
  here: everything `MeanPoolAndNormalize` needs — element-wise add, divide, sum-of-squares,
  sqrt, L2 norm — is already covered by `TensorPrimitives`, which operates directly on
  `ReadOnlySpan<float>`/`Span<float>`, the shape the code already uses end to end (from the
  ONNX output buffer through to the returned `float[]`). Adopting Math.NET Numerics instead
  would mean a second numeric type system in Core plus conversions to and from spans at both
  boundaries. Its SIMD acceleration depends on an optional native MKL provider, which is
  heavier to set up than a managed, in-box-adjacent BCL package for no functional gain here.

## Evidence

`EmbeddingMathBenchmark`, 2026-08-08, on an otherwise idle machine (an earlier run under
IDE load produced StdDev up to ~38% of mean and was discarded):

```
BenchmarkDotNet v0.15.8, macOS Tahoe 26.6.1 (25G76) [Darwin 25.6.0]
Apple M4, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.302
  [Host]     : .NET 10.0.10 (10.0.10, 10.0.1026.32716), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.10 (10.0.10, 10.0.1026.32716), Arm64 RyuJIT armv8.0-a

| Method     | SeqLen | MaskDensity | Mean         | StdDev     | Ratio | Allocated |
|----------- |------- |------------ |-------------:|-----------:|------:|----------:|
| Scalar     | 16     | 0           |     45.90 ns |   1.683 ns |  1.00 |   1.52 KB |
| Vectorized | 16     | 0           |     45.60 ns |   3.247 ns |  0.99 |   1.52 KB |
| Scalar     | 16     | 0.5         |  1,450.24 ns |   6.667 ns |  1.00 |   1.52 KB |
| Vectorized | 16     | 0.5         |    286.22 ns |   0.623 ns |  0.20 |   1.52 KB |
| Scalar     | 16     | 1           |  2,402.55 ns |  14.863 ns |  1.00 |   1.52 KB |
| Vectorized | 16     | 1           |    445.28 ns |   4.135 ns |  0.19 |   1.52 KB |
| Scalar     | 64     | 0           |     50.52 ns |   0.354 ns |  1.00 |   1.52 KB |
| Vectorized | 64     | 0           |     50.89 ns |   0.342 ns |  1.01 |   1.52 KB |
| Scalar     | 64     | 0.5         |  4,266.52 ns |   9.478 ns |  1.00 |   1.52 KB |
| Vectorized | 64     | 0.5         |    765.15 ns |   9.174 ns |  0.18 |   1.52 KB |
| Scalar     | 64     | 1           |  8,216.93 ns | 126.153 ns |  1.00 |   1.52 KB |
| Vectorized | 64     | 1           |  1,438.63 ns |  41.834 ns |  0.18 |   1.52 KB |
| Scalar     | 256    | 0           |    108.70 ns |   0.291 ns |  1.00 |   1.52 KB |
| Vectorized | 256    | 0           |    112.04 ns |   2.337 ns |  1.03 |   1.52 KB |
| Scalar     | 256    | 0.5         | 15,692.15 ns |  94.115 ns |  1.00 |   1.52 KB |
| Vectorized | 256    | 0.5         |  3,612.32 ns |  39.573 ns |  0.23 |   1.52 KB |
| Scalar     | 256    | 1           | 31,701.36 ns | 592.827 ns |  1.00 |   1.52 KB |
| Vectorized | 256    | 1           |  6,989.85 ns |  51.998 ns |  0.22 |   1.52 KB |
```

The decisive case — `SeqLen = 256, MaskDensity = 1.0`, the realistic hot case since
`RunBatch` pads every row in a batch to `maxLen` and 256 is `MaxSequenceLength` — measures
31,701 ± 593 ns scalar vs 6,990 ± 52 ns vectorized: ratio 0.22 (4.5× faster), a margin far
outside the run's noise (RatioSD ≤ 0.03 on every loaded row). Every masked/loaded
combination lands at ratio 0.18–0.23; the `MaskDensity = 0` rows are the early-return path,
where both arms do no pooling work and measure equal, as expected. Allocation is identical
in both arms (the returned `float[]` only), so the win is pure compute.
