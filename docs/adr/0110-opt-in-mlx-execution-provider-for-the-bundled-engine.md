# 0110 — An opt-in MLX execution provider for the bundled engine, osx-arm64 only

Date: 2026-09-24

Status: Proposed

Research: `docs/work/2026-09-24-onnx-runtime-providers-gpu-mlx.md`

## Context

ADR-0108 put the bundled engine (granite-embedding-small-english-r2, fp16) on WebGPU on macOS,
with spinning disabled by #712 (F25/F26). The stock ORT 1.30.0 build has no MLX provider at all —
`AppendExecutionProvider("MLX", …)` throws (F5). An out-of-tree plugin EP,
`onnxruntime-ep-mlx`, exists and loads into the shipped ORT 1.30.0 build unmodified, including the
published PyPI wheel built for the ORT 1.29 C ABI (F7, F33): registering it with
`OrtEnv.RegisterExecutionProviderLibrary` exposes one MLX device, and a session built from that
device runs on Apple's MLX array framework instead of WebGPU or the CPU.

Run as exported, the plugin is a regression, not an improvement. Its `MultiHeadAttention` claim
refuses any node with an `attention_bias` input (F15), and this model feeds its global and local
attention masks through exactly that input on every one of its 12 layers (F16). The plugin still
claims 96.8% of the graph, but every attention op — the expensive part — falls back to the CPU in
fp32, with `Cast` nodes and CPU hops between islands (F9). Measured against the shipped WebGPU
path, that shape is about 3.4x slower and burns roughly 30x the CPU per embed at 128 tokens (F10).

Rewriting the 12 `MultiHeadAttention` nodes into standard ops the plugin's `Attention` lowering
already reads a mask for — Reshape/Transpose/MatMul/Mul(scale)/Add(bias)/Softmax/MatMul — moves
every attention op onto MLX, at cosine 1.000000 against the original CPU output for the rewrite
alone (F17). Folding the graph's one runtime-bounds `Range` into `CumSum` closes the last gap: the
plugin then claims all 547 nodes in a single fused subgraph, and ORT places nothing on the CPU
(F27). With WebGPU's spinning already off (F25), the fully-fused MLX graph is about 10-20% faster
in latency and cuts the remaining CPU by roughly half (F26). Every quality eval run against it —
the memory and code retrieval evals (F28), the bank title→document eval (F36), and the product's
own hybrid-search harness on 332 code queries (F38-F42) — holds within noise of both the CPU
reference and the shipped WebGPU path, and MLX's per-query drift from the CPU reference is smaller
than WebGPU's on every one of those evals.

Shipping the plugin costs package size and a hard platform floor. The Rust-built dylib, the MLX
runtime libraries (`libmlx.dylib`, `libmlxc.dylib`) and `mlx.metallib` add about 43 MB compressed to
the osx-arm64 package (F23) — comfortably inside nuget.org's ~250 MB limit (F24), and there is
headroom after #714's weights-once fix took that package from 180.6 MB to 104.2 MB (F44). MLX
itself only runs on Apple Silicon, and the standard NuGet package only ships one native build per
RID, so this can only ever be an osx-arm64 story.

## Decision

**`embedding.device` gains a fourth value, `mlx`, for the bundled engine only.** `EmbeddingDevice`
adds `Mlx`; `EmbeddingDeviceSetting.PrefersMlx(device, isBundledEngine)` is true only when both
hold — a custom downloaded model has no rewritten graph, so `device mlx` is inert for it rather
than guessing. `PrefersGpu` also returns true for `Mlx` on the bundled engine, so a refused MLX
attempt still falls through to the existing WebGPU-then-CPU chain instead of landing straight on
the CPU. `auto` is unchanged: it still means WebGPU on macOS for the bundled engine, CPU
everywhere else. Nothing about MLX runs unless an operator explicitly asks for it.

**The rewritten graph ships as a second, small ONNX file next to the original.**
`scripts/src/make_mlx_graph.py` performs the MultiHeadAttention rewrite and the Range fold (F17,
F27) and is committed, so the graph is reproducible from `model_fp16.onnx`. Its output,
`model_fp16_mlx.onnx` (219 KB), is committed beside `model_fp16.onnx` and references the same,
unchanged `model_fp16.onnx_data` — the 97 MB weights file stays git-ignored and fetched at build
time exactly as ADR-0108 left it. The graph's SHA-256 is pinned in `scripts/src/bundle.py`
(`BUNDLED_MLX_GRAPH`) next to the model's other pins, verified by
`scripts/download-embedding-model.py` and by `scripts/src/package_verify.py`'s packed-nupkg check,
the same "committed, never fetched" contract `BUNDLED_MANIFEST` already has.

**The MLX runtime binaries are fetched, sha256-verified, and never committed.**
`scripts/download-mlx-runtime.py` downloads the pinned `onnxruntime-ep-mlx` wheel
(0.29.6, PyPI, MIT, sha256-pinned) and unpacks its four files —
`libonnxruntime_mlx_ep.dylib`, `libmlx.dylib`, `libmlxc.dylib`, `mlx.metallib` — flat into the
git-ignored `src/AiRaccoon/mlx-runtime/`, because the wheel's own libraries resolve each other by
`@loader_path` and must stay siblings (F7). `AiRaccoon.csproj` picks these up in one
`Condition="'$(RuntimeIdentifier)' == 'osx-arm64'"` item group with `CopyToOutputDirectory` and no
`Pack`/`PackagePath` — the same shape F30 proved lands files only under that RID's own
`tools/net10.0/osx-arm64/mlx/`, absent from every other RID package and from the RID-agnostic
package. A `THIRD_PARTY_NOTICES.md` (both MIT licenses, onnxruntime-ep-mlx and MLX) ships in the
same `mlx/` folder. A pack-time guard, `RequireMlxRuntimeFiles`, fails an osx-arm64 pack outright
if the runtime files are missing — the same shape ADR-0108's `RequireBundledModelWeights` already
uses for the model weights.

**`OnnxEmbeddingGenerator` tries MLX first, then falls through to the existing GPU-then-CPU
chain.** When `preferMlx` is set, it checks — in order — the platform (macOS on Apple Silicon),
the plugin files under `AppContext.BaseDirectory/mlx`, and the rewritten graph beside the model. On
any refusal it records the reason and continues into the unchanged WebGPU-then-CPU path, appending
`"(MLX refused: …)"` to whatever `ExecutionProvider` that path lands on — the same pattern
`"CPU (GPU refused: …)"` already uses. Registration
(`OrtEnv.RegisterExecutionProviderLibrary("MLXExecutionProvider", …)`) is guarded by a static
once-per-process flag, since registering the same name twice throws. The MLX session applies the
same `session.intra_op.allow_spinning=0` entry the GPU session already applies (#712).

**MLX inference is thread-affine, so every call into an MLX session is pinned to one dedicated
thread.** Measured directly: a session that first ran on one OS thread throws
("MLX eval is thread-affine — use one InferenceSession per thread for concurrent inference") the
moment `Run()` is called from another, and depending on the code path that can surface as a native
abort rather than a catchable exception. `SingleThreadExecutor` is a small, independently
unit-tested class — one dedicated background thread and a work queue — that every MLX-session call
this generator makes (construction, every `Run`, `Dispose`) is marshalled through. Serialization
falls out of this for free: one thread cannot run two calls at once, which is also what "thread-safety
of the plugin is unverified" asked for. WebGPU's own process-wide `GpuGate` lock is untouched and
still gates only WebGPU sessions.

**The engine fingerprint does not change.** MLX vectors match the CPU reference at cosine
0.999999-0.9999993 (F17, F27, F33) — inside the same parity bar WebGPU already meets (F11) — so no
bank re-embeds from switching `embedding.device` to `mlx` and back.

## Consequences

- Default behavior is unchanged: `auto` still means WebGPU on macOS, CPU elsewhere.
- The osx-arm64 package grows by about 43 MB compressed only when someone runs
  `scripts/download-mlx-runtime.py` before packing — a build that skips it packs exactly as it did
  before this ADR, since the plugin files and their pack-time guard are both conditioned on
  `RuntimeIdentifier == osx-arm64`.
- Doctor and the "Embedding session created" log show `execution provider MLX` once the setting is
  active and every check passes; nothing else about that log line's shape changed.
- Retrieval quality is unaffected: the memory, code, and bank evals (F28, F36, F38-F42) all hold
  within noise across CPU, WebGPU, and MLX-C.
- Adopting this in a real release is the project owner's call — this ADR and its PR ship as
  opt-in and Proposed specifically so that decision can be made separately from landing the code.

## Evidence

`docs/work/2026-09-24-onnx-runtime-providers-gpu-mlx.md`: F5 (stock ORT refuses "MLX"), F7/F33
(the plugin loads into ORT 1.30.0, wheel and locally-built alike), F9/F15/F16 (why the plugin
leaves attention on the CPU as exported), F17/F27 (the rewrite and the Range fold reach one fused
subgraph), F10/F18/F26 (latency and CPU measurements across the three graph variants), F23/F24/F44
(package size), F25 (WebGPU spinning), F28/F36 (retrieval-quality parity), F30 (osx-arm64-only
packaging, proven in a scratch tool project), F38-F42 (the product's own hybrid-search harness
holds MLX-C within noise of CPU, closer than shipped WebGPU).

## Related decisions

- [ADR-0108 — One bundled engine for memory and code: granite-embedding-small-english-r2 (fp16),
  GPU first](0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md): this ADR extends its
  `embedding.device` setting and its GPU-then-CPU session-selection shape with a third path tried
  first, opt-in only.
