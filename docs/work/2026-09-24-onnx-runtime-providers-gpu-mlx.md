# Research: ONNX Runtime execution providers, GPU use and MLX support in AiRaccoon

**Date:** 2026-09-24
**Question:** Which ONNX Runtime execution providers can AiRaccoon's embedding sessions use on macOS, is the live server really running on the GPU when it logs "execution provider WebGPU", and can it use MLX — and if so, how does MLX compare with WebGPU on the bundled engine?
**Follow-up (same day):** Why can't the MLX plugin keep attention on the GPU for this model, would an export that passes the mask differently fix that, and is WebGPU's 385-of-404 placement the best available?
**Second follow-up (same day):** Does the osx-arm64 package stay under nuget.org's limit with MLX, does folding the last `Range` give one fused MLX graph, do search results hold on a real corpus, where does WebGPU's CPU time go, and can MLX ship in the osx-arm64 package only?
**Third follow-up (same day):** Do the bank title→document eval and the product's hybrid search hold per provider, and does a fully source-built MLX stack behave the same?

Latency and CPU charts show per-round p50 values from three round-robin rounds, 30 runs each: `low..median..high` across the rounds. Machine: Apple M4, 10 cores, load average 25–55 from other sessions' work.

```chart:range
title: latency p50 ms, 128 tokens (granite-small fp16, ORT 1.30.0)
CPU: 30.9..173.7..243.5
WebGPU: 9.2..9.7..26.3
MLX plugin: 33.0..33.5..65.0
```

```chart:range
title: latency p50 ms, 512 tokens
CPU: 135.0..137.3..1011.8
WebGPU: 29.4..32.1..56.8
MLX plugin: 108.0..108.3..336.8
```

```chart:range
title: process CPU ms per embed p50, 128 tokens
CPU: 150.8..496.1..507.9
WebGPU: 3.1..4.7..10.9
MLX plugin: 141.6..143.4..146.2
```

```chart:range
title: follow-up latency p50 ms, 512 tokens (3 rounds x 30 runs, load 12-22)
WebGPU original: 27.3..30.4..32.1
WebGPU original + enableInt64: 31.2..39.0..42.1
WebGPU variant B: 36.9..37.3..41.3
MLX variant A: 32.1..34.1..34.8
MLX variant B: 27.7..28.6..29.0
```

```chart:range
title: follow-up process CPU ms per embed p50, 512 tokens
WebGPU original: 58.6..64.6..67.1
WebGPU original + enableInt64: 62.3..69.9..73.1
WebGPU variant B: 77.3..84.3..88.5
MLX variant A: 7.2..10.6..11.4
MLX variant B: 6.6..7.9..8.0
```

```chart:range
title: second follow-up latency p50 ms, 512 tokens (3 rounds x 30, load 9-14)
WebGPU, spinning on (shipped): 24.1..24.7..28.8
WebGPU, spinning off: 24.2..25.8..28.8
MLX variant B: 23.6..25.1..27.3
MLX variant C (Range folded): 21.4..21.5..23.5
```

```chart:range
title: second follow-up process CPU ms per embed p50, 512 tokens
WebGPU, spinning on (shipped): 52.6..58.1..59.3
WebGPU, spinning off: 5.2..7.5..8.3
MLX variant B: 4.1..5.6..5.8
MLX variant C (Range folded): 2.8..3.1..3.8
```

## Findings

### F1 — The shipped ORT 1.30.0 osx-arm64 build offers three providers: CoreML, WebGPU, CPU [MEASURED]

`OrtEnv.GetAvailableProviders()` returns `CoreMLExecutionProvider, WebGpuExecutionProvider,
CPUExecutionProvider`, and both CoreML and WebGPU accept `AppendExecutionProvider`. The dylib's
string table also names CUDA, DML, TensorRT and others, but those strings are just the provider
name list. They aren't compiled in.

**Evidence:** file-based app `eps.cs` (`#:package Microsoft.ML.OnnxRuntime@1.30.0`, `dotnet run eps.cs`)
on Apple M4 / macOS 26 (Darwin 25.6.0), 2026-09-24. `strings libonnxruntime.dylib` on the
installed 1.49.1 tool store for the name list.

### F2 — AiRaccoon puts only the bundled engine on WebGPU, macOS only, and falls back to CPU on refusal [READ]

The session appends WebGPU when `preferGpu` is set and the WebGPU provider is available. That
means macOS only. If ORT refuses, it builds a CPU session and records `CPU (GPU refused: …)`.
`embedding.device` is `auto|gpu|cpu`. `auto` prefers the GPU only for the bundled granite fp16
engine, because int8 graphs drift on the GPU (cosine 0.94–0.97). GPU runs are serialized
process-wide, because concurrent WebGPU runs segfault. CoreML is available but deliberately unused.
With dynamic shapes it ran about 3× slower than CPU in 37 partitions.

**Evidence:** `src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs:68-69,128-183`;
`src/AiRaccoon.Infrastructure/Embedding/EmbeddingDeviceSetting.cs:35`;
`docs/adr/0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md` Decision 4 and 6, Alternatives (CoreML).

### F3 — "execution provider WebGPU" is real GPU work, attributable per process [MEASURED]

IOKit's per-client `accumulatedGPUTime` showed the live server (pid 89212, `ai-raccoon serve
--restart`) with 0.18 s of Metal GPU time a few minutes after the logged session creation. It had
the M4's `AGXMetalG16G` driver mapped. A concurrently running AiRaccoon eval server (pid 5139,
another session's scratch binary) consumed 9.52 s of GPU in a 10 s window while draining. The
machine-wide "Device Utilization %" of 76–83% came from that process, not from the live server.
Single 10 s window, one sample.

**Evidence:** `ioreg -l -w0`, filtered for `IOUserClientCreator` and `AppUsage.accumulatedGPUTime`,
sampled twice 10 s apart. `lsof -p 89212` showed the Metal driver and `libonnxruntime.dylib`. Apple M4,
10 cores, 2026-09-24 ~02:50 local.

### F4 — The "nodes not assigned to the preferred execution providers" warning is expected, not a fallback [READ]

ORT's own warning text says shape-related ops are assigned to the CPU deliberately. ADR-0108
accepts that ORT keeps whatever the GPU cannot run on the CPU. That is the partitioning this
warning reports. A GPU refusal is logged differently: the provider string becomes `CPU (GPU refused: …)`.

**Evidence:** the user-supplied log, `session_state.cc:1397`; `OnnxEmbeddingGenerator.cs:156-181`;
ADR-0108 Decision 4.

### F5 — Stock ORT does not know MLX; it refuses the provider name [MEASURED]

`AppendExecutionProvider("MLX", …)` throws `InvalidArgument: Unknown provider name 'MLX'`, and the
error lists the built-in names. MLX isn't among them.

**Evidence:** same `eps.cs` probe as F1.

### F6 — An upstream out-of-tree MLX plugin EP exists, targets ORT 1.29 C ABI, and is Python/Rust-packaged [READ]

`onnxruntime/onnxruntime-ep-mlx` ships `libonnxruntime_mlx_ep.dylib`, registered through
`RegisterExecutionProviderLibrary` into a stock `libonnxruntime.dylib`. It registers as
`MLXExecutionProvider`, leaves unsupported ops on the ORT CPU, and compiles per dynamic-shape key.
Versioning pins it to one ORT C-API version (`0.29.x` ↔ ORT 1.29). It is distributed on PyPI and
crates.io. There is no NuGet package, and building it requires mlx-c, MLX 0.32.2 and Rust.

**Evidence:** https://github.com/onnxruntime/onnxruntime-ep-mlx README (main, fetched 2026-09-24),
"Requirements" and "Versioning" sections.

### F7 — The locally built MLX plugin loads and runs in ORT 1.30.0; 1.29.0 was not needed [MEASURED]

The plugin was built from `onnxruntime/onnxruntime-ep-mlx` at commit `b5efa582` against the ORT 1.30.0
C headers. The build took 3 min 14 s with Rust 1.98.1. It linked against the MLX runtime libraries
(`libmlx`, `libmlxc`, `mlx.metallib`) taken from the 0.29.6 wheel, plus mlx-c headers at the pinned
commit `c74db530`. The MLX-from-source path in `setup_mlx.sh` needs the Metal compiler, which Command
Line Tools lack (`xcrun: unable to find utility "metal"`).

`OrtEnv.RegisterExecutionProviderLibrary` loads the plugin. It then exposes one GPU device, and
`AppendExecutionProvider(env, devices, …)` creates a working session. There are two gotchas:
- The dylib must sit next to the MLX libraries, because the wheel's libraries use `@loader_path`
  install names.
- The device's `EpName` is the name passed at registration, so register it as `MLXExecutionProvider`.

**Evidence:** `bench.cs` (scratch harness, `Microsoft.ML.OnnxRuntime` 1.30.0 NuGet), output
`MLX devices: 1 (GPU)` and `ep=mlx ort=1.30.0`, plus the `[rust-mlx-ep]` session summary. Build log
`cargo build --release` with `ORT_HOME=onnxruntime-osx-arm64-1.30.0`, `MLX_PREFIX=MLXC_PREFIX=<assembled prefix>`.

### F8 — WebGPU keeps 385 of 404 nodes on the GPU; the 19 CPU nodes are shape/mask arithmetic [MEASURED]

With verbose logging, ORT reports 385 nodes on `WebGpuExecutionProvider` and 19 on
`CPUExecutionProvider`. The CPU nodes are Unsqueeze ×6, Gather ×3, Shape ×2, Concat ×2, Cast ×2,
Sub, Range, Greater and Abs. All attention and MatMul nodes run on the GPU. This is what the
production warning in F4 refers to.

**Evidence:** `BENCH_VERBOSE=1 ./bench webgpu <model_fp16.onnx>` (env and session severity VERBOSE),
`session_state.cc:1377-1385 VerifyEachNodeIsAssignedToAnEp` "Node placements" section.

### F9 — The MLX plugin claims 96.8% of nodes but leaves all 12 attention nodes on the CPU [MEASURED]

The plugin summary reports `388/401 nodes claimed (96.8%) across 15 fused subgraph(s)`. The unclaimed
nodes are `com.microsoft.MultiHeadAttention x12: optional input 5 is unsupported` and one Range with
runtime bounds. Input 5 of MultiHeadAttention is `attention_bias`, the additive QK′ bias. This model
feeds its global and local attention masks through that input. As a result, each layer's attention
runs on the CPU in fp32. ORT places 63 nodes on the CPU, including 50 `Cast` nodes around the
attention calls, and splits the graph into 15 MLX islands with a CPU hop between each.

**Evidence:** `ONNXRUNTIME_EP_MLX_VERBOSE=1 ONNXRUNTIME_EP_MLX_CLAIM_DEBUG=1 ./bench mlx …`, the
`MLX EP session summary` block and the ORT "Node placements" (MLX 15, CPU 63). Input 5 is
`attention_bias` per https://github.com/microsoft/onnxruntime/blob/v1.30.0/docs/ContribOperators.md
(com.microsoft.MultiHeadAttention, Inputs).

### F10 — WebGPU beats the MLX plugin ~3.4× on latency and ~30× on CPU at 128 tokens [MEASURED]

These numbers use the steady rounds 2–3; round 1 was the cold round with the most contention.
- **128 tokens:** WebGPU p50 9.2–9.7 ms against MLX 33.0–33.5 ms. Process CPU per embed was
  3.1–4.7 ms against 143–146 ms.
- **512 tokens:** WebGPU p50 29.4–32.1 ms against MLX 108.0–108.3 ms. Process CPU per embed was
  80–82 ms against 453–465 ms.
- **GPU time per embed:** WebGPU 8.4–10.5 ms (128) and 21.9–24.3 ms (512). MLX 6.2–14.9 ms and
  11.8–38.0 ms.

MLX's CPU cost is F9's CPU attention. It even exceeds the CPU provider's own cost at 512 tokens in
rounds 2–3 (453–465 ms against 661 ms for CPU), so it saves little over running everything on the CPU.

**Evidence:** `bench-results.log`. There were three rounds, each running `cpu`, `webgpu` and `mlx` in
turn in separate processes, 5 warm-up runs and 30 timed runs per length. Inputs were synthetic token
ids (seeded, [CLS] … [SEP], full mask) with `IntraOpNumThreads=5` on an Apple M4. Load average was
25–55 from concurrent sessions. GPU time is the harness process's IOKit `accumulatedGPUTime` delta
divided by runs.

### F11 — Both GPU paths match the CPU vectors; MLX slightly closer [MEASURED]

Against the CPU provider's fp16 output on identical inputs, WebGPU reached cosine 0.999978 (128) and
0.999981 (512). MLX reached 0.999999 at both lengths. The results were identical in all three
rounds. The measurement is deterministic for a fixed input, and both paths are well within
ADR-0108's parity bar.

**Evidence:** Python cosine over the `out/r{1,2,3}/{cpu,webgpu,mlx}-{128,512}.vec` files written by
`bench.cs` (384-dimension `sentence_embedding`).

### F12 — During the benchmark the GPU was shared with another session's AiRaccoon eval server [MEASURED]

Across 99 two-second samples, machine GPU utilisation ran 0–100% (median 81%). Another session's
AiRaccoon process (pid 8088) accumulated 68.6 s of GPU time over the 18 samples in which it was
active. The harness's own processes each recorded 0.5–1.3 s. The absolute latencies above are
therefore contended numbers. The WebGPU-vs-MLX ratio holds because the round-robin exposed each
provider to the same background.

**Evidence:** `gpusample.sh` (IOKit per-client `accumulatedGPUTime` deltas plus `Device Utilization %`,
every 2 s), `gpu-samples.log`, run in the background for the whole benchmark.

### F13 — Adopting the MLX plugin on the model as exported would be a regression [INFERRED]

This follows from F9–F11. On granite-small fp16 the plugin is about 3.4× slower than WebGPU. It also
puts the attention work back on the shared CPU, which ADR-0108 moved to the GPU for exactly that
reason. The small parity gain (0.999999 against 0.99998) doesn't justify it. On top of that it would
add a Rust-built native dylib, a 136 MB `mlx.metallib` and a C-ABI coupling to ship per RID. F16–F17 show that a rewritten graph removes the
bottleneck without any plugin change.

### F14 — A plugin built for the 1.29 C ABI (the published wheel) would also load into 1.30.0 (answered by F33) [UNVERIFIED]

This build used the 1.30.0 headers. The published 0.29.6 wheel dylib, built for ORT_API_VERSION 29,
was not loaded here.

### F15 — The plugin's MultiHeadAttention support has no mask path at all [READ]

`multihead_attention_claim` refuses the node whenever optional input 4 (`key_padding_mask`), 5
(`attention_bias`), 8 or 9 is present. The lowering, `multihead_attention_op`, calls
`sdpa_dispatch(…, None, …)`, which never passes a mask to MLX's scaled-dot-product attention. Moving
this model's mask from input 5 to input 4 would therefore still be refused. The plugin's
standard-domain `Attention` does carry a mask: `attention_op` resolves input 3 (`attn_mask`), and
`check_mask` accepts bool or float masks that broadcast to [B,H,Q,K].

**Evidence:** `onnxruntime-ep-mlx` @ `b5efa582`, `rust/src/ops/attention.rs:2453-2584`
(`multihead_attention_claim`, the `for i in [4usize, 5, 8, 9]` loop), `:1343-1436`
(`multihead_attention_op`), `:1226-1317` (`attention_op`, `present(n, 3)` → `sdpa_dispatch(…, mask, …)`),
`:2150-2161` (`check_mask`).

### F16 — The granite export feeds every layer's mask through `attention_bias` [MEASURED]

All 12 `com.microsoft.MultiHeadAttention` nodes (`num_heads=12`, `scale=0.17678`) take Q, K and V
plus input 5. Layers 0, 3, 6 and 9 get the global padding bias `/model/global_attn_mask/Tile`,
shaped [B,12,S,S]. The other eight get the sliding-window bias `/model/local_attn_mask/where_combined`,
a `Where` over |i−j| > window that writes −65504 outside the band. The opset is ai.onnx 21 plus
com.microsoft 1, 586 nodes in total. The local band is not a padding mask, so `key_padding_mask`
could not express it even if the plugin read that input.

**Evidence:** `onnx.load(model_fp16.onnx)` inspection in the scratch venv (onnx 1.x, Python 3.14):
node inputs, attributes and the producers of each input-5 tensor.

### F17 — Rewriting attention in standard ops moves all attention onto MLX, with identical vectors [MEASURED]

There are two rewrites (`rewrite.py`), and both keep the mask tensor unchanged:
- **Variant A:** each MHA becomes ai.onnx `Attention` (opset raised to 23) with `attn_mask` = the
  bias, `q_num_heads=kv_num_heads=12` and the same scale.
- **Variant B:** each MHA becomes Reshape → Transpose → MatMul → Mul(scale) → Add(bias) → Softmax →
  MatMul → Transpose → Reshape, all at opset 21.

With either variant the plugin claims 400/401 (A) or 544/545 (B) nodes in 2 fused subgraphs. Only
the runtime-bounds `Range` stays on the CPU. Cosine against the original model on the CPU provider
is 1.000000 (CPU), 0.999975–0.999981 (WebGPU) and 0.999999 (MLX) for both variants at 128 and 512
tokens.

**Evidence:** `ONNXRUNTIME_EP_MLX_CLAIM_DEBUG=1 ./bench mlx granite_{A,B}.onnx …` session summary
(`claim: 400/401 … across 2 fused subgraph(s)`, `544/545 …`). Parity comes from the `.vec` files
against `out/r3/cpu-*.vec`.

### F18 — MLX on variant B ties WebGPU on latency at about 1/8 of the CPU cost [MEASURED]

These are p50 ranges across three round-robin rounds of 30 runs, at load average 12–22, with no
other AiRaccoon process on the GPU:
- **Latency, 128 tokens:** MLX-B 9.1–13.5 ms, WebGPU original 10.9–11.4 ms.
- **Latency, 512 tokens:** MLX-B 27.7–29.0 ms, WebGPU original 27.3–32.1 ms.
- **Process CPU per embed:** 2.7–5.2 ms against 6.0–7.0 ms at 128 tokens; 6.6–8.0 ms against
  58.6–67.1 ms at 512 tokens.
- **GPU time per embed:** 6.9–9.6 against 9.3–10.3 ms at 128 tokens; 17.5–19.1 against 23.5–28.0 ms
  at 512 tokens.

MLX-A is slower than MLX-B: 15.9–17.8 ms and 32.1–34.8 ms. WebGPU on variant B is worse than on the
original at 512 tokens (36.9–41.3 ms, 77–89 ms CPU), because the original's fused WebGPU MHA kernel
beats the decomposed ops. WebGPU on variant A pushes all 12 `Attention` nodes to the CPU (79.9 and
190 ms), since WebGPU has no kernel for ai.onnx `Attention`.

**Evidence:** `bench2.log` (configs round-robin per round, each in its own process, 5 warm-up
runs), `gpu-samples-2.log` (98 samples, util median 33%, busiest GPU clients rider 30.8 s,
WindowServer 17.1 s, bench 12.5 s). Variant-A WebGPU numbers come from the single 10-run pass in
`verbose-A-webgpu.log`. Apple M4.

### F19 — WebGPU's 19 CPU nodes are int64 ops with no default kernel, not a deliberate shape split [MEASURED]

Verbose logging prints `webgpu kernel not found in registries` for exactly the 19 CPU nodes. They
are the mask arithmetic (`local_attn_mask/distance` Abs, `window_mask_gt` Greater, `range`, the
`attn_mask_reformat` and `global_attn_mask` Casts/Concats/Gathers) and sequence-length shape reads.
The provider option `ep.webgpuexecutionprovider.enableInt64=1` registers int64 kernels and
places 407 nodes on the GPU, leaving 3 on the CPU (2 `Shape` and the `Abs`). `enableGraphCapture=1`
is refused either way ("all compute graph nodes have not been partitioned").

**Evidence:** `verbose-webgpu.log` (38 lines = 19 nodes × 2 logs), `BENCH_WEBGPU_OPTS=enableInt64=1`
run placement, and graph-capture exceptions. The option keys are from
https://github.com/microsoft/onnxruntime/blob/v1.30.0/onnxruntime/core/providers/webgpu/webgpu_provider_options.h
(lines 13, 18). The kernel-lookup path is `webgpu_execution_provider.cc:662-667`.

### F20 — More nodes on WebGPU is slower; the default 385/404 is the fastest placement available [MEASURED]

With `enableInt64=1`, p50 latency rose from 10.9–11.4 to 17.7–27.2 ms at 128 tokens and from
27.3–32.1 to 31.2–42.1 ms at 512 tokens, in the same rounds. The CPU part of the default placement
costs about 0.48 ms of kernel time per run, plus 0.55 ms for 4 `MemcpyFromHost` per run, which is
roughly 3% of a 32.6 ms run at 512 tokens. That is the ceiling on any gain from moving it.
WebGPU's bigger cost is elsewhere. It burns 59–67 ms of process CPU per 512-token embed (F18),
twice its own wall time. MLX-B does not.

**Evidence:** `bench2.log` rounds 1–3. The ORT profile is `onnxruntime_profile__2026-09-24_04-00-54_528.json`
(`BENCH_PROFILE`, 25 runs at 512 tokens), summarised by `profsum.py`: WebGPU 8.9 ms and CPU 0.48 ms of
kernel time per run, 100 `MemcpyFromHost` events over 25 runs.

### F21 — Why int64 on WebGPU is slower, and where WebGPU's CPU time goes (second half answered by F25) [UNVERIFIED]

Neither was traced. Candidates are emulated int64 in WGSL for the first, and host-side dispatch or
spin-waiting for the second. A CPU sample (`sample <pid>` or Instruments) during a 512-token run
would settle the second.

### F22 — Shipping variant B on MLX would beat WebGPU on CPU cost (superseded by F25–F26) [INFERRED]

This follows from F17–F20. With equal latency and parity, and about an eighth of the CPU per embed
at 512 tokens, MLX-B serves ADR-0108's actual goal (keeping embeds off the shared CPU) better than
WebGPU does. The costs don't change from F13: a Rust-built plugin dylib, a 136 MB `mlx.metallib`, and
ORT C-ABI coupling in the package, plus a rewritten model that must be re-pinned. The rewrite is a
deterministic 13-node-per-layer graph edit, and its vectors match the original at cosine 1.000000
on CPU.

### F23 — The osx-arm64 package is 180.6 MB today, 223.6 MB with MLX added, and 149.3 MB with MLX if the weights ship once [MEASURED]

The installed 1.49.1 RID package, `ai-raccoon.osx-arm64.1.49.1.nupkg`, is 180,554,671 bytes. It
carries `model_fp16.onnx_data` (97.4 MB) twice: at `Models/…` and at `tools/net10.0/osx-arm64/Models/…`.
The RID-agnostic `ai-raccoon.1.49.1.nupkg` (76.3 MB) carries a third copy.

The MLX runtime compresses as follows: `mlx.metallib` 135.7 → 35.6 MB, `libmlx.dylib` 21.5 → 5.4 MB,
the plugin 4.2 → 1.7 MB, `libmlxc` 0.9 → 0.2 MB. Adding those four files at zip -9 gives 223.6 MB. That
is under the documented limit, with about 26 MB of headroom. Dropping the root `Models/` weights
copy as well gives 149.3 MB.

**Evidence:** `ls -l` and `unzip -l` on the tool-store nupkgs; `gzip -9c | wc -c` per MLX file. The
simulated packages were `zip -9 -r` copies (`pkg/sim.nupkg`, `pkg/sim-dedup.nupkg`) with
`tools/net10.0/osx-arm64/mlx/*` added.

### F24 — nuget.org's limit is "about 250 MB" per package [READ]

**Evidence:** https://learn.microsoft.com/nuget/nuget-org/publish-a-package#package-size-limits ("Nuget.org
has a package size limit of about 250 MB", 413 on push). The NuGet FAQ says "allows packages up to 250MB".

### F25 — WebGPU's CPU cost is ORT's intra-op thread pool spin-waiting, and one setting removes it [MEASURED]

A 10 s `sample` of a 512-token WebGPU loop shows four threads at ~50% CPU each, and their stacks
are the thread pool. `ThreadPoolTempl::WorkerLoop` and its lambda account for 23,027 top-of-stack
samples, plus 339 in `SpinPause`, against ~110 in real CPU kernels (`Sub<long long>`, `Abs<long long>`).
With `session.intra_op.allow_spinning=0`, or with `IntraOpNumThreads=1`, the process CPU per 512-token
embed drops from 44.2–47.1 ms to 3.5–4.1 ms, and latency is unchanged (24.0–24.6 ms p50 either way).
AiRaccoon's WebGPU session is built with 5 intra-op threads and spinning left at the default
(`OnnxEmbeddingGenerator.cs:156-176`, "intra-op threads 5" in the production log).

**Evidence:** `sample <pid> 10` → `webgpu.sample.txt`, and `ps -M` thread CPU. `spin.log` holds three
rounds of `threads=5 spin=1`, `threads=5 spin=0` and `threads=1 spin=1`, 30 runs each, load ~10.
The harness sets `so.AddSessionConfigEntry("session.intra_op.allow_spinning", "0")`.

### F26 — With spinning off, WebGPU and MLX are close; the fully fused MLX graph is ~10–20% faster [MEASURED]

These are p50 ranges from three round-robin rounds of 30 runs at load 9–14, with no other AiRaccoon
process on the GPU:
- **Latency, 128 tokens:** WebGPU (spin off) 8.3–13.0 ms, MLX-B 7.8–14.7 ms, MLX-C 6.4–7.7 ms.
- **Latency, 512 tokens:** WebGPU 24.2–28.8 ms, MLX-B 23.6–27.3 ms, MLX-C 21.4–23.5 ms.
- **CPU per embed, 512 tokens:** WebGPU 5.2–8.3 ms, MLX-B 4.1–5.8 ms, MLX-C 2.8–3.8 ms.
- **GPU per embed, 512 tokens:** WebGPU 22.0–23.8 ms, MLX-C 15.1–19.8 ms.

WebGPU with spinning on, the shipped setting, used 52.6–59.3 ms of CPU at 512 tokens in the same
rounds.

**Evidence:** `bench3.log` and `gpu-samples-3.log` (busiest GPU clients: rider 9.1 s, bench 6.2 s,
WindowServer 5.2 s).

### F27 — Folding the last `Range` gives MLX the whole graph as one fused subgraph [MEASURED]

The `Range(0, S, 1)` in `/model/local_attn_mask/range`, with S from `Shape(attention_mask)[1]`, was
replaced by `CumSum(ConstantOfShape(Unsqueeze(S), 1), axis 0) − 1` (`fold_range.py`), giving variant C.
The plugin then reports `547/547 nodes claimed (100.0%) across 1 fused subgraph(s)`, and ORT places
nothing on the CPU. Cosine against the original CPU output is 1.000000 on CPU and 0.999999 on MLX.

**Evidence:** `ONNXRUNTIME_EP_MLX_CLAIM_DEBUG=1 ./bench mlx granite_C.onnx …` → `verbose-C-mlx.log`;
parity from the `out/C/*.vec` files.

### F28 — On real corpora, retrieval quality is unchanged on every provider; MLX-C tracks the CPU reference more closely than shipped WebGPU [MEASURED]

These are dense-only rankings (no keyword leg, no fusion) over the 174-doc/68-query memory corpus and
the 12-language code corpus (312 non-negative queries, 1,379 line chunks of ≤400 tokens). Every text
went through the same `tokenizer.json`, one row per run.

| provider / graph | memory nDCG@10 | memory MRR@10 | code hit@1 | code hit@5 | top-10 identical to CPU ref (mem / code) |
|---|---|---|---|---|---|
| CPU, original (reference) | 0.6359 | 0.8405 | 0.5897 | 0.8365 | 68/68, 312/312 |
| WebGPU, original (shipped) | 0.6366 | 0.8408 | 0.5929 | 0.8365 | 35/68, 186/312 |
| MLX, variant C | 0.6356 | 0.8405 | 0.5897 | 0.8365 | 47/68, 243/312 |
| CPU, variant C | 0.6359 | 0.8408 | 0.5897 | 0.8365 | 60/68, 268/312 |

Minimum per-row cosine against the reference is 0.999962 for WebGPU and 0.999997 for MLX-C. The
top-10 differences are reorderings among near-tied candidates; the quality metrics move by ≤0.003.
The memory nDCG@10 of 0.636 reproduces the benchmark's 0.632–0.636 for this model, which is a sanity
check on the harness.

**Evidence:** `dump.cs` (`#:project` AiRaccoon.Benchmarks, RealWorldCorpus and RealWorldQueries) →
`memcorpus.json`. Then `tokenize_eval.py` → `evalset.ids` (1,933 rows). `BENCH_EMBED` mode of `bench.cs`
per config → `evalvec/*.f32`, and `score_eval.py` for the metrics. Code relevance means the chunk is in
`expectedSource` and overlaps `expectedLines`.

### F29 — MLX recompiles once per new sequence length [MEASURED]

Over 1,933 rows with 285 distinct lengths, the plugin reports `cache: 3581 HIT / 1 MISS / 284 RETRACE`
across two passes, so every new length retraced once. The first pass took 59.3 s on MLX against
63.1 s on WebGPU; the second, warm pass took 50.0 s against 62.6 s. Each is a single run in one
process, spinning off. The earlier cold runs of the same file took 86.2 s (MLX) and 46.1 s (WebGPU),
so these whole-corpus timings swing with machine load. They show no large compile penalty, but they
are not a ranking.

**Evidence:** `BENCH_EMBED_PASSES=2 ONNXRUNTIME_EP_MLX_VERBOSE=1 ./bench {webgpu,mlx} …`, including the
MLX session summary `compute:` line.

### F30 — MLX can ship in the osx-arm64 package only, with one conditional item group [MEASURED]

In a scratch tool project with `RuntimeIdentifiers=osx-arm64;linux-x64` and `PackAsTool`, an
`ItemGroup Condition="'$(RuntimeIdentifier)' == 'osx-arm64'"` holding the native files
(`CopyToOutputDirectory`, `Link="mlx/…"`) packed them into `ridtool.osx-arm64.0.0.1.nupkg` under
`tools/net10.0/osx-arm64/mlx/`. They were absent from `ridtool.linux-x64` and from the top-level
`ridtool.0.0.1.nupkg`. AiRaccoon uses the same RID-specific tool packaging
(`src/AiRaccoon/AiRaccoon.csproj:5`, `RuntimeIdentifiers`). Its model items, by contrast, use
`Pack="true" PackagePath="Models/"` (`:30-31`), and that is what puts the weights at the package root
and in the top-level package.

**Evidence:** `ridtool/ridtool.csproj`, `dotnet pack -c Release -o out`, and `unzip -l` on all three
nupkgs.

### F31 — The weights' root copy is only a fallback [INFERRED]

`BundledModel.ResolveDirectory` walks up from `AppContext.BaseDirectory` looking for
`Models/<dir>/manifest` (`BundledModel.cs:81-94`). An installed tool runs from
`tools/net10.0/<rid>/`, which has its own `Models/` copy, so the root copy is only reached when that
copy is missing. This reasons from the lookup code and the package layout in F23. Nobody removed
the root copy and ran the installed tool.

### F32 — Next steps, by value per effort [INFERRED]

This follows from F25–F29.
1. **Turn off intra-op spinning on the WebGPU session** (done in #712), or give it 1 intra-op thread. That is a
   one-line change that cuts about 40–55 ms of shared CPU from every 512-token embed, with no
   latency or vector change.
2. **Weights once per package** (F23, F31; PR #714, open at time of writing, F44). About 74 MB off the osx-arm64 package and 97 MB off the
   top-level one, and it restores headroom for anything else.
3. **MLX with variant C** is the remaining gain: ~10–20% latency and ~half the remaining CPU, at the
   cost of a Rust-built plugin, ~43 MB compressed per osx-arm64 package, a re-pinned rewritten
   model, and ORT C-ABI coupling. It is worth doing only after 1 and 2, and only if embed throughput
   matters.

### F33 — The published 0.29.6 wheel's plugin (ORT 1.29 C ABI) loads into ORT 1.30.0 and matches the local build exactly [MEASURED]

The plugin was registered from the unpacked wheel directory, so it sits beside its own `libmlx`,
`libmlxc` and `mlx.metallib`. It exposes one GPU device and claims 547/547 nodes of variant C in one
fused subgraph. Its vectors equal the locally built plugin's at cosine 1.0000000 (128 and 512
tokens), and 0.9999992–0.9999993 against the original CPU output. A locally built plugin is
therefore not required for ORT 1.30.0.

**Evidence:** `./bench mlx granite_C.onnx out/whl deps/whl/onnxruntime_ep_mlx/libonnxruntime_mlx_ep.dylib 10`,
then cosine of `out/whl/mlx-*.vec` against `out/C/mlx-*.vec` and `out/r3/cpu-*.vec`.

### F34 — `enableInt64` is slower because it adds GPU→CPU readbacks mid-graph [MEASURED]

In ORT profiles at 128 tokens (30 runs), the default placement has 4 `MemcpyFromHost` per run and no
`MemcpyToHost`. With `enableInt64=1` there are 5 `MemcpyToHost` per run, totalling 26.7 ms of
profiled time. Those are the reads that feed the three nodes still on the CPU (2 `Shape` and the
`Abs`) from tensors now computed on the GPU. Each readback waits for the GPU queue to drain before the
CPU node can run, which serializes the graph. In the default placement the int64 mask arithmetic
starts from CPU-resident graph inputs, so nothing is read back. Absolute times in this run are
inflated by concurrent builds (model_run p50 25.4 ms against 30.7 ms), but the mechanism doesn't
depend on load.

**Evidence:** `BENCH_PROFILE` runs with and without `BENCH_WEBGPU_OPTS=enableInt64=1` →
`prof2/onnxruntime_profile__2026-09-24_04-46-{07,10}_*.json`, summarised per provider and op.

### F35 — Spinning is what the running server's CPU goes to while it embeds [MEASURED]

A scratch server (installed 1.49.1 binary, fresh data root, `model embedding set local`) was
ingesting `docs/adr` (109 files) on WebGPU and re-embedding. It ran at 62.5% CPU. A 10 s `sample`
shows the busiest non-idle stacks in `ThreadPoolTempl::WorkerLoop` and its lambda (8,842 samples)
plus `SpinPause` (56). The WebGPU dispatch (`WebGpuContext::Run`, 47), SQLite (`sqlite3VdbeExec`,
37) and the JIT each show under 100. This matches F25, and it supports spinning as the likely source
of the live server's 92% CPU after its start, though that process was not itself sampled.

**Evidence:** `spin_confirm.py` (repo `retrieval_tuning.server.start_server`, `settings ingest scope
add`, `memory_ingest_directory`), `sample <pid> 10` → `serve.sample.txt`, `spin-serve.log`
("execution provider WebGPU", "intra-op threads 5").

### F36 — On the bank title→document eval, quality is unchanged per provider; MLX-C tracks CPU more closely than WebGPU [MEASURED]

The documents came from a read-only snapshot of the live bank (`sqlite3 .backup`, 1.66 GB):
- 150 documents were sampled with seed 5 from the 623 ADR, work and reference sources. 117 of
  them had an H1 in chunk 0; that title is the query, and the heading line is removed from the chunk.
- For each document, the first ≤8 stored chunks form its relevant set. All 756 chunks form the
  corpus.

| provider / graph | nDCG@10 | top-10 identical to CPU | min cosine vs CPU |
|---|---|---|---|
| CPU, original | 0.5043 | 117/117 | 1.000000 |
| WebGPU, original (shipped) | 0.5017 | 65/117 | 0.999968 |
| MLX, variant C | 0.5045 | 92/117 | 0.999998 |

The absolute nDCG is not comparable with the survey's 0.377: the sample, the dedupe (one row per
chunk index) and the 117-document subset all differ. Only the per-provider comparison is the
result.

**Evidence:** `bankeval_build.py` (snapshot `bank-snap.db`, the `entries` table) →
`bankset.ids`/`bankset.meta.json`. Vectors are from `BENCH_EMBED` per config (spinning off) →
`bankvec/*.f32`, scored with nDCG@10 by an inline numpy scorer.

### F37 — A fully source-built MLX stack (pinned MLX 0.32.2 + mlx-c) gives identical results [MEASURED]

With the Xcode Metal Toolchain installed (`xcodebuild -downloadComponent MetalToolchain`, 839 MB,
metal 32023.921), `setup_mlx.sh` built the pinned MLX and mlx-c from source in 8 min 36 s. The
plugin was rebuilt against it in 53 s. It claims 547/547 nodes in 1 fused subgraph and gives vectors
identical to the wheel-MLX build (cosine 1.0000000). Timing at 20 runs was in the same range as
before: 8.6 ms p50 at 128 tokens and 24.5 ms at 512. This build links `@rpath/libmlx*.dylib`, not
`@loader_path`, so a packaged copy would need `install_name_tool` fixes or a matching rpath.

**Evidence:** `DEVELOPER_DIR=/Applications/Xcode.app/Contents/Developer bash rust/scripts/setup_mlx.sh deps/mlx-src-b`
→ `mlx-src-build2.log`. Then `cargo build --release` with `MLX_PREFIX=MLXC_PREFIX=deps/mlx-src-b` →
`build-src.log`, `otool -L`, and `./bench mlx granite_C.onnx out/src mlx-ep-src/…` against
`out/C` and `out/r3`.

### F38 — On the product harness, CPU and WebGPU give the same hybrid quality: nDCG@5 0.8247 vs 0.8238, hit@1 0.7711 vs 0.7681, hit@5 0.8916 both [MEASURED]

`run_code_eval.py` ran on the 12-language code corpus (332 queries, 1,376 product chunks) with a scratch copy of the installed 1.49.1 binary. There was one fresh data root per arm, and the device was set before the engine loaded. Each server log confirms its provider: `execution provider CPU` and `execution provider WebGPU`. The WebGPU arm's top-1 matched the CPU arm's on 329 of 332 queries, its top-5 on 287 and its top-10 on 185. Ten queries changed nDCG@5: 6 up and 4 down, with no hit@5 gained or lost. `compare_code_eval.py` gave a held-out bootstrap mean of −0.0043, CI [−0.0171, 0.0073], n=105, so the difference is inside noise. The CPU arm's 0.8247 reproduces the benchmark doc's shipped 0.824.

**Evidence:** `scratchpad/hyb/drive.py`, which wraps `run_code_eval.py`: it bootstraps a scratch server, runs `settings model device cpu|auto`, then runs the eval. The flags were `--binary hyb/bin149/AiRaccoon --corpus-root code-corpus/files --queries code-corpus/queries.json --save-hits --allow-busy`. Outputs are `scratchpad/hyb/runs/results-{cpu,webgpu}.json`, `hits-*.json`, `data-root-*/serve.log` and `scratchpad/hyb/compare-cpu-webgpu.txt`.

### F39 — The product's code fusion is plain weighted RRF, reproducible outside the server [READ]

`SqliteCodeSearchService` works in these steps:
- It runs an FTS5 leg: `code_fts MATCH plan.Expression`, `ORDER BY bm25`, `LIMIT window`.
- It runs a vec0 leg: `vec_code`, cosine distance, `k = window`, `ORDER BY distance, path`.
- Both legs use window = `CandidateWindowFor(10) = 100` (max3x100).
- It fuses with score += weight/(k+rank), k=60 and weights 1 by default. No retrieval settings rows exist in the scratch bank.
- It max-normalizes, orders by score desc then path, and takes 10.

There is no structure, sibling or relevance-floor stage on the code path.

**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/Code/SqliteCodeSearchService.cs:28-61` (flow), `:82-111` (legs), `:120-163` (Fuse/AddList). `SqliteMemoryStore.Search.cs:25-28` (window) and `SearchQuery.cs:21` (`DefaultRrfK = 60`). `git log v1.49.1..HEAD` shows no change to these files or to `FtsQueryNormalizer.cs`.

### F40 — The simulation reproduces the product exactly, so the MLX-C arm is measured through the product's own ranking [MEASURED]

The simulation (`scratchpad/hyb/sim.py`) re-creates the product's code search outside the server:
- **FTS leg:** it reuses the product's FTS query plans, taken from the internal `FtsQueryNormalizer.BuildPlan` via a file-based app compiled as `AiRaccoon.Benchmarks` (an InternalsVisibleTo name). The same FTS SQL runs on a copy of the CPU arm's bank.
- **Dense leg:** stored bank vectors, or new vectors from `bench.cs` BENCH_EMBED, with H2's RRF and tie-break.

Checks that the simulation matches the product:
- Stored CPU chunk vectors plus bench CPU query vectors reproduce the product CPU arm's top-10 on 332/332 queries, with identical metrics.
- The same holds for the WebGPU arm (332/332).
- Bench `cpu-orig` embeddings of the stored `code_entries.value` text match the stored CPU vectors at cosine min 1.000000. The product embeds the chunk `value` verbatim.
- A `compare_code_eval.py` run on the simulated WebGPU arm reproduces the real run's bootstrap output exactly (mean −0.0043, same CI).

**Evidence:**
- `scratchpad/hyb/ftsplan/plan.cs` → `scratchpad/hyb/fts-plans.json` (332 plans).
- `scratchpad/hyb/extract.py` → `scratchpad/hyb/prod.ids` (1,376 chunks + 332 queries) and `scratchpad/hyb/stored-{cpu,webgpu}.npy`.
- `bench` BENCH_EMBED (spinning off) → `scratchpad/hyb/prod-{cpu-orig,webgpu-orig,mlx-C}.f32`.
- `scratchpad/hyb/sim.py` output (FIDELITY lines).

### F41 — MLX variant C keeps hybrid quality within noise, and its top-1 never moves: nDCG@5 0.8225, hit@1 0.7711, hit@5 0.8855 [MEASURED]

Against the CPU reference arm (sim of cpu-orig, identical to the product CPU arm):
- **MLX-C:** top-1 identical on 332/332, top-5 on 310/332, top-10 on 255/332. Six queries changed nDCG@5 (3 up, 3 down), and two lost hit@5. The held-out bootstrap is mean −0.0032, CI [−0.0141, 0.0033], and the tuning-split delta is −0.0018.
- **Shipped WebGPU, for comparison:** top-1 329/332, top-5 287/332, top-10 185/332, bootstrap mean −0.0043.

So MLX-C drifts less from the CPU reference than the shipped WebGPU path does. Its hit@5 is 0.006 lower on this set (2 of 332 queries).

**Evidence:** `scratchpad/hyb/sim.py` (SIM lines), `scratchpad/hyb/diff.py` (per-query changes, `scratchpad/hyb/results-sim-{webgpu-orig,mlx-C}.json`), and `compare_code_eval.py --target-category behaviour-nl results-cpu.json results-sim-mlx-C.json`.

### F42 — Both MLX-C hit@5 losses are fused-score near-tie flips, not retrieval failures [MEASURED]

- **nlohmann-json-003:** the target chunk (`adl_serializer.hpp:20`) scores 0.7715 on CPU and 0.7644 on MLX-C, inside a cluster of four candidates at 0.7634–0.7715. It drops from rank 3 to rank 6. Shipped WebGPU gives the same chunk the same 0.7644 and moves it to rank 5.
- **bootstrap-015:** the target's score is unchanged (0.7467). A competing `test-mathjax4.html:149` chunk rises from 0.7433 to 0.7470 and passes it by 0.0003.

**Evidence:** `scratchpad/hyb/sim-results.json`, with top-7 (path, lineStart, fused score) dumped per arm for both queries.

### F43 — The product's hybrid effect on memory search (not code) was not measured per provider [UNVERIFIED]

Memory search adds structure fusion, sibling boost and the relevance-floor rescale on top of RRF (ADR-0108 item 5). This lane covered the code path only.

### F44 — Packing the weights once, measured with a real `dotnet pack`: osx-arm64 180.6 → 104.2 MB, top-level 76.3 MB → 7.9 KB [READ]

The follow-up change drops `Pack="true" PackagePath="Models/…"` from the model items and excludes the
model's JSON from the Web SDK's `content/` glob. After it, a real `dotnet pack
-p:RuntimeIdentifiers=osx-arm64` produces:
- `ai-raccoon.osx-arm64.1.49.2.nupkg`: 104,239,604 B (from 180,554,092), with exactly one
  `model_fp16.onnx_data` under `tools/net10.0/osx-arm64/Models/`.
- The RID-agnostic `ai-raccoon.1.49.2.nupkg`: 7,939 B (from 76,323,697), with no model files.

The packed tool installed to a throwaway tool path loads the bundled engine (`doctor` → `memory
engine: bundled`). This confirms F31's inference that the root copy was only a fallback. With MLX
added (F23's ~43 MB compressed), the osx-arm64 package would be about 147 MB, leaving ~100 MB of
headroom under the limit.

**Evidence:** PR #714's report (`src/AiRaccoon/AiRaccoon.csproj`, `scripts/src/package_verify.py`),
including the before/after `unzip -l` and the install-and-load proof. Graded READ because the pack
was run by the delegated lane, not in this record's session.

### F45 — Through the product's hybrid memory search, CPU and WebGPU give the same quality [MEASURED]

The 174-doc memory corpus was written as markdown files (`# Title` + body) and ingested with
`memory_ingest_directory` into a fresh scratch bank per arm. Production chunking, headings,
structure fusion, the sibling boost and the relevance floor all applied. Each arm drained all 174
entries before any query ran. The 68 queries went through `memory_search(kind=memory,
scope=project, limit=10, minRelativeScore=0)`, with results mapped to documents by source file.

| arm (installed 1.49.2) | nDCG@10 | MRR@10 |
|---|---|---|
| `settings model device cpu` (log: execution provider CPU) | 0.6844 | 0.9170 |
| `auto` (log: execution provider WebGPU) | 0.6863 | 0.9178 |

Top-1 is identical on 68/68 queries, top-5 on 64/68 and top-10 on 46/68. Hybrid nDCG is higher than
the dense-only 0.636 (F28) because the keyword leg and structure fusion add signal.

**Evidence:** `memeval_product.py <scratch> {cpu cpu|webgpu auto} ai-raccoon`, which uses the repo's
`retrieval_tuning.server.start_server`. The device was set on a first server start, then a second
start loaded the engine and ran the evaluation. Output is in `memeval-{cpu,webgpu}.json`, with the
provider taken from `memeval-<arm>/serve.log`.

### F46 — The product's opt-in MLX device runs end to end (draft PR #722) [READ]

PR #722 (branch `feat/mlx-embedding-provider`, ADR-0110 Proposed) adds `settings model device mlx`
for the bundled engine. Its parts:
- **Runtime:** the plugin is registered once per process, the MLX device is appended, and the
  rewritten `model_fp16_mlx.onnx` is loaded. That file is 219 KB and references the unchanged
  `model_fp16.onnx_data`. If MLX fails, the session falls back to WebGPU and then CPU.
- **Packaging:** the MLX runtime is fetched sha-pinned from the 0.29.6 wheel and ships only in
  `tools/net10.0/osx-arm64/mlx/`.
- **Size:** the packed osx-arm64 1.49.3 package is 223.7 MB, measured before #714. That matches
  F23's 223.6 MB simulation.
- **Thread affinity:** the lane found that MLX sessions are thread-affine. The plugin aborts with
  "this InferenceSession first ran on thread … but Run() was called from ThreadId(2). MLX eval is
  thread-affine". So the generator runs every MLX session call on one dedicated thread
  (`SingleThreadExecutor`).

This file-and-harness research did not surface the thread-affinity constraint, because
`bench.cs` runs a session on one thread.

**Evidence:** PR #722 description and the lane's report, including the e2e transcript with
`execution provider MLX` and a vector leg in `memory_search`. Graded READ: the implementation and
pack were run by the delegated lane.

### F47 — Through the product, MLX gives the same hybrid memory quality as CPU and WebGPU [MEASURED]

This used the same procedure as F45, with the #722 build (`1.49.3+93745ad5`, scratch tool path) and
`settings model device mlx`. The log shows `execution provider MLX`. The result was nDCG@10 0.6839
and MRR@10 0.9109, against 0.6844/0.9170 for CPU and 0.6863/0.9178 for WebGPU. Against the CPU
arm, top-1 is identical on 67/68 queries, top-5 on 62/68 and top-10 on 53/68. WebGPU matched CPU on
46/68 at top-10.

**Evidence:** `memeval_product.py <scratch> mlx mlx <scratchpad>/mlx-e2e/tool/ai-raccoon` →
`memeval-mlx.json`, `memeval-mlx/serve.log`.

### F48 — Through the product, MLX hybrid code search reproduces the F41 simulation exactly [MEASURED]

`run_code_eval.py` with `device mlx` on the #722 build scored the same as the F41 simulation:
nDCG@5 0.8225, hit@1 0.7711, hit@5 0.8855 and nDCG@10 0.8044. The log shows `execution provider
MLX`. `compare_code_eval.py` against the CPU arm gives a held-out bootstrap mean of −0.0032, CI
[−0.0141, 0.0033], which is the simulation's own figure. The difference from CPU is inside noise.
The tool labels it "DROP" because its rule asks whether a candidate *improves* on the baseline; this
question is whether it degrades it.

Drain time for the 1,376 chunks was 78.3 s on MLX, 64.4 s on WebGPU and 423.5 s on CPU. Those are
single runs on a loaded machine, and the WebGPU and CPU runs used the 1.49.1 binary (spinning on).
They are not a throughput ranking.

**Evidence:** `EVAL_DEVICE=mlx python3 hyb/drive.py --binary <scratchpad>/mlx-e2e/tool/ai-raccoon
--corpus-root code-corpus/files --queries code-corpus/queries.json --arm mlx --out hyb/runs
--save-hits --allow-busy` → `hyb/runs/results-mlx.json`, then
`compare_code_eval.py --target-category behaviour-nl runs/results-cpu.json runs/results-mlx.json`.

### F49 — Uncontended, on one binary, WebGPU and MLX drain the code corpus in the same time [MEASURED]

The same #722 build (`1.49.3+93745ad5`, which has #712's spinning off for both GPU paths) ran
`auto` (WebGPU) and `mlx` alternately for three rounds, each on a fresh scratch bank with 1,376
chunks. Load average was 3.6–6.7.
- **Drain time:** WebGPU 34.1, 34.1 and 34.1 s; MLX 32.1, 32.1 and 34.2 s. The runner polls
  pending rows every 2 s, so these sit at its resolution. MLX is at most ~2 s (≈6%) faster over the
  whole drain, and part of each drain is chunking and FTS work that doesn't depend on the device.
- **Quality:** identical to F48 in every round (WebGPU nDCG@5 0.8239, MLX 0.8225). Both are
  deterministic across rounds.

The per-embed latency gain of F26 (10–20%) doesn't turn into a meaningful end-to-end drain gain on
this corpus.

**Evidence:** `EVAL_DEVICE={auto,mlx} python3 hyb/drive.py --binary <scratchpad>/mlx-e2e/tool/ai-raccoon …
--arm <dev>-r<n> --out tput` → `tput/log.txt`. The drain poll interval is `DRAIN_POLL_SECONDS` in
`scripts/retrieval_tuning/run_code_eval.py`.

### F50 — MLX's single-thread executor adds no serialization the WebGPU path didn't already have [INFERRED]

This reasons from ADR-0108 and the generator code. Memory and code share one ONNX session, because
the engine cache is keyed by fingerprint (ADR-0108, Context). WebGPU runs already pass through the
process-wide `GpuGate` lock (`OnnxEmbeddingGenerator.cs`, `Run`), so every embed is already
serialized on the GPU path. #722's `SingleThreadExecutor` replaces the lock with a dedicated thread
(F46). Concurrency stays the same, one run at a time, and only the thread identity changes. It was
not measured with overlapping memory and code drains.

## Still open

- **Overlapping memory and code drains** were not measured on either GPU path. F50 argues that
  MLX's single-thread executor changes nothing there, but that is not a measurement.
- **Whether MLX is worth its ~43 MB.** F47–F49 show equal quality and ≤6% end-to-end drain gain on
  this corpus. The remaining argument for MLX is per-embed CPU (F26: ~3 ms against ~5–8 ms at 512
  tokens), which matters on a CPU-contended machine. That is the owner's call in ADR-0110.
