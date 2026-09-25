# 0117 — The CoreML execution provider stays out

Date: 2026-09-25

Status: Accepted

Research: `docs/work/2026-09-25-coreml-ane-buckets-and-residency.md`

## Context

The CoreML execution provider ships inside the pinned `Microsoft.ML.OnnxRuntime` osx-arm64 native
package already; nothing needs to be added to reach it, only a session that asks for it. ADR-0108
had already looked at it once, in passing, and rejected it on a dynamic-shape probe (305 of 401
nodes, roughly 2x slower than plain CPU). The owner asked for a real evaluation, on its own, as a
possible `embedding.device coreml` option alongside MLX.

Getting there needed two CoreML-specific fixes and one ORT-wide setting the earlier probe never
found: static shapes require a free-dimension override on the attention mask's own dimension name,
not just batch and sequence, or the model refuses to build at all; a compiled-model cache shared
across bucket shapes serves the wrong compiled model back and crashes on the next differently-sized
row; and ONNX Runtime's own thread spinning roughly doubles CPU per row on every device, CoreML
included, unless it is turned off. Fixed, the graph loads, routes 338 of 376 nodes into Core ML
across 25 partitions, and answers at cosine 0.999988-0.999997 against the CPU path. It runs. The
question this ADR answers is whether it is worth running.

## Decision

**No `embedding.device coreml` ships.** MLX stays the only opt-in GPU execution path on macOS
(ADR-0110, bucketed and cache-capped per ADR-0114); everywhere else the existing
CPU-then-WebGPU-then-CUDA chain is unchanged.

**The harness keeps its `coreml` device.** `scripts/src/retrieval_tuning/coreml.py`,
`run_chunk_window_eval.py --device coreml` and `coreml_ab.py` (merged in #756) stay in the repo,
unshipped, as a re-measurement tool rather than being deleted. The next attempt should not have to
re-pay the E1-E3 build cost to ask the question again.

**Re-open conditions**, restated as the concrete thresholds the go/no-go rule already used and
measured against: CoreML's CPU-seconds per row must fall below MLX's ×64-bucket, 512 MiB-cap
reference by range separation (every CoreML repeat faster than every MLX repeat), not overlap;
peak phys+neural footprint must be at most 1.2 GB under the cheapest residency mode that still
clears the recall and latency gates; the compiled cache on disk must stay at or under 1 GB for the
chosen config. None of the four CoreML configurations measured here comes close on any of the three,
though the closest of the three gates to clearing was disk, not CPU cost or footprint (research
record F3).
Concrete triggers worth re-measuring against: an ANE-friendly layout re-export in the style of
`apple/ml-ane-transformers`, or a future ONNX Runtime CoreML EP release that changes how it
dispatches long shapes, since the neural-footprint collapse past 704 tokens (F5) suggests today's
dispatcher is not the last word on this.

## Consequences

- No product code changes ship. `EmbeddingDevice` stays `{default, webgpu, cuda, mlx}`; P1-P3 of
  the plan (the setting, `CoreMlShapes`/`CoreMlSessions`, generator wiring) are skipped, per the
  plan's own E0 early-stop branch.
- No how-to row and no README "What's new" line, since nothing changes for a user of this release.
- The Neural Engine stays unused across the whole product. MLX remains the only GPU acceleration
  path on macOS.
- The harness's `coreml` device is now a maintained but dormant re-measurement tool, not a dead
  end. It costs an afternoon to point it at a re-exported graph or a new ORT release, not a rebuild
  from nothing.
- Follow-up issues worth filing, not built here: the `ml-ane-transformers` conv-layout re-export,
  `powermetrics` confirmation of ANE utilization, and OpenVINO's NPU execution provider on Windows
  or Linux.

## Alternatives considered

- **Dynamic-shape CoreML.** Measured 305 of 401 nodes, roughly 2x slower than plain CPU at 512
  tokens, 1.6-2.1 GB footprint. A straight regression against the CPU baseline it was meant to
  beat; rejected before static shapes were even tried (F1).
- **The `NeuralNetwork` model format instead of `MLProgram`.** Traced only 3 of 401 nodes. Too
  small a fraction of the graph to be worth measuring further.
- **Coarser buckets or an LRU cap on live sessions**, to bring CoreML's memory and disk cost down.
  Measured in F3: the cheapest combination found, 128-token buckets with every session kept live,
  still costs 7.3x-7.7x MLX's CPU-seconds and its own compiled cache alone exceeds the 1 GB disk
  ceiling. An LRU cap lowers memory further but raises CPU cost and latency, since an evicted session has to
  recompile or reload on its next row. Neither tuning direction clears the bar even at its best
  setting.
- **A native `coremltools` plus Objective-C++ shim**, bypassing ONNX Runtime's CoreML EP entirely.
  Not built. It ranked behind the ORT EP path in the NPU research because it needed no new build
  target to try first, and the EP path lost badly enough that the extra effort is not justified now.
- **The `apple/ml-ane-transformers` conv-layout rewrite.** The one lever in this record that could
  plausibly change the verdict, reported at up to 10x for a comparable encoder at short sequence
  lengths. Not attempted here: it needs a new graph export, disproportionate effort for a
  research-only spike, and is named as a re-open trigger instead.
- **Other NPUs, evaluated only on paper (F6).** QNN (Qualcomm, Windows) is blocked by the same
  int8/int16-only, cosine 0.94-0.97 problem ADR-0108 already measured on this graph. Vitis AI (AMD)
  has too narrow a driver window to justify a build with no hardware on hand. OpenVINO's NPU
  execution provider (Intel, Windows and Linux) accepts fp16 and is the one candidate not already
  ruled out on quality grounds, only on not being built yet. WinML's automatic EP selection
  (Windows 11 24H2+) was not attempted for the same reason, no hardware to validate against.

## Evidence

`docs/work/2026-09-25-coreml-ane-buckets-and-residency.md` F2 (E0 go/no-go verdict), F3 (memory
combinations, none beats the MLX reference), F5 (compute-plan vs. runtime neural footprint). Raw
numbers: `docs/work/coreml-ab/e0-go-no-go.json`, `docs/work/coreml-ab/memory-combinations.json`,
`docs/work/coreml-ab/residency.json`, `docs/work/coreml-ab/compute-plan.json`.

## Related decisions

- [ADR-0108 — One bundled engine for memory and code](0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md):
  its own CoreML alternative note (dynamic-shape probe, about 3x slower than CPU) is the prior
  context this ADR replaces with a full measurement, and its int8-on-GPU cosine finding is why QNN
  is ruled out here too.
- [ADR-0110 — An opt-in MLX execution provider for the bundled engine](0110-opt-in-mlx-execution-provider-for-the-bundled-engine.md):
  MLX stays the macOS GPU path this ADR declines to add a second one next to.
- [ADR-0114 — MLX rows pad to 64-token buckets, and MLX's buffer cache is capped at 512 MiB](0114-mlx-length-buckets-and-a-capped-buffer-cache.md):
  the exact configuration this ADR's go/no-go rule measures CoreML against.
