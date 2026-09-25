# Research: MLX cache limit, length buckets, and a 766-token middle arm

**Date:** 2026-09-25
**Follows:** `docs/work/2026-09-25-chunk-size-vs-attention-window-mlx.md` (PR #738) and `docs/qa-sessions/mlx-padding-memory-vs-equal-length-rows.md`.
**Questions:** (1) How does a 766-token arm (768 with [CLS]/[SEP]) do for both corpora, as a middle ground between 510 and 1022? (2) What are length buckets here, and can their distribution be improved? (3) Can the onnxruntime MLX plugin cap MLX's cache, and would that bound memory without padding? (4) Are 32-token buckets a better trade than 64-token ones?

**Answer:** The 17.9 GB footprint in the first MLX run was MLX's free-buffer cache, not live memory: 17,180 MiB cached against 92 MiB active at the end of a 766 or 1022 arm. The plugin has no option for it, but `mlx_set_cache_limit` from the `libmlxc.dylib` shipped beside the plugin controls it in the same process. With the limit at 0 every arm stays at 0.4-1.0 GB with no padding. That costs CPU, not latency: a capped cache frees and re-allocates Metal buffers on every run, which roughly doubles CPU per row (about 13 ms against 6 ms). The cheapest configuration measured is 64-token buckets with a 256-512 MiB cache limit: 0.9-1.2 GB peak, per-row p50 unchanged. 32-token buckets are the worse trade. They halve pad waste (2-7% against 4-12% at 254-1022) but double the shape count, and with an uncapped cache that nearly doubles the footprint (6.6 GB against 3.7 GB at 1022). The 766 arm does not earn its place. Memory at 766 gets about half of 1022's section gain (+0.069, interval −0.005..+0.146, against +0.166 at 1022), and 1022 beats 766 outright (+0.097, +0.036..+0.164). Code at 766 is tied with 510 on file MRR (−0.017, −0.048..+0.013), and targets past token 512 again fall to 0.70. Config D (memory 1022, code 510) stays the pick.

```chart:bars
title: peak footprint MiB at memory 1022, by cache and padding
no cap, no padding: 17901
no cap, x32: 6595
no cap, x64: 3673
cap 512 MiB, no padding: 1527
cap 0, no padding: 995
cap 0, x64: 842
```

## What a bucket is here

The model runs one row per call, and a row's shape is its token count. MLX compiles and allocates per shape, so every distinct chunk length costs a compile and a set of buffers. A bucket is a fixed length that rows are padded up to with `[PAD]` and a zero attention mask. The vector does not change (cosine ≥ 0.999998, PR #738 F3), but the shape does: at 1022 tokens, 273-301 distinct chunk lengths become 15-16 shapes with 64-token buckets. The price is compute on the pad tokens, which run through every layer like real ones.

## Findings

### F1: The footprint is MLX's free-buffer cache, and `mlx_set_cache_limit` caps it [MEASURED]

In a probe of 100 rows of 100 different lengths (723-1020), the default run ends with 92 MiB active and 17,170 MiB cached (footprint 17.5 GB). The cache limit defaults to MLX's memory limit, which is 23,347 MiB on this machine. `mlx_set_cache_limit(0)` ends the same probe at 409 MiB (peak 763), 512 MiB ends it at 922 MiB, and `mlx_clear_cache()` after every run ends it at 719 MiB. Across the full sweep, a zero limit gives 421-995 MiB for memory arms and 433-1,035 MiB for code arms with no padding, recall unchanged. MLX's own peak (active high-water) is 147-699 MiB and follows the longest row. That is the real working set. The plugin's own switches do not help: `ONNXRUNTIME_EP_MLX_NO_COMPILE=1` keeps the 17 GB cache and makes each row 2.5× slower, and `ONNXRUNTIME_EP_MLX_NO_STABLE_CROSS_CACHE=1` changes nothing.

**Evidence:** `mlx_memory_mib` (active, cache, peak) and `footprint_kib_peak_total` in `docs/work/chunk-window-mlx-cache0/`, `-cache512/`, `-pad64-cache0/*.json`. The probe output is in the PR description. `nm -gU libmlxc.dylib` lists `mlx_set_cache_limit`, `mlx_clear_cache` and `mlx_get_{active,cache,peak}_memory`. The plugin README (github.com/justinchuby/onnxruntime-mlx) lists only diagnostic environment variables, and MLX's `set_cache_limit` docs state the default.

### F2: A capped cache costs CPU, not latency [MEASURED]

400 rows of 300-1022 tokens, compiled once and then timed, three interleaved repeats:

| cache limit | padding | row p50 ms | CPU s (400 rows) | peak footprint MiB |
|---|---|---|---|---|
| default | none | 51-68 | 17.8-20.7 | 17,813-17,911 |
| 0 | none | 39-56 | 5.9-12.0 | 1,038-1,181 |
| 512 MiB | none | 37-49 | 5.7-11.9 | 1,571-1,646 |
| default | ×64 | 36-46 | 2.1-2.5 | 3,570-3,649 |
| 0 | ×64 | 37-44 | 5.6-11.2 | 661-813 |
| 256 MiB | ×64 | 35-39 | 5.3-5.6 | 920-1,026 |
| 512 MiB | ×64 | 35-37 | 5.2-5.4 | 1,144-1,190 |
| 1 GiB | ×64 | 35-38 | 4.7-5.2 | 1,656-1,661 |

Latency is the same once padding is on. CPU per row goes from about 6 ms to about 13 ms under any cap below the ~3.6 GB the 16 bucket shapes want. The unpadded, uncapped default is the worst on all three columns, because the cache churns through 17 GB of buffers. For a 383-chunk memory ingest the cap adds about 2.5 CPU-seconds.

**Evidence:** `ab.py` probe, same host, in the PR description.

### F3: 32-token buckets halve pad waste but double the shapes [MEASURED]

Chunk tokens added by padding (from the chunk-length distributions, chunks only):

| arm | ×64 shapes | ×64 waste | ×32 shapes | ×32 waste | ×16 waste | fitted 8 buckets | fitted 16 buckets |
|---|---|---|---|---|---|---|---|
| memory 128 | 3 | 18.2% | 5 | 12.3% | 6.8% | 3.1% | 1.4% |
| memory 254 | 4 | 9.4% | 8 | 6.3% | 3.3% | 4.2% | 2.0% |
| memory 510 | 8 | 6.3% | 16 | 3.6% | 1.9% | 5.3% | 2.5% |
| memory 766 | 12 | 4.7% | 23 | 2.7% | 1.3% | 5.6% | 2.7% |
| memory 1022 | 15 | 3.8% | 30 | 2.0% | 1.0% | 5.3% | 2.6% |
| code 128 | 3 | 21.0% | 5 | 12.4% | 6.8% | 4.4% | 2.1% |
| code 254 | 4 | 11.9% | 8 | 6.7% | 3.5% | 4.7% | 2.3% |
| code 510 | 8 | 6.6% | 16 | 3.6% | 1.7% | 4.7% | 2.3% |
| code 766 | 12 | 4.8% | 24 | 2.5% | 1.2% | | |
| code 1022 | 16 | 3.6% | 32 | 1.8% | 0.9% | 4.5% | 2.1% |

With the default cache, footprint scales with shapes: ×32 peaks at 6.6 GB at 1022 against 3.7 GB for ×64, and at 3.6 GB at 766 against 2.1 GB. Pad waste is GPU time, and F2 shows GPU time is not the bottleneck on this path. The A/B did not include ×32, and the sweep's timings are too noisy to compare it with ×64. So ×32 buys a few percent of GPU work and costs memory unless the cache is capped. Under a cap, bucket count no longer drives memory and ×32 costs about as much as ×64.

**Evidence:** `docs/work/chunk-window-mlx-pad32/*.json` against `-pad64/`. The waste table comes from the chunk ports and the bundled tokenizer over the same corpora (`dist.py`, `schemes.py` in the PR description). "Fitted" buckets are the per-arm optimum from dynamic programming over the actual lengths. That is a lower bound, not a product setting.

### F4: The distribution sits against the budget, so the top bucket matters most [MEASURED]

Chunkers pack up to the budget, so lengths pile up just under it: at 1022 the memory median is 817 and the p75 972, and at 510 they are 435 and 496. At 510, 766 and 1022 the window top (budget + 2 = 512, 768, 1024) is already a multiple of 64, which is why waste there is only 4-7%. At 128 it is not: most chunks are 114-130 tokens, and the 129-130 ones pad to 192. That is where ×64 wastes 18-21%. Two cheap improvements follow. First, add the exact window top as a bucket (budget + 2), which drops 128's waste to 14-19% at no cost elsewhere. Second, use finer buckets near the top and coarser ones below, because the tail is thin. The fitted 8-bucket sets reach 3-6% waste with half of ×64's shapes at 1022. The gain is a few percent of GPU time, and on MLX that time is already cheap (F2). It is not worth a corpus-specific scheme.

### F5: 766 gives memory half of 1022's gain and code nothing [MEASURED]

Memory at 766: file MRR 0.920, section MRR 0.619 (vs 254: +0.069, −0.005..+0.146). 1022 beats it on both (file +0.028, +0.001..+0.061; section +0.097, +0.036..+0.164). Code at 766: file MRR 0.803 (vs 510: −0.017, −0.048..+0.013), span 0.670 (−0.001). Inside a 766 code chunk, targets at token 512 or later reach the top 5 at 0.70 (n=30), against 0.79-0.84 earlier. That matches 1022's late-target loss. Resources: with ×64 padding and a zero cap, memory 766 peaks at 527 MiB and code 766 at 551 MiB, against 842 and 409 MiB for D's memory 1022 and code 510. Index sizes are 3,156 KiB (memory, 517 chunks) and 4,076 KiB (code, 730 chunks).

**Evidence:** `docs/work/chunk-window-mlx/memory-766.json`, `code-766.json` and the variant directories, `chunk_window.paired_bootstrap`.

### F6: MLX can abort at process exit [MEASURED, cause INFERRED]

Two of about 70 MLX processes ended with `libc++abi: terminating … recursive_mutex lock failed: Invalid argument` after its work was done. The same abort fires at once if `mlx_detail_compile_clear_cache` is called from Python. It looks like MLX's static destructors racing the plugin's teardown. The harness now exits with `os._exit` once an arm's result is written. Whether the product's shutdown hits it (its MLX session is disposed on a dedicated thread, ADR-0110) is unverified.

## Recommendation

Keep config D (memory 1022, code 510). On the product's MLX path, pad rows to 64-token buckets with the window top as the last bucket, and set `mlx_set_cache_limit` to 512 MiB once after the plugin loads (P/Invoke into the `mlx/libmlxc.dylib` the osx-arm64 package already ships). Measured here, that holds the footprint near 1.2 GB instead of 17.9 GB, keeps latency, and costs about 7 ms more CPU per row. If the extra CPU matters more than the memory, padding without a cap gives 3.6 GB and the lowest CPU. Both belong in #739.

## Method

Same harness, engine and machine as the MLX record (`--device mlx --threads 3`), plus `--pad-to 32`, `--cache-limit-mib {0,512}` and `--buckets`. Six variants × ten arms (128, 254, 510, 766, 1022 per corpus). Three other agent sessions were busy on the machine during the sweep, so the sweep's wall and CPU times are noisy (for example memory-128 at ×32 took 79 s against 23 s at ×64). The CPU claims above come from the interleaved A/B probe, not the sweep. The `padded_tokens` field counts query and gold-position embeds too, so the chunk-only waste figures come from the offline table.

## Still open

- Measure the product (not the harness) with padding plus a 512 MiB cap, and check its shutdown for the F6 abort.
- Check whether the WebGPU path (`device auto`) has the same cache behavior.
