# 0118 — An opt-in `coreml` device runs the bundled engine on the Neural Engine

Date: 2026-09-26

Status: **Accepted** — 2026-09-26. Opt-in shipped in 1.53.0. It supersedes
[ADR-0117](0117-coreml-execution-provider-stays-out.md)'s decision. Making it the Apple Silicon
default is a later, separate decision, still gated by "Before it can become the default" below.

Research: `docs/work/2026-09-25-ane-layout-reexport.md` (F1-F7), following
`docs/work/2026-09-25-coreml-ane-buckets-and-residency.md`.

## Context

ADR-0117 kept the CoreML execution provider out. On it, the shipped graph cost 7-18x MLX's CPU. The
ADR named one lever that could change that verdict: a re-export in Apple's `ml-ane-transformers`
layout. That re-export now exists (#760, #767) and has been measured on an M4:

- **Placement.** It loads as one CoreML partition, and 1401-1405 of about 1407 ops run on the
  Neural Engine (F2). The graph has to be fp16: fp32 graphs place no ops on the Neural Engine at all
  (F1).
- **Cost against MLX** (MLX ×64 buckets, 512 MiB cap, 3 threads). With 256-token buckets it uses 6-8%
  of MLX's CPU-seconds, and every repeat was below every MLX repeat. Peak phys+neural memory is
  0.49-0.57 GiB and the compiled cache is 808 MiB, so all three ADR-0117 re-open gates clear (F3).
- **Quality.** Recall matches the CPU baseline on all four eval arms (F4).
- **Dispatch.** `powermetrics` shows the Neural Engine rail at 1276 mW while the graph runs, against
  29 mW idle (F6).
- **Size.** The ANE graph can read the weights file the product already ships. It adds a 5.7 MB
  graph and no second copy of the weights (F7).

All of this comes from one chip. WebGPU, today's macOS default, and this graph have also never been
measured in the same harness (the gap is INFERRED from the MLX record's F26). Both gaps are reasons
to ship the path opt-in first, in the same way MLX shipped (ADR-0110).

## Decision

**A new `embedding.device coreml` runs the bundled engine on the Neural Engine, on osx-arm64
only.** It uses the CoreML EP and the shared-weights ANE graph. `auto` is unchanged: WebGPU on macOS
and CPU elsewhere. `gpu`, `cpu`, `mlx` and `cuda` keep their meanings. Custom (manifest) models
ignore `coreml` and use the CPU, the same way they ignore `mlx`, because only the bundled graph has
an ANE re-export. On any other platform the setting is refused with a reason, and the
WebGPU-then-CPU chain runs, as with MLX's refusals (ADR-0110).

**What ships.**
- `model_fp16_ane.onnx` (5.7 MB) sits in the bundled model directory, next to `model_fp16.onnx` and
  `model_fp16_mlx.onnx`.
- It reads `model_fp16.onnx_data` at the product graph's own offsets, so it has to live in that same
  directory. ORT refuses external data reached through a symlink out of the model directory, and
  `onnx.load` refuses a hard-linked file (F7).
- `scripts/retrieval_tuning/export_ane_encoder.py --share-weights-with` builds it.
- A test over the committed graph asserts that every external reference matches the product graph's
  (offset, length). A changed weights file then fails the build, not a user's session.

**Sessions.** There is one CoreML session per length bucket: 256, 512, 768 and 1024 tokens. Every
shipped chunk budget plus [CLS]/[SEP] (256, 512, 1024) is one of these buckets, so no bundled row
falls outside them. Rows run at batch 1, padded to their bucket. A row longer than 1024 tokens
(outside the bundled chunk budgets) runs on a separate CPU session instead of a bucket. Each
session is configured with:
- free-dimension overrides for `batch_size`, `sequence_length` and the attention mask's own
  `total_sequence_length`, plus `RequireStaticInputShapes=1` (the plan review's ask, closing off any
  silent fallback to a dynamic shape);
- `ModelFormat=MLProgram` and `MLComputeUnits=CPUAndNeuralEngine`;
- `session.intra_op.allow_spinning=0` and `inter_op.allow_spinning=0`;
- its own `ModelCacheDirectory` at
  `<data-root>/coreml-cache/<model sha12>/<ORT version>/CPUAndNeuralEngine/bucket-<n>/`. A cache
  directory shared across shapes serves the wrong compiled model and crashes (ADR-0117 context).

**The first compile runs in the background, and WebGPU serves until it finishes.** A cold compile
takes 9-28 s per bucket depending on load (F5), about 1-2 minutes for all four. The engine is a
small state machine, and it records the trigger of every move:
- `WebGpuServing → CompilingNeuralEngine` on start.
- `CompilingNeuralEngine → NeuralEngineServing` once all four bucket sessions have loaded and a
  parity probe passes. The probe compares the CLS vector from the ANE and from WebGPU on one fixed
  row, and needs cosine ≥ 0.999. The WebGPU session is disposed at this transition.
- `CompilingNeuralEngine → WebGpuServing (refused)` on any load failure, timeout or probe failure.
  The reason is logged and shown by `doctor`, and there is no retry until restart.

With a warm cache the switch takes seconds. No request ever waits on a compile.

**The fingerprint does not change.** ANE vectors match CPU vectors (CLS cosine ≥ 0.999999 on
CoreML, F7), inside the parity bar that WebGPU and MLX already meet. No bank re-embeds when the
device changes, in either direction.

**Cache housekeeping.** The cache costs about 200-210 MiB per bucket, 808 MiB for four. When a new
set finishes compiling, directories for any other model sha or ORT version are deleted. `doctor`
reports the cache size, and switching away from `coreml` keeps the cache, so switching back is
warm. A cache directory that fails to load, for example one left half-written by a compile killed
at restart, is deleted and recompiled once. It must never crash the session.

## Switching devices

A device change takes effect on the next server restart (`settings model device`, unchanged). Every
ordered pair of values must work across that restart: `auto`, `gpu`, `cpu`, `mlx`, `cuda` and
`coreml`, in both directions. That includes switching during a background compile, and switching to
a device the platform refuses, which falls back with a reason. "Work" means:
- the new session reports the provider the setting asked for, or a recorded refusal;
- vectors stay within the parity bar of the previous device's, and no bank re-embeds;
- nothing from the previous device survives into the new session. This covers the MLX cache limit,
  WebGPU's `GpuGate`, CoreML sessions and threads, and a partial CoreML cache.

The proof is layered:
1. **Unit:** the setting's decisions (`Parse`, `PrefersGpu`/`PrefersMlx`/`PrefersCoreMl`, platform
   refusal) for every value on every platform.
2. **In-process, macOS arm64:** build the bundled generator once per device in several orders,
   including `coreml → mlx → gpu → cpu → coreml` and `mlx → coreml → mlx`, disposing each before the
   next. Assert the provider and cross-device CLS cosine ≥ 0.999. This is where state leaking
   between sessions shows up.
3. **Across restart:** a manual-checklist row walks the full ordered matrix on a live install
   (`settings model device X`, restart, `doctor`, one search), including a restart in the middle of
   a CoreML compile.

## Before it can become the default

`auto` switches to `coreml` on osx-arm64 only through an amendment to this ADR or a new ADR, and
only after:
1. **A second chip passes.** `coreml_ab.py` (MLX reference against ANE step256) and the four recall
   arms are re-run on an M1 or M2, or an M3 if that is what is available, against ADR-0117's three
   thresholds.
2. **It beats WebGPU in the product.** An A/B of `device coreml` against `auto` (WebGPU), using the
   product's own method (bank copies, `ps` CPU-seconds, ADR-0110). `coreml` must win on CPU-seconds
   by range separation and must not lose on p95 latency.
3. **It holds up in real use.** A release cycle of opt-in use with no open issue against the path.

## Consequences

- Users who opt in move their embedding work to the Neural Engine, at roughly an order of magnitude
  less CPU per row than MLX. Nobody else sees a change.
- First run after opting in, or after an upgrade: WebGPU serves for about 1-2 minutes while the ANE
  sessions compile. After that, loading takes seconds.
- The package grows by 5.7 MB. Users who opt in also carry about 808 MiB of compiled cache in the
  data root.
- Memory is 0.49-0.57 GiB phys+neural, counted on its own ledger (`ri_neural_footprint`). The WebGPU
  session is also resident while the compile runs.
- This path runs at batch size 1, so its throughput comes from the Neural Engine, not from batching.
  The bucket step is 256, not MLX's 64: finer buckets cost more cache and memory than they save (F3).
- An ORT upgrade changes the cache key, so the first start after it recompiles in the background.
- A minor version bump, a README "What's new" line, a how-to row for `device coreml` and a manual
  checklist row ship with the implementation.

## Alternatives considered

- **Make it the Apple Silicon default right away.** This was the first draft of this ADR. It was
  deferred because only one chip has been measured and the win over WebGPU is still inferred. The
  default flip is kept as a gated follow-up.
- **Compile synchronously on the first row of each bucket.** Simpler, but the first search or ingest
  would stall 9-28 s for each bucket not yet compiled.
- **Ship the standalone ANE graph with its own weights.** Rejected: it adds 98 MB, against 5.7 MB
  for the shared graph (F7).
- **Download the ANE graph on demand.** It is published as
  `Arraasz/granite-embedding-small-english-r2-ane`, but that adds a network dependency to save
  5.7 MB. Not worth it.
- **64-token buckets, like MLX.** Rejected: 3.1 GiB of cache and 1.6-1.7 GiB peak footprint fail two
  of ADR-0117's gates (F3).

## Related decisions

- [ADR-0117 — The CoreML execution provider stays out](0117-coreml-execution-provider-stays-out.md):
  this ADR supersedes its decision on acceptance. Its re-open thresholds are the gates used here.
- [ADR-0108 — One bundled engine, GPU first](0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md):
  `auto` stays WebGPU on macOS, and WebGPU is the fallback while the ANE compiles.
- [ADR-0110 — Opt-in MLX](0110-opt-in-mlx-execution-provider-for-the-bundled-engine.md): the
  opt-in, refusal-with-reason pattern this device follows.
- [ADR-0114 — MLX buckets and cache cap](0114-mlx-length-buckets-and-a-capped-buffer-cache.md): MLX
  is unchanged.
