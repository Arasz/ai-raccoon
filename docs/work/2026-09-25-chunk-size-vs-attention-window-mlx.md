# Research: chunk size vs the attention window, re-run on MLX

**Date:** 2026-09-25
**Question:** `docs/work/2026-09-24-chunk-size-vs-attention-window.md` measured the chunk-size arms on a 4-vCPU Linux host with no GPU (its F7 left the GPU open). The live bank here runs `embedding.device mlx` with `embedding.threads 3`. How do the same arms behave on that path (Apple M4, onnxruntime MLX plugin EP, ADR-0110)? And what about a fourth configuration: memory at 1022 tokens with code left at 510, which is the expected best trade between recall and resources?

**Answer:** MLX changes the cost picture, not the recall picture. Every arm's recall matches the CPU run to within ±0.007 MRR, so the earlier recall findings (F1, F3, F4) hold unchanged. On MLX, embed time per 1k tokens stays flat from 128 to 1022 tokens. Wall time is small everywhere (16-45 s per corpus) and CPU time is 4-17 s against 293-659 s on the CPU provider. The real cost is memory, and it comes from shapes rather than chunk size. MLX keeps buffers for every distinct sequence length it has run, so the process footprint grows with the number of lengths, capping near 17.9 GB (about 75% of this machine's 24 GB). The product does not pad today, so 1022-token chunks peak at 17.9 GB and 510 at 10.6-11.8 GB, against 0.7 GB at 128. Padding each row to a multiple of 64 tokens gives the same vectors (cosine ≥ 0.999998) and cuts those peaks to 3.7 GB and 1.1 GB. Config D (memory 1022, code 510) keeps the full memory gain (section MRR +0.166, interval +0.078..+0.254) and code's baseline recall. With padding its memory corpus peaks at 3.7 GB.

```chart:bars
title: peak process footprint MiB, MLX, product shape (no padding)
memory 128: 687
memory 254: 2038
memory 510: 10654
memory 1022: 17901
code 510: 11839
code 1022: 17924
```

```chart:bars
title: peak process footprint MiB, MLX, rows padded to a multiple of 64
memory 128: 386
memory 254: 476
memory 510: 1071
memory 1022: 3673
code 510: 1079
code 1022: 3682
```

## The four configurations

Deltas are against the MLX baseline row of the same corpus. The two footprint columns are the product's current shape (no padding) and 64-token padding. Recall is identical between the two within ±0.001.

| config | corpus | file R@5 | file MRR@10 | span R@5 | span MRR@10 | ingest wall s (pad64) | ingest CPU s (pad64) | peak footprint MiB | peak footprint MiB pad64 | index KiB | query ms p50 |
|---|---|---|---|---|---|---|---|---|---|---|---|
| A baseline | memory 254 | 1.000 | 0.899 | 0.733 | 0.550 | 17 | 6 | 2,038 | 476 | 5,324 | 6.3 |
| A baseline | code 510 | 0.946 | 0.819 | 0.825 | 0.671 | 23 | 6 | 11,839 | 1,079 | 4,572 | 3.9 |
| B equalize | memory 510 | 0.987 | 0.890 | 0.800 | 0.560 | 16 | 4 | 10,654 (+423%) | 1,071 (+125%) | 3,540 (−34%) | 3.6 |
| B equalize | code 510 | unchanged | | | | | | | | | |
| C both 2× | memory 1022 | 0.973 | 0.948 | 0.827 | 0.716 | 18 | 4 | 17,901 (+778%) | 3,673 (+672%) | 2,728 (−49%) | 3.5 |
| C both 2× | code 1022 | 0.921 | 0.788 | 0.833 | 0.644 | 27 | 5 | 17,924 (+51%) | 3,682 (+241%) | 3,524 (−23%) | 5.1 |
| **D memory 2×** | **memory 1022** | 0.973 | 0.948 | 0.827 | 0.716 | 18 | 4 | 17,901 (+778%) | 3,673 (+672%) | 2,728 (−49%) | 3.5 |
| **D memory 2×** | **code 510** | unchanged from A | | | | | | | | | |
| extra | memory 128 | 1.000 | 0.890 | 0.747 | 0.520 | 23 | 10 | 687 | 386 | 9,856 | 14.8 |
| extra | code 128 | 0.950 | 0.833 | 0.771 | 0.599 | 27 | 12 | 693 | 396 | 10,992 | 10.4 |
| extra | code 254 | 0.938 | 0.823 | 0.833 | 0.652 | 22 | 8 | 1,966 | 479 | 6,688 | 11.1 |

Ingest seconds are from the padded run. The unpadded run's timings depend on which shapes the system's Metal cache already held (F4). Query latency is the unpadded run's.

## Findings

### F1: MLX reproduces the CPU recall on every arm [MEASURED]

The paired difference MLX minus CPU on the same arm lies within −0.020..+0.007 for file and span MRR@10 on all eight arms, with means from −0.007 to +0.002. The 1022 arms are identical on both metrics. The chunk-size effects from the CPU record come out the same: memory 1022 vs 254 gives span MRR +0.166 (+0.078..+0.254), code 1022 vs 510 gives file MRR −0.031 (−0.064..−0.000). Inside a 1022 code chunk, targets that start at token 512 or later still reach the top 5 at 0.724 against 0.85-0.91 for earlier buckets. Equalizing memory at 510 still moves nothing (span +0.010, −0.057..+0.078).

**Evidence:** `docs/work/chunk-window-mlx/*.json` against `docs/work/chunk-window/*.json`, `chunk_window.paired_bootstrap` (4,000 resamples, seed 1).

### F2: Memory grows with distinct sequence lengths, not with chunk size [MEASURED]

Running one 1022-token row 300 times holds the footprint at 676-679 MiB. Running 300 rows of 300 different lengths (723-1022) grows it to 9.4 GB after 50 and 17.5 GB after 100, where it levels off. At 213-512 tokens it climbs steadily to 15.6 GB over 300 lengths. The harness arms show the same thing: 122 distinct lengths peak at 0.7 GB (128), 233 at 2.0 GB (254), 315-360 at 10.6-11.8 GB (510) and 292-346 at 17.9 GB (1022). The plateau near 17.9 GB is about 75% of the machine's 24 GB, which fits MLX's default memory limit (its buffer cache keeps freed buffers until that limit), but we did not read that limit directly. The live server (`embedding.device mlx`) showed a 4.6 GB footprint with 845 MB of it tagged IOAccelerator while this run was going, so the product builds up the same state.

**Evidence:** `footprint_kib_*` and `distinct_lengths` in `docs/work/chunk-window-mlx/*.json`. Probe script and `footprint <pid>` output are in the PR description. `process_memory.memory_kib` reads `proc_pid_rusage` (`phys_footprint`, `lifetime_max_phys_footprint`), the figure Activity Monitor shows. It counts Metal buffers in unified memory, and RSS does not (RSS peaked at 0.4-0.8 GB).

### F3: Padding rows to 64-token buckets gives the same vectors and bounds memory [MEASURED]

Padding with `[PAD]` (50283) and a zero attention mask up to the next multiple of 64 matches the unpadded CPU vector at cosine 0.999998-0.999999 (lengths 100, 300, 700, 1000). Across all eight arms padded recall is within ±0.001 of unpadded. The shape count falls to 3-16 per corpus, and peak footprint falls to 386-476 MiB at 128-254, about 1.1 GB at 510 and about 3.7 GB at 1022. Padding costs at most 63 extra tokens per row. Throughput held at 46-57 ms per 1k real tokens.

**Evidence:** `docs/work/chunk-window-mlx-pad64/*.json` (`pad_to: 64`), `scripts/src/retrieval_tuning/padding.py`, tested by `scripts/tests/test_pad_row.py`.

### F4: Each new shape pays a one-time compile, and macOS caches it across processes [MEASURED, cache INFERRED]

Cold, in the first smoke run, a first-seen length cost 165 ms p50 against 60 ms for a repeated one (memory 1022). The first run of the whole probe paid 1,964 ms for its first shape. In the sweep the same "first length" figure varied from 15 to 152 ms, depending on whether an earlier process had already run that length: code-254 (run after memory-254) paid 15 ms, and memory-254 paid 149 ms. The likely cause is the Metal shader cache, which persists per executable. So unpadded ingest times depend on run order and are not quoted as findings. Padding also bounds this cost to at most 16 compiles per process.

**Evidence:** `embed_ms_first_length` and `embed_ms_repeat_length` in the result files. The smoke run is recorded in the PR description.

### F5: Embed cost per token stays flat up to 1022 on MLX [MEASURED]

Padded wall time per 1k real tokens is 54, 46, 49 and 57 ms for memory at 128, 254, 510 and 1022, and 57, 47, 49 and 57 ms for code. On the Linux CPU it rose from 416-419 to 714-756 ms. The 128 arms run fastest per chunk but pay the fixed per-run overhead most often. Ingest CPU time is 4-12 s per corpus, so on this machine chunk size does not decide CPU use.

**Evidence:** `embed_ms_per_1k_tokens`, `ingest_cpu_s` in `docs/work/chunk-window-mlx-pad64/*.json`.

### F6: Query latency is 3-15 ms and does not grow with chunk size [MEASURED]

Query p50 is 3.5-6.3 ms for memory at 254-1022 and 3.9-11.1 ms for code. The 128 arms are slowest (10-15 ms) because they brute-force 3,600-4,300 vectors. Query embed on MLX is 2.7-12 ms.

**Evidence:** `query_ms`, `query_embed_ms` in `docs/work/chunk-window-mlx/*.json`.

## Recommendation

Config D is the best trade on MLX, but only together with bucket padding. Memory at 1022 keeps the +0.166 section MRR gain from the CPU run, and code stays at 510, where it does not lose file MRR. Without padding, the memory corpus alone can take the process to 17.9 GB of footprint on a 24 GB machine. With padding it peaks at 3.7 GB, and CPU time stays under 20 s per full ingest. F4 of the CPU record still applies: part of the memory gain comes from bigger chunks holding more sections, and a top-5 at 1022 hands the agent about 3,700 tokens.

Padding is a product change to `OnnxEmbeddingGenerator`'s MLX path (pad each row to a bucket before `Run`). The engine fingerprint would not change, because the vectors match at cosine ≥ 0.999998. It needs its own PR and a product-level measurement.

## Method

- **Engine:** the installed ai-raccoon 1.50.0 osx-arm64 tool's `Models/granite-embedding-small-english-r2/` (`model_fp16_mlx.onnx` with `model_fp16.onnx_data`) and its `mlx/libonnxruntime_mlx_ep.dylib`, registered through `onnxruntime.register_execution_provider_library` and `add_provider_for_devices`. ORT 1.30.0 Python, `intra_op_num_threads 3` (the bank's `embedding.threads`), `session.intra_op.allow_spinning 0` as in the product. One process per arm, one row per run.
- **Harness:** `scripts/retrieval_tuning/run_chunk_window_eval.py --device mlx --mlx-dir <tool>/mlx --threads 3 [--pad-to 64]`. Corpora, chunker ports, queries and scoring are unchanged from the CPU record.
- **Machine:** Apple M4, 10 cores, 24 GB. Not idle: Rider and the live ai-raccoon server (64-100% CPU for the first minutes, 10% after) ran alongside, so wall and CPU times carry some noise. Recall does not.
- **CPU comparison:** recall only, against the committed `docs/work/chunk-window/*.json`. Cost figures are not compared across hosts.

## Still open

- Read MLX's memory limit and cache size from inside the plugin EP to confirm the 75% plateau (F2).
- Measure the product with bucket padding on the MLX path, including a long-running server's footprint after a full re-embed.
- Check whether the WebGPU path (`device auto`) shows the same per-shape growth.
