# 0113 — MLX rows pad to 64-token buckets, and MLX's buffer cache is capped at 512 MiB

Date: 2026-09-25

Status: Accepted

Research: `docs/work/2026-09-25-chunk-size-vs-attention-window-mlx.md`,
`docs/work/2026-09-25-mlx-cache-limit-and-buckets.md`

## Context

ADR-0110 runs the bundled engine on the onnxruntime MLX plugin when `embedding.device` is `mlx`.
The session runs one row per call, so each call's shape is that row's token count. MLX compiles and
allocates per shape, and it keeps freed buffers in a cache that may grow to its memory limit
(23,347 MiB on a 24 GB M4). A real corpus has hundreds of distinct chunk lengths. Measured with the
product's graph, a memory ingest at 1022-token chunks ended with 92 MiB of live memory and about
17,180 MiB of cached buffers, a 17.9 GB process footprint. At the shipped 510-token code budget it
was 10.6-11.8 GB. RSS does not show it (under 1 GB). The footprint does, and so does Activity
Monitor. A live server with `device mlx` sat at 4.6 GB during the measurement.

The plugin has no option for this: its documented environment variables are diagnostic, and
`ONNXRUNTIME_EP_MLX_NO_COMPILE` keeps the cache and makes every row 2.5× slower. But the osx-arm64
package already ships MLX's C library, `mlx/libmlxc.dylib`. The plugin loads it from the same
folder, so a call to its `mlx_set_cache_limit` changes the allocator the plugin uses.

Two levers were measured on 400 rows of 300-1022 tokens (warm, three interleaved repeats):

| cache cap | padding | row p50 ms | CPU s | peak footprint MiB |
|---|---|---|---|---|
| none | none (before this ADR) | 51-68 | 17.8-20.7 | 17,813-17,911 |
| none | ×64 | 36-46 | 2.1-2.5 | 3,570-3,649 |
| 0 | none | 39-56 | 5.9-12.0 | 1,038-1,181 |
| 512 MiB | ×64 | 35-37 | 5.2-5.4 | 1,144-1,190 |

Padding a row with masked tokens up to the next multiple of 64 leaves the vector unchanged (cosine
≥ 0.999998 against the unpadded CPU vector, recall within ±0.001 on every eval arm), and turns
~300 shapes into at most 16 up to 1024 tokens. A cap bounds the cache no matter how many shapes
there are. It costs CPU, because MLX frees and re-allocates Metal buffers on each run (about 13 ms
against 6 ms per row), but not latency.

## Decision

**An MLX session pads every row to the next multiple of 64 tokens, never past the model's window.**
`LengthBuckets.PaddedLength` (Core, pure) computes the length. `OnnxEmbeddingGenerator.RunBatch`
already pads a batch's shorter rows with id 0 and attention mask 0. For an MLX session it now pads
to the bucket the same way. Every shipped chunk budget plus [CLS]/[SEP] (256, 512, 1024) is itself
a multiple of 64, so a full chunk pads nothing, and the "window top" bucket the research proposed
comes out of the same rule. CPU, WebGPU and CUDA sessions run rows at their own length as before.
On the CPU, padding would only add work.

**The MLX session caps MLX's free-buffer cache at 512 MiB once it is created.** `MlxCacheLimit`
loads `libmlxc.dylib` from the plugin folder with `NativeLibrary.TryLoad` and calls
`mlx_set_cache_limit` on the MLX session's dedicated thread. It logs the new and previous limits
(event 434). Anything that goes wrong (the library will not load, the export is missing, MLX
returns an error) is logged as event 435, and the session runs uncapped. The cap never refuses a
session. `OnnxEmbeddingGenerator.MlxCacheLimitApplied` says which happened. 512 MiB is the
measured sweet spot: 256 MiB saves ~200 MiB for no CPU gain, and 1 GiB costs ~500 MiB for ~0.3 s
less CPU per 400 rows.

**The engine fingerprint does not change.** Padded vectors match the unpadded ones, so no bank
re-embeds.

## Consequences

- A `device mlx` server's footprint stays near 1.2 GB during a re-embed instead of growing toward
  18 GB on a 24 GB machine. Latency per row is unchanged.
- MLX CPU per row roughly doubles (about 6 ms to 13 ms), which is about 2.5 CPU-seconds on a
  383-chunk memory ingest. Padding without a cap would keep CPU lowest but hold ~3.6 GB. This ADR
  chooses memory.
- The cap is process-wide in MLX's allocator. One process runs at most one MLX session per engine,
  so that is the intent, not a side effect.
- MLX occasionally aborts in its static destructors at process exit (`recursive_mutex lock failed`,
  2 of about 70 harness processes). Whether the server's shutdown hits it is unverified. This ADR
  neither causes nor fixes it.
- Chunk budgets that are not 2 below a multiple of 64 (a 128-token budget pads 130 to 192) waste
  more. None ships.

## Evidence

`docs/work/2026-09-25-mlx-cache-limit-and-buckets.md` F1-F4 (cache vs active memory, the A/B table,
bucket waste per scheme) and `docs/work/2026-09-25-chunk-size-vs-attention-window-mlx.md` F2-F3
(footprint growth per distinct length, padded parity).
`tests/AiRaccoon.Tests/Integration/Embedding/BundledEngineBucketPaddingTests.cs` pins padded-vs-unpadded
parity, and `BundledEngineMlxSessionTests` checks the cap on a real MLX session where the plugin is
present.

## Related decisions

- [ADR-0110 — An opt-in MLX execution provider for the bundled engine](0110-opt-in-mlx-execution-provider-for-the-bundled-engine.md):
  this ADR bounds the memory of the session that ADR introduced.
