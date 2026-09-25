# 0118 — The bundled engine runs on the Neural Engine by default on Apple Silicon

Date: 2026-09-26

Status: Proposed. It becomes Accepted when the owner approves it and when the second-chip run in
Gate 1 below has passed. On acceptance it supersedes [ADR-0117](0117-coreml-execution-provider-stays-out.md)'s
decision.

Research: `docs/work/2026-09-25-ane-layout-reexport.md` (F1-F7), following
`docs/work/2026-09-25-coreml-ane-buckets-and-residency.md`.

## Context

ADR-0117 kept the CoreML execution provider out. The shipped graph ran on it at 7-18x MLX's CPU
cost, and it named one lever that could change that: a re-export in Apple's `ml-ane-transformers`
layout. That re-export now exists (#760, #767) and has been measured on an M4:

- **Placement.** It loads as one CoreML partition, with 1401-1405 of about 1407 ops on the Neural
  Engine (F2). It has to be fp16, because fp32 graphs place no ops on the Neural Engine at all (F1).
- **Cost against MLX** (MLX ×64 buckets, 512 MiB cap, 3 threads), with 256-token buckets. It uses
  6-8% of MLX's CPU-seconds, and every repeat was below every MLX repeat. Peak phys+neural memory is
  0.49-0.57 GiB and the compiled cache is 808 MiB, so all three ADR-0117 re-open gates clear (F3).
- **Quality.** Recall matches the CPU baseline on all four eval arms (F4).
- **Dispatch.** `powermetrics` shows the Neural Engine rail at 1276 mW while it runs, against 29 mW
  idle (F6).
- **Asset size.** The ANE graph can read the weights file the product already ships, so it adds a
  5.7 MB graph and no second weights copy (F7).

The macOS default today is WebGPU (ADR-0108), not MLX, which stays opt-in (ADR-0110). WebGPU and
this graph have not been measured in one harness. The MLX research record (F26) puts WebGPU with
spinning off at 5.2-8.3 ms of CPU per 512-token embed. The ANE graph's smoke run was about 0.8 ms at
bucket 512. That comparison is INFERRED; Gate 2 measures it directly.

## Decision

**On osx-arm64, `embedding.device auto` runs the bundled engine on the Neural Engine,** through the
CoreML EP and the shared-weights ANE graph. Intel Macs, other platforms and every other device value
are unchanged. `mlx`, `gpu`, `cpu` and `cuda` keep their meanings. A new value, `coreml`, forces the
path explicitly, and `gpu` still means WebGPU. Custom (manifest) models never take this path,
because only the bundled graph has an ANE re-export.

**What ships.** `model_fp16_ane.onnx` (5.7 MB) sits in the bundled model directory, next to
`model_fp16.onnx` and `model_fp16_mlx.onnx`. It reads `model_fp16.onnx_data` at the product graph's
own offsets. It has to live in the same directory: ORT refuses external data reached through a
symlink out of the model directory, and `onnx.load` refuses a hard-linked file (F7). It is built by
`scripts/retrieval_tuning/export_ane_encoder.py --share-weights-with`. A build-time check, or a test
over the committed graph, confirms that every external reference matches the product graph's
(offset, length). That way a changed weights file breaks the build, not a user's session.

**Sessions.** There is one CoreML session per length bucket: 256, 512, 768 and 1024 tokens. Every
shipped chunk budget plus [CLS]/[SEP] (256, 512, 1024) is a bucket, so no bundled row falls
outside them. Rows run at batch 1, padded to their bucket. Each session gets:

- free-dimension overrides for `batch_size`, `sequence_length` and the attention mask's own
  `total_sequence_length`;
- `ModelFormat=MLProgram` and `MLComputeUnits=CPUAndNeuralEngine`;
- `session.intra_op.allow_spinning=0` and `inter_op.allow_spinning=0`;
- its own `ModelCacheDirectory`, under
  `<data-root>/coreml-cache/<model sha12>/<ORT version>/CPUAndNeuralEngine/bucket-<n>/`. A cache
  directory shared across shapes serves the wrong compiled model and crashes (ADR-0117 context).

**The first compile runs in the background, and WebGPU serves until it finishes.** A cold compile
takes 9-28 s per bucket, depending on load (F5), so about 1-2 minutes for all four. The engine is a
small state machine, and each move records its trigger:

- `WebGpuServing → CompilingNeuralEngine`: on start, when the platform qualifies.
- `CompilingNeuralEngine → NeuralEngineServing`: when all four bucket sessions have loaded and a
  parity probe passes. The probe compares the ANE output's CLS against WebGPU's on one fixed row
  and needs cosine ≥ 0.999. The WebGPU session is disposed at this transition.
- `CompilingNeuralEngine → WebGpuServing (refused)`: on any load failure, timeout or probe
  failure. The reason is logged and shown by `doctor`, and nothing is retried until restart.

A warm cache loads in seconds, so after the first run the switch happens almost at once. Requests
never wait on a compile.

**The fingerprint does not change.** ANE vectors match CPU vectors (CLS cosine ≥ 0.999999 on
CoreML, F7), inside the parity bar WebGPU and MLX already meet, so no bank re-embeds.

**Cache housekeeping.** The cache costs about 200-210 MiB per bucket, 808 MiB for four. Directories
for another model sha or ORT version are deleted when the new set finishes compiling. `doctor`
reports the cache size.

## Gates before acceptance

1. **Second chip.** Re-run `coreml_ab.py` (the MLX reference against ANE step256) and the four
   recall arms on one M1 or M2 machine, or an M3 if that is what is available. It passes on the same
   three thresholds as ADR-0117. If it fails there, the default becomes M4-and-later only, gated by
   chip, or the ADR is rejected.
2. **WebGPU, in the product.** Once the implementation exists behind `device coreml`, run an A/B
   with the product's own harness (bank copies, `ps` CPU-seconds, per ADR-0110's method) of
   `coreml` against `auto`-today, meaning WebGPU. The default flips only if `coreml` beats WebGPU on
   CPU-seconds by range separation and does not lose on p95 latency.

## Consequences

- The Neural Engine does the embedding work on Apple Silicon. CPU cost per row falls by roughly an
  order of magnitude against MLX, and likely against WebGPU (INFERRED until Gate 2).
- First run after install or upgrade: embeddings run on WebGPU for about 1-2 minutes, until the ANE
  sessions finish compiling. Every run after that: seconds.
- Disk: 5.7 MB more in the package and about 808 MiB of compiled cache in the data root.
- Memory: 0.49-0.57 GiB phys+neural, counted on its own ledger (`ri_neural_footprint`). While the
  compile runs, the WebGPU session is also resident.
- Batch size is 1 on this path; throughput comes from the Neural Engine, not from batching. The
  bucket step is 256, not MLX's 64: finer buckets cost more cache and memory than they save (F3).
- A future ORT upgrade changes the cache key, so the first start after it recompiles in the
  background.
- A release note, a README "What's new" line, a how-to row for `device coreml`, and a minor version
  bump ship with the implementation.

## Alternatives considered

- **Keep WebGPU as the default and make `coreml` opt-in, as MLX is.** Safer, but it leaves an
  order-of-magnitude CPU win unused by default. The background compile with WebGPU fallback removes
  the one user-visible cost that made opt-in look safer.
- **Compile synchronously on the first row of each bucket.** Simpler, but the first search or
  ingest would stall for 9-28 s per new bucket.
- **Ship the standalone ANE graph with its own weights.** Rejected: it adds 98 MB, where the shared
  graph adds 5.7 MB (F7).
- **Download the ANE graph on demand** (it is published as `Arraasz/granite-embedding-small-english-r2-ane`).
  That spends a network dependency to save 5.7 MB. Not worth it.
- **64-token buckets, like MLX.** Rejected: 3.1 GiB of cache and 1.6-1.7 GiB peak footprint, which
  fails two of ADR-0117's gates (F3).

## Related decisions

- [ADR-0117 — The CoreML execution provider stays out](0117-coreml-execution-provider-stays-out.md):
  superseded by this ADR on acceptance; its re-open thresholds are the gates used here.
- [ADR-0108 — One bundled engine, GPU first](0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md):
  WebGPU stays the default everywhere else, and it is the fallback while the ANE compiles.
- [ADR-0110 — Opt-in MLX](0110-opt-in-mlx-execution-provider-for-the-bundled-engine.md) and
  [ADR-0114 — MLX buckets and cache cap](0114-mlx-length-buckets-and-a-capped-buffer-cache.md): MLX
  stays opt-in and is unchanged.
