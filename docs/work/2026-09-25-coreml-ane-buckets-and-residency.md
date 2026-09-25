# Research: CoreML EP on the Neural Engine, across bucket step and session residency

**Date:** 2026-09-25
**Follows:** `docs/work/2026-09-25-mlx-cache-limit-and-buckets.md` (ADR-0114) and the CoreML EP harness merged in #756.
**Questions:** (1) Can the CoreML execution provider run the bundled model at all, and what had to be fixed first? (2) How does it perform against MLX across memory combinations, bucket step times session residency, on CPU, latency, memory and disk? (3) Does it actually use the Neural Engine? (4) Could we use an NPU anywhere else?

**Answer:** CoreML stays out. The Neural Engine only runs through Core ML, and Core ML's `CPUAndNeuralEngine` path does compile and answer once two CoreML-specific bugs and one ORT-wide setting are fixed: a missing free-dimension override on the attention mask, a compiled-model cache shared across bucket shapes, and ORT's own thread spinning, which doubles CPU per row on every device, not CoreML alone. Fixed, it works. It also loses, badly. Across every memory-residency combination measured, CoreML costs 7.3x to 16.1x MLX's CPU-seconds per hundred rows and at least 2.2x its memory footprint, worse again once the compiled-cache disk cost is counted. The pre-registered E0 rule, written into the plan before a single row ran, called this outcome without ambiguity: every CoreML repeat used more CPU-seconds than every MLX repeat, at both thread counts tried. The residency sweep adds a second reason to stay out. The Neural Engine's own memory footprint climbs to about 288 MiB by 640 tokens, then collapses to about 8 MiB once chunks pass 704 tokens, while the process's physical footprint keeps climbing past 6 GiB at the top bucket. Something is still running those long shapes, and it looks like CPU, not ANE. Windows and Linux NPUs stay unmeasured here; OpenVINO's fp16 NPU path is the one candidate this record has not already ruled out on cosine grounds.

```chart:bars
title: CPU-seconds per 100 long rows, memory-residency configs
MLX x64 cap512 (ref): 1.6
CoreML step128 all-live: 12.2
CoreML step128 LRU4: 20.7
CoreML step64 all-live: 16.7
CoreML step64 LRU4: 26.4
```

## Findings

### F1: two CoreML bugs and one ORT-wide setting stood between here and a working run [MEASURED]

Every naive attempt at running the bundled graph through the CoreML EP either produced CPU in disguise or crashed. `MLProgram` with the static-shape flag alone traced only 8 of 401 nodes into Core ML, because the graph still declared batch and sequence as symbolic dimensions, so everything else fell back to plain CPU regardless of the compute-unit setting. Allowing dynamic shapes did better on node count (305 of 401, 37 partitions) but ran about twice as slow as plain CPU at 512 tokens (roughly 200 ms against 111 ms) and cost 1.6-2.1 GB, a straight regression. Asking for the Neural Engine specifically (`CPUAndNeuralEngine`, `ALL`) with dynamic shapes failed to build at all: `Failed to create MLModel, error: Error in declaring output _model_layers_12_final_norm_layernorm_output_0 with error -1`. The older `NeuralNetwork` format traced 3 of 401 nodes, too small to measure further.

Two CoreML-specific fixes plus one ORT-wide setting got the graph running for real. First, static shapes via ONNX Runtime's free-dimension overrides (`batch_size=1`, `sequence_length=N`, `total_sequence_length=N`) close the build error above, but only once the attention mask's own dimension name is included too. Leaving it out reproduces the identical error -1. Second, the compiled-model cache directory has to be per bucket. A cache shared across shapes serves the wrong compiled model back to a different shape, and `Run()` throws a shape mismatch (`MultiArray shape (1 x 256) does not match the shape (1 x 64) specified in the model description`). Third, and this one is not a CoreML bug at all: ONNX Runtime's own thread spinning roughly doubled CPU per row in an early review probe at 256 tokens (60 ms against 24.7 ms), on every device it runs, CPU and MLX included. Every measurement in this record runs with spinning off, on every device, not only CoreML.

With all three fixed, the graph routes 338 of 376 nodes into Core ML across 25 partitions, at every compute-unit setting tried, and the vectors match the CPU path at cosine 0.999988-0.999997 across four sample inputs.

**Evidence:** this task's baseline probe (session-local, not committed as JSON). The node and partition counts are corroborated in `docs/work/coreml-ab/compute-plan.json` (`coreml_nodes: 338`, `coreml_partitions: 25` at every bucket and compute-unit setting measured).

### F2: E0 go/no-go, CoreML is 9x to 18x MLX's CPU cost, and the pre-registered rule calls it [MEASURED]

| config | CPU-s / 100 rows | p50 ms | p95 ms |
|---|---|---|---|
| MLX x64 cap512 t3 | 1.34-1.53 | 41.9-43.2 | 53-55 |
| CoreML ANE x64 t1 | 14.14-15.18 | 155-160 | 204-247 |
| CoreML ANE x64 t3 | 22.34-24.01 | 121-130 | 180-209 |
| CPU EP t3 | 44.28-48.67 | 180-237 | 248-314 |

Paired within the same rotation, CoreML/MLX CPU-seconds run 9.3-10.8x at one thread and 14.6-17.9x at three.

The plan wrote its stop rule before this table existed: if every CoreML repeat's CPU-seconds beats every MLX repeat's, in either direction, the run decides itself, no judgment call needed. Here it decided against CoreML at both thread counts. The fastest CoreML repeat still cost 14.1 CPU-seconds against MLX's slowest repeat at 1.53. Compiling every bucket the untimed warm-up pass touches costs about 44 CPU-seconds of `ANECompilerService`/`aned` work on the first repeat, outside the measured process itself, dropping to 11-14 s on later repeats once the on-disk cache is warm. That number is a machine-wide, name-summed `ps` delta, not scoped to this one subprocess, so it can pick up compiler work from any other CoreML session sharing the machine, not only this run's. The cost is real, but it happens once per bucket, not once per row, so it does not explain the steady-state gap above. Verdict: RESEARCH-ONLY at both thread counts, and the plan's overall rule (both configs research-only means overall research-only) closes the E0 gate.

**Evidence:** `docs/work/coreml-ab/e0-go-no-go.json` (`verdict.overall`, `paired_within_rotation_ratio_coreml_over_mlx`, `cpu_s_summary`).

### F3: Every memory-residency combination costs more, not less [MEASURED]

| config | CPU-s / 100 rows | p50 ms | p95 ms | peak phys+neural |
|---|---|---|---|---|
| MLX x64 cap512 t3 (ref) | 1.46-1.81 | 41-65 | 54-126 | 1.11-1.13 GiB |
| CoreML step64 all-live | 15.71-17.63 | 168-184 | 251-326 | 5.88-6.12 GiB |
| CoreML step64 LRU4 | 23.62-29.24 | 277-335 | 457-615 | 2.98-3.09 GiB |
| CoreML step128 all-live | 11.22-13.23 | 129-145 | 173-244 | 2.70-2.86 GiB |
| CoreML step128 LRU4 | 16.06-25.32 | 164-246 | 405-632 | 2.56-2.63 GiB |

Per-row latency here includes any session rebuild the row triggers. An earlier run of this sweep started the timer after the session lookup, which left rebuilds out and made the two LRU rows look faster than keeping every session live; that harness bug was fixed and the whole sweep re-run. With rebuilds counted, an LRU cap is slower as well as more CPU-hungry: the step-64 LRU median is 277-335 ms against 168-184 ms with every session live.

None of the four CoreML configurations beats the MLX reference on CPU cost; the JSON's own `beats_reference` field says so for all four. Paired against the same rotation, the cheapest option (128-token buckets, all sessions kept live) still costs 7.3x-7.7x MLX's CPU-seconds; the most expensive (64-token buckets under a 4-session LRU cap) costs 16.1x-16.1x.

Coarsening the bucket step from 64 to 128 tokens is the biggest single lever measured here, and its size depends on residency mode. Under all-live residency it roughly halves memory (6.00 GiB to 2.78 GiB) while cutting CPU cost by about a quarter (16.7 to 12.2 CPU-seconds). The memory win shrinks once a session cap is in place: step64-LRU4 to step128-LRU4 only saves memory from 3.04 GiB to 2.60 GiB, because capping live sessions at four already bounds most of step64's excess before the coarser step gets a chance to help. An LRU cap makes CPU cost and latency worse, not better, at either step, because evicting a session means recompiling it, or at minimum reloading its compiled cache, on the next row that needs it (114 evictions at step 64, 46 at step 128, across 200 embeds - the untimed warm-up plus the timed pass - and 12 or 6 distinct buckets touched).

Memory tells the same story from a different angle. Even the cheapest config costs at least 2.2x the MLX reference's footprint, and touching all 12 buckets in the step-64 all-live run leaves about 2.20 GiB of compiled models on disk, over twice the 1 GB ceiling the plan set for a shipped config. Step-128's own disk cost, 1.10 GiB, already exceeds that same ceiling too, but by the smallest margin of anything measured in this record: about 10% over, against CPU cost missing by 7-9x and footprint by more than 2x. Of the three go/no-go gates, disk is the one that came closest to clearing, and it still didn't.

**Evidence:** `docs/work/coreml-ab/memory-combinations.json` (`summary.beats_reference`, `summary.paired_ratio_over_reference`, `sessions_live_peak`, `evictions`, `cache_disk_kib`).

### F4: The residency curve loads slower and the disk cache never shrinks [MEASURED]

Loading every bucket from 64 to 1024 tokens, one at a time in a single process, against a cold compiled-model cache: bucket 64 loads in 3.66 s (231.6 MiB resident, 182.5 MiB on disk); bucket 256 in 4.05 s (491.0 MiB, 732.0 MiB disk); bucket 512 in 4.23 s (1.07 GiB, 1.44 GiB disk); bucket 768 in 9.93 s, with a 291 ms first run (2.47 GiB, 2.17 GiB disk); bucket 1024 in 14.06 s, with a 528 ms first run (6.20 GiB, 2.91 GiB disk). Touching all 16 buckets costs 112.7 seconds of summed per-bucket load time, and the whole cold pass (load, tokenize, first run, every bucket) takes 118.2 s of wall time - both are wall-clock, not CPU time; `bucket_load_s` times each build with `perf_counter`, not `process_time`. A second process against the now-warm cache is faster for the small buckets, 0.21-0.54 s up to 640 tokens, but still pays 4.5-5.7 s to load each bucket from 704 tokens up, because compiling the long shapes is not as cacheable as the short ones. Warm, the full 16-bucket sweep still costs 34.3 seconds of summed per-bucket load time and 40.9 s of wall time.

The disk cache never shrinks across this sweep, 182.5 MiB with one bucket touched, 2.91 GiB with sixteen. A product that pre-built even a handful of long buckets would carry multiple gigabytes of compiled models on disk permanently, which is most of what the go/no-go rule's disk ceiling exists to catch.

**Evidence:** `docs/work/coreml-ab/residency.json` (`cold.per_bucket`, `cold.total_load_s`, `cold_wall_s`, `warm.per_bucket`, `warm.total_load_s`, `warm_wall_s`).

### F5: the compute plan promises more Neural Engine work as shapes grow; the runtime footprint says the opposite [MEASURED; interpretation INFERRED; dispatch UNVERIFIED]

`ProfileComputePlan` reports Core ML's own static op assignment, and it moves one direction as the bucket grows: at 64 tokens, 219 ops go to the Neural Engine against 121 to CPU; at 512, 229 against 111; at 1024, 327 against only 13. The plan claims the Neural Engine takes over almost the whole graph at the longest shape (MEASURED). A `CPUOnly` control at 512 tokens assigns all 340 ops to CPU, which confirms the plan is reading a real device split, not returning a fixed default.

Runtime memory disagrees. In the residency sweep (F4), the process's neural-footprint counter climbs from about 54 MiB at the shortest bucket to about 288 MiB at 640 tokens, then collapses to about 8 MiB from bucket 704 onward, right where physical memory starts climbing past 2 GiB (MEASURED, both from `residency.json`). This is a separate counter from physical footprint, not a subset folded inside it: a probe taken right after a fresh CoreML session load read neural footprint at 58.3 MB against physical footprint at 42.5 MB (MEASURED) - neural larger than phys is only possible if the two are independent ledgers, which is why F3's footprint metric sums them rather than treating one as already counted in the other. A plan that claims 327 of 340 ops run on the Neural Engine at 1024 tokens should not leave that counter looking nearly idle. The likeliest explanation is that Core ML's static plan is aspirational at these shapes, and the real dispatcher falls back toward CPU once a shape crosses some Neural Engine memory or graph-size limit (INFERRED, this record has not traced the mechanism). Settling it needs `powermetrics`, which reports ANE utilization directly and needs `sudo`. That was not run in this pass, so the dispatch claim stays UNVERIFIED.

**Evidence:** `docs/work/coreml-ab/compute-plan.json` (`results[].compute_plan`) and `docs/work/coreml-ab/residency.json` (`cold.per_bucket[].neural_kib`, `.phys_kib`).

### F6: NPU options off this platform [READ]

macOS has one route to the Neural Engine: Core ML, measured above. Apple's own `apple/ml-ane-transformers` names a specific fix for a BERT-class encoder like this one, rewriting attention to the `(B, C, 1, S)` convolution layout the Neural Engine actually favors, reported at up to 10x for DistilBERT at sequence length 128. That needs a graph re-export and was not attempted here. A native `coremltools` plus Objective-C++ shim ranked below the ONNX Runtime EP path and was never built; the EP path came first because it needed no new build target, and it lost badly enough that the shim is not worth building either, for now.

| Platform | NPU route | Status |
|---|---|---|
| macOS | Core ML (Neural Engine) | Measured here; research-only |
| Windows | QNN (Qualcomm HTP) | Needs int8/int16 and static shapes; blocked by ADR-0108's cosine 0.94-0.97 finding on this graph |
| Windows | Vitis AI (AMD) | Narrow driver window; not attempted |
| Windows, Linux | OpenVINO NPU (Intel) | Accepts fp16 (`Intel.ML.OnnxRuntime.OpenVino` on NuGet); not built |
| Windows | WinML auto EP selection | Win11 24H2+, runtime-downloaded providers; not attempted |
| Linux | Intel NPU | OpenVINO only |

What not to build right now: the native `coremltools` shim (the ONNX Runtime EP path already answers whether Core ML is worth it, and it is not); QNN (blocked on the same int8 cosine problem ADR-0108 already measured); Vitis AI and WinML auto-selection (no hardware on hand to validate against). OpenVINO's NPU path is the one candidate this record has not ruled out on quality grounds, only on not having built it yet.

URLs: https://onnxruntime.ai/docs/execution-providers/CoreML-ExecutionProvider.html ; https://github.com/apple/ml-ane-transformers ; https://machinelearning.apple.com/research/neural-engine-transformers ; https://onnxruntime.ai/docs/execution-providers/QNN-ExecutionProvider.html ; https://onnxruntime.ai/docs/execution-providers/OpenVINO-ExecutionProvider.html ; https://learn.microsoft.com/en-us/windows/ai/new-windows-ml/supported-execution-providers ; https://github.com/microsoft/onnxruntime/issues/32569 ; https://github.com/microsoft/onnxruntime/issues/14455

**Evidence:** `scratchpad/research/npu-lane.md` (this task, a READ pass over vendor docs and the two linked ORT issues); the cosine figures come from ADR-0108.

## Recommendation

Do not ship `embedding.device coreml`. MLX stays the only opt-in GPU path on macOS (ADR-0110, ADR-0114), and the CPU-then-WebGPU chain stays the default everywhere else. Keep the harness's `coreml` device alive rather than deleting it: re-measuring costs an afternoon, not a rebuild, and there are two concrete triggers worth watching for, an `ml-ane-transformers`-style conv-layout re-export of this graph, or a future ONNX Runtime CoreML EP release that changes how it dispatches long shapes. Neither is close enough to schedule against today.

## Method

Machine: Apple M4, 24 GB, macOS 27. Runtime: ONNX Runtime 1.30.0, granite-embedding-small-english-r2 fp16 (the bundled engine). Harness merged in #756: `scripts/src/retrieval_tuning/coreml.py` (length buckets, free-dimension overrides derived from the graph's own input shapes, per-bucket cache directories, the lazy LRU `BucketSessions` cache, and the A/B scheduling and range-separation helpers), `chunk_window.py`'s `Engine(device="coreml")`, `process_memory.py` (RUSAGE_INFO_V6, including `ri_neural_footprint`), plus the two CLIs:

```
scripts/retrieval_tuning/run_chunk_window_eval.py --device coreml \
  --compute-units CPUAndNeuralEngine --pad-to <step> --top-bucket 1024 \
  [--max-sessions N] --coreml-cache-dir <dir> \
  [--profile-compute-plan] [--specialization FastPrediction]

scripts/retrieval_tuning/coreml_ab.py --model-dir <dir> --out <dir> \
  --configs <configs.json> --repeats 3 --long-n 100 --short-n 0 \
  --seed 20260925 --reference mlx-x64-cap512-t3
```

Every repeat runs in a fresh subprocess, interleaved across configs rather than blocked, with spinning off (`session.intra_op.allow_spinning` and `inter_op`, both 0) on every device including CPU. E0 ran 3 interleaved repeats of 100 long rows (memory-1022 chunking, 300-1022 tokens, seed 20260925) under loadavg 2.9-5.9, with 3 `dotnet` and 8 `python` processes competing on the same machine throughout; `docs/work/coreml-ab/e0-go-no-go.json` came from a scratch orchestrator built around `coreml_ab.py`'s `--one-config` entry point, written before this harness's N-config `summarize_ab` summary existed. The memory-combination sweep ran 2 repeats of the same 100-row set under loadavg 3.1-8.7, with 10-11 competing `python` processes, and `docs/work/coreml-ab/memory-combinations.json` came from `coreml_ab.py` itself, in its current N-config form. The residency sweep loaded and ran all 16 buckets, 64 to 1024 tokens step 64, in one process against a cold cache, then again in a fresh process against the now-warm cache. The compute-plan sweep is `ProfileComputePlan` op-per-device counts at buckets 64, 512 and 1024 under `CPUAndNeuralEngine`, plus a `CPUOnly` control at 512.

Every memory figure above is a GiB or MiB computed straight from the harness's own KiB counters, as
KiB/1024² and KiB/1024. An earlier pass of this record approximated gigabytes as KiB/1e6, which
reads a binary (1024-byte) KiB value as if it were decimal and undercounts the true figure by about
2%; this record uses the binary convention throughout instead.

## Still open

- The `apple/ml-ane-transformers` conv-layout re-export: done in #760, and it clears every gate at 256-token buckets. See `docs/work/2026-09-25-ane-layout-reexport.md`.
- `powermetrics` confirmation that the Neural Engine is doing anything at all past bucket 640. Needs `sudo` and was out of scope for this pass.
- OpenVINO's NPU execution provider on Intel Windows or Linux hardware. Not built; no hardware on hand to validate against.
