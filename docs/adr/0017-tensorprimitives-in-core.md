# 0017 — TensorPrimitives in AiRaccoon.Core

Date: 2026-08-08

Status: Accepted (dependency and kernel land together; Evidence below is a placeholder
filled by WP-5 once the benchmark runs — per plan WP-4 criterion 5, the kernel ships only if
it measures faster, and this ADR is withdrawn with it if it does not)

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
- **Negative:** the win this ADR exists to justify is **unmeasured as written**. The
  benchmark that settles it (WP-4/WP-2's `EmbeddingMathBenchmark`) has not run yet at the
  time this ADR is filed; see Evidence below. If the measured ratio at the realistic
  worst case is not a clear improvement, the kernel is reverted and this ADR is withdrawn
  with it — a dependency that does not measure faster has no buyer.
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

Placeholder — filled by WP-5 with the `EmbeddingMathBenchmark` summary table (candidate vs.
scalar baseline, `Ratio` column, every `SeqLen`/`MaskDensity` combination) **including
BenchmarkDotNet's host/runtime header**, per plan WP-2 criterion 5 and WP-4 criterion 4. The
decisive number is `SeqLen = 256, MaskDensity = 1.0` — the realistic hot case, since
`RunBatch` pads every row in a batch to `maxLen` and 256 is `MaxSequenceLength`. Per WP-4
criterion 5: if that ratio is not a clear improvement, the kernel is reverted and this ADR's
Status above changes from Accepted to Withdrawn.
