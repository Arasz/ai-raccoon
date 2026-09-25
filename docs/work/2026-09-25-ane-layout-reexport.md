# Research: granite-small re-exported in the Neural Engine's layout, re-measured through CoreML

**Date:** 2026-09-25
**Issue:** #760
**Follows:** `docs/work/2026-09-25-coreml-ane-buckets-and-residency.md` and ADR-0117.
**Question:** ADR-0117 named one lever that could flip its verdict, an `apple/ml-ane-transformers`-style re-export of the bundled encoder. Does that re-export, run through the ONNX Runtime CoreML EP, clear ADR-0117's three re-open gates?

**Answer:** Yes, at 256-token buckets, and by a wide margin on CPU. The fp16 re-export in the Neural Engine layout loads as one CoreML partition, and CoreML places 1401 to 1405 of its roughly 1407 ops on the Neural Engine. Against the MLX reference (64-token buckets, 512 MiB cache cap, 3 threads) it costs 6% to 8% of MLX's CPU-seconds on the same 200 rows. Every repeat was cheaper than every MLX repeat. Its peak phys+neural footprint is 0.49 to 0.57 GiB, and its compiled cache is 808 MiB on disk. Recall matches the CPU baseline to the third decimal on all four eval arms. Two things are still open. The first is a `powermetrics` confirmation of dispatch, which needs `sudo`. The second is whether the product should ship it: that is a decision for the owner, and it carries real costs, listed under F5.

```chart:bars
title: CPU-seconds per 200 rows (100 long + 100 short), mean of 3 repeats
MLX x64 cap512 t3 (ref): 4.22
ANE fp16 step64: 0.30
ANE fp16 step128: 0.33
ANE fp16 step256: 0.27
```

## Findings

### F1: fp32 graphs never reach the Neural Engine; fp16 graphs do [MEASURED]

The first exports kept fp32 weights. `ProfileComputePlan` put zero ops on the Neural Engine for either layout. With `ALL`, every op went to the GPU. With `CPUAndNeuralEngine`, every op went to the CPU. The same module exported in half precision moved 1402 of 1407 ops onto the Neural Engine. The shipped `model_fp16.onnx` is fp16 inside with fp32 inputs and outputs, so the exporter's default matches that: half-precision weights and compute, with a cast to float32 on both outputs.

### F2: the layout, not the flag, decides the partition count [MEASURED]

These are CoreML EP partitions at bucket 512 under ORT 1.30.

| graph | partitions | nodes placed | where the split comes from |
|---|---|---|---|
| bundled `model.onnx` | 25 | 337/375 | contrib `MultiHeadAttention` plus `Neg` |
| plain HF re-export | 13 | 525/549 (fp32), 599/623 (fp16) | the 24 `Neg` ops in HF's `rotate_half` |
| ANE layout, einsum attention | 25 | none of the 288 `Einsum` ops | CoreML EP does not take `Einsum` |
| ANE layout, MatMul attention | **1** | 1405/1405 (fp32), 1407/1407 (fp16) | none |

The plain re-export alone does not get there. In fp16 it still leaves about 40% of its ops on the CPU (NeuralEngine 313, CPU 288 at bucket 512). The ANE layout that works has four features:
- the tensors stay `(B, C, 1, S)` from the embedding norm to the final norm;
- every Linear is a 1x1 `Conv2d`;
- LayerNorm is computed over the channel axis;
- attention is split per head and written as MatMul.

RoPE's `rotate_half` is folded into extra conv output channels, so the graph has no `Neg`. ADR-0117 inferred that the bundled graph's 25 partitions came from its 12 contrib attention ops. That was only part of it: the standard-op re-export still splits 13 ways, on `Neg` alone.

### F3: step256 clears every re-open gate [MEASURED]

The run used `coreml_ab.py`, 3 repeats per config, rotated interleave, seed 20260925, 100 long rows (300-1022 tokens) plus 100 short rows, with a cold cache on repeat 0. The load average was 4.2-7.4 throughout. Raw data: `docs/work/coreml-ab/ane-fp16-ab.json`; configs: `ane-fp16-ab-configs.json`.

| config | CPU-s (min-max) | paired ratio vs MLX | p50 ms | p95 ms | peak phys+neural | cache on disk |
|---|---|---|---|---|---|---|
| MLX x64 cap512 t3 (ref) | 3.58-4.56 | 1 | 30.3-37.6 | 75.7-91.3 | 1.11-1.17 GiB | 0 |
| ANE fp16 step64 | 0.30-0.30 | 0.065-0.085 | 20.5-21.4 | 57.6-59.2 | 1.60-1.68 GiB | 3.1 GiB |
| ANE fp16 step128 | 0.27-0.43 | 0.061-0.121 | 21.5-29.8 | 57.2-58.1 | 0.84-0.95 GiB | 1.6 GiB |
| **ANE fp16 step256** | **0.26-0.29** | **0.057-0.076** | 20.7-22.8 | 57.0-58.0 | **0.49-0.57 GiB** | **808 MiB** |

These are the three gates from ADR-0117:
- **CPU below MLX by range separation.** All three ANE configs pass. Their worst repeat is under a tenth of MLX's best.
- **Peak phys+neural at most 1.2 GB.** step256 and step128 pass. step64 fails at 1.6-1.7 GiB.
- **Compiled cache at most 1 GB.** Only step256 passes.

So step256 is the only config that passes all three. The Neural Engine's memory scales with how many bucket shapes are live: 1.37 GiB of neural footprint at step64, 714 MiB at step128 and 371 MiB at step256. The earlier record saw that footprint collapse to about 8 MiB past 704 tokens. That did not happen here, because the top bucket (1024) keeps 371 MiB of neural footprint and 1405 ops on the Neural Engine.

### F4: recall is unchanged [MEASURED]

This is `run_chunk_window_eval.py` on the four arms, CPU EP with the product graph against CoreML step256 with the re-export. Raw data: `docs/work/coreml-ab/ane-recall/{cpu,ane}/`.

| arm | file R@5 | file MRR@10 | span R@5 | ingest s (CPU EP / ANE) | ingest CPU-s (CPU EP / ANE) |
|---|---|---|---|---|---|
| memory 254 | 1.000 / 1.000 | 0.891 / 0.891 | 0.720 / 0.720 | 95 / 10 | 270 / 2 |
| memory 1022 | 0.973 / 0.973 | 0.931 / 0.931 | 0.827 / 0.827 | 99 / 21 | 318 / 2 |
| code 510 | 0.946 / 0.946 | 0.822 / 0.819 | 0.825 / 0.825 | 124 / 23 | 372 / 2 |
| code 1022 | 0.921 / 0.921 | 0.788 / 0.789 | 0.833 / 0.833 | 140 / 28 | 450 / 2 |

The CPU baseline ran on 4 threads and CoreML on 1. The CoreML arm reused the compile cache that the A/B run had already warmed, so its ingest numbers leave out the one-time compile cost in F5. Per-row parity on the CPU EP, against the product `model_fp16.onnx`, is a CLS cosine of 1.000000 and a minimum token cosine of at least 0.99999 on rows of 12, 31, 300 and 1000 tokens. On CoreML itself, the CLS cosine is 0.999999 at buckets 512 and 1024.

### F5: what shipping it would cost [MEASURED except where marked]

- **A cold compile per bucket.** Each bucket costs 18-28 s wall to compile the first time, which is 37 s of `aned`/`ANECompilerService` CPU for step256's four buckets and 123 s for step64's sixteen. On warm repeats that delta falls to 0.03-0.19 s, so the ANE daemons do little per-row work that the process CPU figure misses.
- **Disk.** At step256 the compiled cache adds 808 MiB for four buckets. The re-exported weights add 97.6 MB, since the MLX and CPU paths still need the shipped graph, so a second fp16 graph would ship next to it.
- **Tail latency.** p95 is about 57 ms, flat across steps, against MLX's 76-91 ms under the same load. Rows past 768 tokens pay the full 1024 bucket.
- **A known fp16 overflow.** On a 1000-token row, the ANE layer norm's centered squares reach about 3.2e6, well past fp16's 65504. The exporter computes the norm's statistics on x/64, and a torch-half test on the 1000-token row goes red without that scaling. CoreML ran the unscaled graph without overflowing (cosine 0.999977), which suggests it does not square in fp16 literally [INFERRED]. The ORT CPU EP can't catch this class of bug either, because it upcasts fp16 graphs.
- **Dispatch is not confirmed by `powermetrics`** [UNVERIFIED]. The evidence for Neural Engine execution is the compute plan (1401-1405 ops on the Neural Engine), a neural footprint of 371 MiB and up, and host CPU per row under 1 ms. `powermetrics` needs `sudo`, which this session did not have.

## Method

Machine: Apple M4, 24 GB. ONNX Runtime 1.30.0, torch 2.13.0, transformers 5.17.0, and the legacy TorchScript exporter at opset 17 (the dynamo exporter needs `onnxscript`, which is not installed). Weights come from `ibm-granite/granite-embedding-small-english-r2`, loaded offline.

The export is `scripts/retrieval_tuning/export_ane_encoder.py --layout ane --dtype fp16 --out <dir> --max-len 1024`. It builds the module in `scripts/src/retrieval_tuning/ane_encoder.py`. The sliding-window band mask and the RoPE tables are precomputed up to `--max-len`, so the graph only slices them. The padding mask is `(1 - mask) * -1e4`. The directory gets `model_fp16.onnx`, its data file, the tokenizer files, and a manifest with recomputed hashes.

The tests are `scripts/tests/test_ane_encoder.py` and `scripts/tests/test_ane_export.py`. They are local gates, skipped when torch or the weights are missing, and they check:
- parity against HF eager and against both shipped graphs;
- sliding window, theta routing, padding, CLS pooling, exact GELU and the RoPE fold, each seen red under its mutation;
- no contrib, `Einsum` or `Neg` ops in the ANE graph;
- the IO signature and the stored weight dtype;
- manifest hashes, and that a re-export does not grow the data file.

The A/B is run with this command:

```
scripts/retrieval_tuning/coreml_ab.py --model-dir src/AiRaccoon/Models/granite-embedding-small-english-r2 \
  --configs ane-fp16-ab-configs.json --out ab.json --coreml-cache-dir <dir> \
  --repeats 3 --long-n 100 --short-n 100
```

Each config's `model_dir` points it at its own graph, which lets MLX run the shipped graph in the same run.

## Still open

- `powermetrics --samplers ane_power` during a step256 run, to confirm dispatch. It needs `sudo`.
- The ship decision. The measurements answer ADR-0117's gates, but shipping means a second fp16 asset of about 98 MB, a compile of 18-28 s per bucket on first use, 808 MiB of cache, and a bucket policy of 256 and not MLX's 64. That trade belongs in its own ADR with the owner's go-ahead, not in this research PR.
- Whether the MLX path would also gain from the Neg-free, per-head graph. Not measured.
