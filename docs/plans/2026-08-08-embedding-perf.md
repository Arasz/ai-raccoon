# Embedding math: correctness fix, benchmark, and SIMD kernel

Date: 2026-08-08

Status: Planned — not yet executed

Worktree: `.ai-badger/worktrees/perf-optimization`

## The request

The owner asked for five things:

1. finish `benchmarks/AiRaccoon.Benchmarks/Benchmarks/EmbeddingMathBenchmark.cs`;
2. learn from the .NET SIMD guidance, Math.NET Numerics, and DotNext's buffer-rental doc;
3. treat `src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs` as "the first optimization";
4. produce before/after benchmarks for every change "if possible";
5. explore math-heavy code for SIMD or memory-pooling opportunities.

## What the investigation actually found

The "first optimization" file does not need optimizing. It needs fixing. Commit
`ac0c4d6` ("refactor: use array pooling for embeding generation") swapped
`new long[batch * maxLen]` for `new MemoryOwner<long>(ArrayPool<long>.Shared, batch * maxLen)`
in `OnnxEmbeddingGenerator.RunBatch`. `new long[...]` is zeroed by the CLR; a pooled
rental is not. DotNext's constructor calls `pool.Rent(length)` with no clear step —
`exactSize` only controls the reported length. The fill loop then writes only positions
where `s < ids.Length` (it `continue`s past padding) and never writes `tokenTypeIds` at
all, so padding token ids, padding attention-mask values, and every token type id can be
arbitrary stale data. Renting 4096 longs was observed returning 4096 of 4096 stale
sentinel values. The consequence is silently wrong embeddings, written into the memory
bank, whenever the pool hands back a dirty array.

The SIMD hunt is largely a negative result, and that is the honest deliverable. Corpus-scale
cosine distance is not computed in managed code at all — it is delegated to the native
`sqlite-vec` extension through `vec_distance_cosine(...)` in
`src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:138,157`. Outside
`EmbeddingMath.MeanPoolAndNormalize` there is no large float-array loop in `src/` to
vectorize. There is exactly one managed float kernel worth vectorizing, and this plan
vectorizes it.

## Scope

**In scope**

- Zero-fill correctness in the ONNX batch input buffers, with a test that has been seen red.
- The `--bench` argument gate in `benchmarks/AiRaccoon.Benchmarks/Program.cs`.
- A real `EmbeddingMathBenchmark` that reports scalar-vs-vectorized as a baseline/candidate
  ratio in one run.
- `TensorPrimitives` vectorization of `EmbeddingMath.MeanPoolAndNormalize`, plus the ADR the
  clean-layering invariant requires for the new `AiRaccoon.Core` dependency.

**Out of scope** (findings reported at the end, not worked here)

- `SnippetFallback.From` allocation churn; RRF / source-affinity / structure-fusion LINQ churn;
  `EmbeddingBlob.ToBytes`; the dead `EmbeddingBlob.ToFloats`; any benchmark of
  `OnnxEmbeddingGenerator` itself; wiring an ArchUnitNET domain-purity test.

---

## Decisions

### D1 — Sequencing: the correctness fix ships as its own PR, first

**Recommendation: separate PR, merged before the performance PR opens.**

The buffer bug is not an optimization and does not belong in a performance change.
Three reasons, in order of weight:

- **Rollback scope.** The vectorized kernel changes float results (different summation
  order). If a retrieval band moves and the perf PR has to be reverted, a bundled
  correctness fix goes back out with it and the memory bank resumes writing wrong
  embeddings. Those two changes must be independently revertable.
- **"One PR per task."** A bug repair and a performance improvement are two tasks. The
  invariant is explicit, and nothing here qualifies for its owner-instruction exception.
- **Review economics.** The fix is small, obviously correct, and reviewable in minutes.
  Behind a `TensorPrimitives` diff it becomes invisible.

So: **PR-1 = WP-0** (fix + test). Merge. Rebase the worktree branch. **PR-2 = WP-1…WP-5**
(benchmark + kernel + ADR). Both open as draft PRs from their first commit, per the
small-commits invariant.

Tell the owner this explicitly — they named `OnnxEmbeddingGenerator.cs` as "the first
optimization" and may be expecting one PR. The answer is that the file contains a
regression rather than an optimization opportunity, and the split is what makes each
half provable.

### D2 — The domain-layer dependency: add `System.Numerics.Tensors` to `AiRaccoon.Core`, recorded as ADR 0017

`AiRaccoon.Core` is the clean domain layer by shape: other projects reference it, it
references none of them, and its only packages are `FluentValidation` and
`CommunityToolkit.Diagnostics`. `System.Numerics.Tensors` ships stable in .NET 10 but is
**not in-box** — it requires a `PackageReference`. It is present in
`AiRaccoon.Benchmarks`'s transitive graph at 10.0.10 (via `Microsoft.Extensions.AI`) and is
already used at `benchmarks/AiRaccoon.Benchmarks/Embedders/EmbeddingBackend.cs:53`, but
`CentralPackageTransitivePinningEnabled` is `false`, so there is no `PackageVersion` entry
and Core will not resolve it without one.

Three options were considered.

| Option | Verdict |
|---|---|
| **(a) Add the package to `AiRaccoon.Core`, keep the kernel in the domain** | **Recommended** |
| (b) Move `MeanPoolAndNormalize` to `AiRaccoon.Infrastructure`, leave Core scalar or empty | Rejected |
| (c) Hand-roll `Vector<T>` in Core to avoid the package | Rejected |

**Why (a).** Mean-pool + L2-normalize is domain math: it defines what an AiRaccoon
embedding *is* (FR-NM-3, sentence-transformers semantics), which is why it lives in Core
today. `System.Numerics.Tensors` is pure computation — no I/O, no HTTP, no persistence, no
serialization — so it is not in the class the clean-layering rule forbids (ASP.NET Core, EF
Core, Azure SDKs, `System.Net.Http`, transport/serialization namespaces). The precedent is
exact: ADR 0001 added `FluentValidation` to Core and closes with "AiRaccoon.Core gains one
third-party dependency (pure logic, no I/O); the clean-layering rule requires this ADR for
that change." This is the same shape of decision and gets the same treatment.

**Why not (b).** It works mechanically — `OnnxEmbeddingGenerator` is the *only* production
caller of `EmbeddingMath` (verified: `EmbeddingMath.` appears in `src/` only at
`OnnxEmbeddingGenerator.cs:89-91`). But it relocates domain math out of the domain to dodge
a numerics package, which is the clean-layering rule's "don't extend the boundary to fit the
SDK" read backwards. It also *costs* rather than saves: `AiRaccoon.Benchmarks` references
only `AiRaccoon.Core`, so the benchmark would then need an Infrastructure project reference
plus an `InternalsVisibleTo`. Keep (b) as the fallback if the owner refuses a new Core
dependency; nothing else in the plan changes except the file paths in WP-4.

**Why not (c).** The .NET SIMD guidance explicitly prefers the already-accelerated
`TensorPrimitives` over hand-rolled `Vector<T>`/`Vector128<T>` for element-wise float span
math. Hand-rolling means owning tail handling and platform vector width for a kernel of
about fifteen lines. "Ask if a simpler shape would do" points away from it.

**The record required:** a new ADR, `docs/adr/0017-tensorprimitives-in-core.md`. Existing
ADRs run 0001–0016, so 0017 is next. Match the house shape used by 0016: `Context` /
`Decision` / `Consequences` / `Alternatives rejected` / `Evidence`, with `Date:` and
`Status: Accepted` under the title. Add the row to the table in `docs/adr/README.md`. The
`Evidence` section is where the benchmark table goes.

Note the ADR must state plainly that the clean-layering invariant's ArchUnitNET enforcement
is still not wired in this repo, so nothing mechanically prevents the next such dependency.
Wiring it is a separate task; see the reported findings.

### D3 — Benchmark scope

**What `EmbeddingMathBenchmark` measures.** One class holding both implementations, so a
single BenchmarkDotNet run prints a real `Ratio` column against a `Baseline`. Do **not**
compare two git commits: cross-run comparison on a laptop is noise, and BDN's own
baseline machinery exists for exactly this.

To hold both side by side, the benchmark project keeps a **private frozen copy** of the
current scalar body as `MeanPoolAndNormalizeScalar`, and calls the production
`EmbeddingMath.MeanPoolAndNormalize` as the candidate. The alternative — keeping a
now-unused scalar method alive in production Core purely so a benchmark can call it — is a
cost with no buyer and was rejected. The copy is a speed reference, not a correctness
oracle: correctness is gated by the unit tests in WP-4, so if the copy ever drifts the worst
outcome is a stale ratio, disclosed in a one-line comment above it.

**Parameters.**

- `[Params(16, 64, 256)] public int SeqLen` — 256 is `MaxSequenceLength` and the realistic
  hot case, because `RunBatch` pads every row in a batch to `maxLen`; one long text drags
  the whole batch to 256. 64 is a typical chunk, 16 a short entry.
- `[Params(1.0, 0.5, 0.0)] public double MaskDensity` — 1.0 is the dense case (equal-length
  batch, and always true of the row that set `maxLen`); 0.5 is the mixed-length batch where
  half the rows are skipped padding; 0.0 hits the `active == 0` early return. Note honestly
  in the class doc comment that 0.0 is a public-API edge, not a reachable production state —
  every tokenized item has at least `[CLS] [SEP]`, so a real mask row is never all zeros.
- Mask layout is **active tokens first, then padding**, matching how `RunBatch` lays it out.
  No interleaving, no RNG in the mask.
- **`Dim` is not a parameter.** Fix it at `EmbeddingMath.Dimension` (384). The model is
  pinned; a dimension sweep produces numbers nobody can act on.
- Hidden state filled in `[GlobalSetup]` from a fixed seed (`new Random(20260808)`) over
  `[-1, 1)`, so before/after runs are comparable.

**Also fix in the same file:** the `Hiidden` typo, and the public auto-properties that
nothing populates — state becomes private fields assigned in `[GlobalSetup]`, following
`benchmarks/AiRaccoon.Benchmarks/Benchmarks/EmbeddingLatencyBenchmark.cs` as the in-repo
template. `[Baseline = true]` moves onto the scalar method, where it means something; on a
single-method class it means nothing.

`[MemoryDiagnoser]` stays, but be clear about what it can show: `MeanPoolAndNormalize`
returns the `float[]` it allocates, and that array escapes into `Embedding<float>`. There is
no pooling opportunity here — the allocation is the return value. Expect a flat ~1,560 B per
op before and after. This benchmark measures time, not allocation.

**Ruling on F4 — do not benchmark `OnnxEmbeddingGenerator`.** Adding an
`AiRaccoon.Infrastructure` project reference plus an `InternalsVisibleTo` for
`AiRaccoon.Benchmarks` (Infrastructure grants it only to `AiRaccoon.Tests` today) buys a
number that cannot be read: ONNX session inference for a 256×384 BERT forward pass is
milliseconds, while the buffer fill is a memset and a copy over `batch × maxLen` longs —
64 KB for a batch of 32 — which is sub-microsecond. The micro-benchmark would measure the
model. Widening an assembly's internals visibility permanently, for a number nobody will act
on, fails "ask if a simpler shape would do". The buffer change in WP-0 is a **correctness**
fix and its gate is a test, not a number. If the owner wants end-to-end evidence that
pooling is not a regression, the instrument already exists: `EmbeddingLatencyBenchmark` and
the retrieval harness. This is the honest answer to "benchmarks for all changes if possible"
— for this change it is not possible in a meaningful way, and a meaningless number is worse
than none.

### D4 — Scope discipline on the allocation findings

The owner asked for an exploration. The exploration's result is that there is almost nothing
to vectorize and the remaining opportunities are small, sit in the retrieval pipeline rather
than the embedding pipeline, and each carry their own gates. **None of them enter this
task.** Manufacturing work to fill out the request is exactly what the invariants forbid.
Ruling per item:

- **`SnippetFallback.From`** (`src/AiRaccoon.Infrastructure/Sqlite/SnippetFallback.cs:14-45`),
  two allocations plus a SHA-256 per result row, called unconditionally from
  `SqliteMemoryStore.cs:727` on every dual-vector search. This is the strongest of the
  four — but the interesting word is *unconditionally*. The fix is not SIMD or pooling, it
  is not doing the work when the fallback is not needed, which is a design change in the
  retrieval pipeline with ADR-0015 band gates attached. **Separate task.**
- **`ReciprocalRankFusion.Fuse`, `SourceAffinityRanker.Rank/Consolidate`,
  `StructureFusion.Rank`.** Dictionary/list plus LINQ `OrderByDescending` churn per search,
  N in the low hundreds. Ranking-correctness-sensitive and gated by the same band tests.
  Bundling them into an embedding-math PR is the bundling the invariant names.
  **Separate task.**
- **`EmbeddingBlob.ToBytes`** (`src/AiRaccoon.Infrastructure/Embedding/EmbeddingBlob.cs:8-30`).
  **Do not change.** A `MemoryMarshal` cast is shorter and faster, but the current
  `BinaryPrimitives.WriteSingleLittleEndian` loop is explicitly little-endian and the blob is
  *persisted and synced to cloud*. Trading a documented byte-order invariant for a micro-win
  on a once-per-write path is a bad trade. Recorded here so nobody "optimizes" it later.
- **`EmbeddingBlob.ToFloats`** — dead, zero callers. A deletion, not a performance change.
  **Separate one-line follow-up** for the owner to approve.
- **`SweepService`, `MarkdownChunker`, `OnnxEmbeddingGenerator.Encode`** — I/O-bound or cold.
  Not worth optimizing.

---

## Work packages

Gate commands are run from the worktree root:
`/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/perf-optimization`.
BenchmarkDotNet writes to `BenchmarkDotNet.Artifacts/`, which `.gitignore` already excludes —
run from the root so the artifacts land there, and paste tables into documents rather than
committing the artifact tree.

### WP-0 — Zero-fill the ONNX batch input buffers (PR-1, ships alone)

**Scope**

- `src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs`
- `src/AiRaccoon.Infrastructure/Embedding/TokenBatch.cs` (new)
- `tests/AiRaccoon.Tests/Unit/Embedding/TokenBatchTests.cs` (new)

**Shape.** Extract the batch fill out of `RunBatch` into a named internal static type in the
same domain folder:

- `internal readonly record struct TokenizedText(int[] Ids)` — the per-item token ids.
- `internal static void Fill(ReadOnlySpan<TokenizedText> items, int maxLen, Span<long> inputIds, Span<long> attentionMask, Span<long> tokenTypeIds)`
  writes **every** element of all three spans: real ids where `s < Ids.Length`, `0` in the
  padding tail; `1` in the attention mask where `s < Ids.Length`, `0` in the tail; `0`
  throughout `tokenTypeIds` (all-MiniLM-L6-v2 is single-segment, so all-zero token types is
  exactly what the pre-`ac0c4d6` `new long[...]` produced — this fix restores the old
  behavior bit for bit under a clean pool, so no golden file moves).

Two things drop out of the extraction and should go with it:

- `Item.Mask` becomes redundant. `Encode` currently allocates `new int[ids.Count]` and
  `Array.Fill(mask, 1)` per text; the mask is fully derivable from `Ids.Length`. Delete it —
  "derive the list, or delete it" applies literally, and it removes an allocation per text.
- Leave `maskRow` in `RunBatch` alone. It is a single `new int[maxLen]` reused across the
  batch and fully overwritten each iteration, so it is not affected by this bug.

**Why extract rather than add three `.Clear()` calls.** The three-line version
(`inputIds.Span.Clear()` and friends) is simpler and equally correct — but it is only
reachable through `RunBatch`, which needs a live ONNX session, so it cannot be put in front
of a red test. "Done means proven" wins over the shorter diff. The extracted type is small,
internal, named for a domain concept, and lives next to its only caller.

**Making the check go RED first.** Write `TokenBatchTests` before touching production code.
The pool is not needed to produce the red — a deliberately dirtied span stands in for a
dirty rental and is deterministic, whereas real `ArrayPool<T>.Shared` recycling depends on
per-thread and per-core stash internals:

```
const int maxLen = 4;
items = [ new TokenizedText([101, 7592, 102]) ];   // 3 real tokens, 1 padding slot
ids/mask/types = new long[4] each, then .AsSpan().Fill(-1)   // stands in for a dirty rental
TokenBatch.Fill(items, maxLen, ids, mask, types);
assert ids[3] == 0 && mask[3] == 0 && types all == 0
```

**What the red looks like:** against the current `continue`-past-padding logic the first
assertion fails with `-1 should be 0` at `ids[3]`, and `types` remains all `-1`. It fails
for the right reason — the buffer was not fully defined. After the fix it passes.

**Explicitly rejected as a second test:** a "rent, dirty, return, rent again" test against
`ArrayPool<long>.Shared`. Whether the second rent returns the same array is an
implementation detail; if it returns a fresh zeroed array the test passes vacuously. A check
that can pass without ever having been able to fail is not a check.

**Optional behavior anchor.** A batch-vs-single equivalence test (embed `"hello"` alone,
embed it in a batch alongside a ~200-token text, assert agreement within 1e-5),
`Speed=Slow`, needs the bundled model. First check whether
`tests/AiRaccoon.Tests/Integration/EmbeddingFeatureTests.cs` already covers this; if it does,
skip. Label it honestly in the PR: it is green today on a clean pool, so it is an anchor
against the class of bug, not the red.

**Acceptance criteria**

1. `TokenBatch.Fill` writes every element of all three output spans for any `maxLen` and any
   item lengths, including `Ids.Length > maxLen` (truncation already happens in `Encode`;
   assert `Fill` does not overrun) and `Ids.Length == 0`.
2. `TokenBatchTests` was observed red before the fix and green after.
3. `Item.Mask` and the per-text `Array.Fill` are gone.
4. No embedding output changes under a clean pool — the retrieval and golden gates are
   unmoved.

**Gates**

```
dotnet build --nologo
dotnet test --filter "FullyQualifiedName~TokenBatchTests" --no-build --nologo -v m
dotnet test --filter "Speed=Fast" --no-build --nologo -v m
dotnet test --filter "Speed=Slow" --no-build --nologo -v m
```

The `Speed=Slow` run is what proves criterion 4 — it carries `GoldenFileTests` and the
retrieval baseline tests.

---

### WP-1 — Fix the `--bench` argument gate

**Scope:** `benchmarks/AiRaccoon.Benchmarks/Program.cs` only.

`BenchmarkArg` is `"--bnech"` but line 19 strips the literal `"--bench"` before handing args
to `BenchmarkSwitcher`, so the gate word and the stripped word differ and the benchmark path
is unreachable as documented. Fix by making one constant serve both sites: set
`BenchmarkArg = "--bench"` and filter with `a != BenchmarkArg`. One source for the word, so
the two cannot disagree again.

**Acceptance criteria**

1. `--bench` enters the BenchmarkDotNet switcher; absence of it still runs the retrieval
   quality comparison.
2. `--bench` is not forwarded to the switcher (it is not a BDN argument).
3. Remaining arguments (`--filter`, `--list`) reach the switcher intact.

**Gate**

```
dotnet run -c Release --project benchmarks/AiRaccoon.Benchmarks -- --bench --list flat
```

**The red:** run that exact command before the change. It prints the
"ai-raccoon retrieval benchmark — embedding model comparison" table (or fails on a missing
LM Studio / GGUF backend) instead of listing benchmark ids. After the change it prints the
fully-qualified benchmark names. Watch both.

---

### WP-2 — Make `EmbeddingMathBenchmark` measure something

**Scope:** `benchmarks/AiRaccoon.Benchmarks/Benchmarks/EmbeddingMathBenchmark.cs` only.

Build the class per D3, with the current scalar production implementation as the only
benchmark method for now (`Baseline = true`), plus `[GlobalSetup]`, the `SeqLen` and
`MaskDensity` params, the fixed 384 dimension, and the seeded hidden state. Fix `Hiidden`.

**Acceptance criteria**

1. The BDN summary has nine rows (3 `SeqLen` × 3 `MaskDensity`), each with a non-trivial
   `Mean`.
2. `Mean` scales roughly linearly with `SeqLen × MaskDensity`; the `MaskDensity = 0` rows are
   the floor (early return).
3. `Allocated` is ~1,560 B for every row with `MaskDensity > 0` — one `float[384]`.
4. The run is reproducible: a second run reproduces each `Mean` within its own reported
   error bars.
5. The summary table, **including BDN's host/runtime header**, is captured for the PR. A
   number without the host it was measured on is not a measurement.

**Gate**

```
dotnet run -c Release --project benchmarks/AiRaccoon.Benchmarks -- --bench --filter '*EmbeddingMathBenchmark*'
```

**The red:** run that command against the file as it stands today (after WP-1 makes it
runnable). `Hiidden`, `Mask`, `SeqLen` and `Dim` are never populated, so it reports a single
row over empty spans with `SeqLen = 0` and `Dim = 0` — a few nanoseconds, no `SeqLen` or
`MaskDensity` columns, and no meaningful allocation. That single degenerate row *is* the
observed failure of the acceptance criteria above. Record it in the PR next to the fixed
run; it is the clearest possible demonstration that the stub measured nothing.

---

### WP-3 — Land the dependency and the ADR

**Scope**

- `Directory.Packages.props` — add
  `<PackageVersion Include="System.Numerics.Tensors" Version="10.0.10"/>` to the Application
  group. 10.0.10 matches the version already resolving transitively in the benchmarks graph
  via `Microsoft.Extensions.AI` 10.8.3, so no version split is introduced. Follow the file's
  convention of a comment when the pin carries a reason.
- `src/AiRaccoon.Core/AiRaccoon.Core.csproj` — add the `PackageReference`.
- `docs/adr/0017-tensorprimitives-in-core.md` (new) — per D2.
- `docs/adr/README.md` — add the 0017 row to the contents table.

**Acceptance criteria**

1. Solution builds; `AiRaccoon.Core`'s only new dependency is `System.Numerics.Tensors`, and
   no other project's resolved graph changes version.
2. ADR 0017 exists with `Context` / `Decision` / `Consequences` / `Alternatives rejected` /
   `Evidence`, states the clean-layering rule required it, names options (b) and (c) from D2
   as the rejected alternatives, and notes that no ArchUnitNET purity test enforces the
   boundary in this repo yet.
3. `docs/adr/README.md` lists 0017.
4. `Evidence` is left with a placeholder to be filled by WP-5 — the ADR is only accepted once
   it carries the measurement that justified it.

**Gate**

```
dotnet build --nologo
dotnet list src/AiRaccoon.Core/AiRaccoon.Core.csproj package --include-transitive
```

Read the second command's output and confirm the graph gained one package, not a subtree.
There is no automated check for ADR presence; the gate for criteria 2–3 is the PR review,
which is the record the clean-layering invariant asks for.

---

### WP-4 — Vectorize `EmbeddingMath.MeanPoolAndNormalize`

**Scope**

- `src/AiRaccoon.Core/Embedding/EmbeddingMath.cs`
- `tests/AiRaccoon.Tests/Unit/Embedding/EmbeddingMathTests.cs` (add one test)
- `benchmarks/AiRaccoon.Benchmarks/Benchmarks/EmbeddingMathBenchmark.cs` (add the candidate
  method and the frozen scalar copy)

**Shape.** Replace the three scalar loops, keeping both early returns exactly as they are:

- accumulate: per active row, `TensorPrimitives.Add(pooled, hidden.Slice(s * dim, dim), pooled)`;
- mean: `TensorPrimitives.Divide(pooled, (float)active, pooled)`;
- norm: `TensorPrimitives.Norm(pooled)` — this replaces the current double-accumulated
  `lengthSquared` and `MathF.Sqrt`;
- normalize: `TensorPrimitives.Divide(pooled, norm, pooled)`.

Keep the `active == 0` and `norm <= 0` guards. Do **not** fuse the two divides into a single
`Divide(pooled, active * norm, pooled)` pass on the first attempt — it is one traversal
instead of two, but it changes rounding; try it only after the two-pass version is green, and
only if the tests stay green.

**Precision is the real risk, and it is an acceptance criterion, not a footnote.** The
current code accumulates the squared length in `double`; `TensorPrimitives.Norm` works in
`float`. Vectorized summation also reassociates the row accumulation. The existing
`EmbeddingMathTests` assert at 1e-6 and must pass **unchanged** — loosening a tolerance to
make the optimization fit is not permitted without the owner ruling on it explicitly.

**TDD note, stated honestly.** A pure optimization has no new behavior, so there is no
natural failing test that demands it. The test that demands the change is the benchmark
(WP-2); the tests that constrain it are the behavior tests. The invariant is satisfied by
doing both halves and watching both:

1. **Write first, watch it pass against the scalar code:** a new characterization test
   `MeanPoolAndNormalize_AtProductionDimension_MatchesNaiveReference` — `dim = 384`,
   `seqLen = 256`, mixed mask (active-first, ~60% density), reference sum computed in
   `double` inside the test, agreement asserted at 1e-5. This matters because every existing
   test uses `dim = 3`, which is entirely vector *tail* — nothing in the suite currently
   exercises a full 256-bit lane, so a tail-handling or lane bug would ship green today.
2. **After the vectorized version is green, prove the check can fail.** Two deliberate
   breaks, each observed red, each reverted:
   - accumulate over `row[..(dim - 1)]` so the last element of every row is dropped — the
     new dim-384 test must go red while the dim-3 tests may well stay green, which is the
     whole point;
   - drop the mask check so every row accumulates — the existing
     `WithSingleActiveToken` test must go red.

   Record both observations in the PR. "I could not make it fail" is not the same claim as
   "it cannot fail."

**Acceptance criteria**

1. All four existing `EmbeddingMathTests` pass with tolerances unchanged.
2. The new dim-384 characterization test passes, and was observed red under both deliberate
   breaks above.
3. `Speed=Slow` passes — golden files and retrieval baselines are the gate on whether the
   changed float results moved a ranking. ADR 0015 set those to portable bands with a 5e-3
   ranking tolerance, so small float movement is tolerated by design; a failure here means
   the movement was not small.
4. The benchmark reports the candidate against the scalar baseline with a `Ratio` column at
   every `SeqLen`/`MaskDensity` combination.
5. If the measured ratio is not a clear improvement at `SeqLen = 256, MaskDensity = 1.0`,
   **the change does not ship.** Report the number and revert the kernel; WP-3's ADR is
   withdrawn with it. An optimization that does not measure faster is a dependency with no
   buyer.

**Gates**

```
dotnet build --nologo
dotnet test --filter "FullyQualifiedName~EmbeddingMathTests" --no-build --nologo -v m
dotnet test --filter "Speed=Fast" --no-build --nologo -v m
dotnet test --filter "Speed=Slow" --no-build --nologo -v m
dotnet run -c Release --project benchmarks/AiRaccoon.Benchmarks -- --bench --filter '*EmbeddingMathBenchmark*'
```

---

### WP-5 — Record the results and the negative finding

**Scope**

- `docs/adr/0017-tensorprimitives-in-core.md` — fill `Evidence` with the WP-4 summary table
  including the host header.
- This plan file — append a `Results` section with the same table and the WP-2 before/after
  contrast.
- PR description — the negative SIMD result, and the reported findings below.

**Acceptance criteria**

1. Every performance claim in the PR points at a table with a host header; no unmeasured
   figure is stated as if measured.
2. The exploration's negative result is written down, not omitted: corpus-scale distance math
   is native (`MemorySql.cs:138,157`), and `MeanPoolAndNormalize` was the only managed float
   kernel worth vectorizing.
3. The out-of-scope findings below are handed to the owner as candidate tasks.

**Gate:** owner review of the PR. This package produces documents; its gate is a reader.

---

## Ordering and parallelism

```
PR-1:  WP-0                                   (alone; merge, then rebase)
                    |
PR-2:  WP-1 ──► WP-2 ──► WP-4 ──► WP-5
       WP-3 ──────────────┘
```

| Package | Runs with | Serializes because |
|---|---|---|
| WP-0 | — | Own PR (D1). |
| WP-1 | **parallel with WP-3** | Disjoint files (`Program.cs` vs `Directory.Packages.props` / `Core.csproj` / `docs/adr/`). |
| WP-3 | **parallel with WP-1, WP-2** | Disjoint files. |
| WP-2 | after WP-1 | Needs a runnable `--bench`; also owns `EmbeddingMathBenchmark.cs`. |
| WP-4 | after WP-2 **and** WP-3 | Shares `EmbeddingMathBenchmark.cs` with WP-2; needs WP-3's package to compile. |
| WP-5 | after WP-4 | Consumes WP-4's numbers; shares the ADR file with WP-3. |

No two packages marked parallel touch the same file.

---

## Risks

- **The kernel may not measure faster.** `MeanPoolAndNormalize` is `seqLen × dim` float adds
  — 98,304 at the 256×384 worst case — which is real work, but the JIT already
  auto-vectorizes simple accumulation loops in some shapes. WP-4 criterion 5 exists so this
  ends in a reverted branch and a recorded number rather than a shipped dependency.
- **End-to-end impact is likely invisible.** Even a large win on the kernel sits behind an
  ONNX forward pass measured in milliseconds. The PR must not claim an end-to-end embedding
  speedup unless `EmbeddingLatencyBenchmark` shows one.
- **Float movement in the retrieval gates.** Handled by `Speed=Slow` and ADR 0015's bands; a
  failure there is a stop, not a tolerance negotiation.
- **Concurrent sessions on this repo.** `main` moves under long tasks. Both PRs are built in
  this worktree and rebased before push.

---

## Findings reported, not worked here

1. **`SnippetFallback.From` runs unconditionally on every search result row** —
   `src/AiRaccoon.Infrastructure/Sqlite/SnippetFallback.cs:14-45`, called from
   `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:727`. Two allocations plus a
   SHA-256 per row on every semantic search. The fix is laziness, not vectorization.
   Retrieval-pipeline task with band gates.
2. **Ranking-stage LINQ and dictionary churn** — `ReciprocalRankFusion.Fuse`,
   `SourceAffinityRanker.Rank/Consolidate`, `StructureFusion.Rank`. Low-hundreds N per
   search. Same task family as (1).
3. **`EmbeddingBlob.ToFloats` is dead** —
   `src/AiRaccoon.Infrastructure/Embedding/EmbeddingBlob.cs`, zero callers. One-line deletion,
   owner approval.
4. **Do not "optimize" `EmbeddingBlob.ToBytes`** — its per-element
   `BinaryPrimitives.WriteSingleLittleEndian` loop is deliberately endian-explicit for a
   persisted, cloud-synced blob. Recorded so the next reader does not swap in
   `MemoryMarshal`.
5. **The clean-layering invariant has no teeth here.** CLAUDE.md asks for an ArchUnitNET
   domain-purity test; none exists in `tests/AiRaccoon.Tests`. WP-3 adds a Core dependency
   through a review gate only. Wiring the test is its own task, and the right moment to do it
   is now that a second package has crossed into Core.
