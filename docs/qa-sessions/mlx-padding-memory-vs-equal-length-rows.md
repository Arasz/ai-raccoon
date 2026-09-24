# QA session: MLX padding memory vs equal-length rows

**Date:** 2026-09-25
**Context:** the MLX re-run of the chunk-size arms, `docs/work/2026-09-25-chunk-size-vs-attention-window-mlx.md` (PR #738), and its probes.

## Main motive

The MLX record recommends padding every row to a multiple of 64 tokens, because the footprint grows with each distinct sequence length. The question was whether a padded row costs exactly what an unpadded row of the same length costs, or whether padding adds a hidden overhead or saves something extra.

## Refined questions

1. On the MLX path, does a row padded to L tokens (fewer real tokens, the rest masked `[PAD]`) use the same memory, time and CPU as an unpadded row that is exactly L tokens long?

## Structured answers

1. Yes. MLX keys its work by input shape, and the mask does not change the shape. A padded row of length L behaves like any other row of length L. A direct probe of 300 runs at L = 1024 on the M4 showed:

   | run | shapes | peak footprint | run p50 | CPU for 300 runs |
   |---|---|---|---|---|
   | 300 unpadded rows, all 1024 tokens | 1 | 719 MiB | 59.6 ms | 1.8 s |
   | 300 rows of 961-1024 real tokens, padded to 1024 | 1 | 718 MiB | 64.4 ms | 1.8 s |
   | the same 961-1024 rows, unpadded | 64 | 17,629 MiB | 67.7 ms | 10.1 s |

   Padded and equal-length runs match on memory (1 MiB apart) and CPU. Time is 5 ms apart per run, and that gap is inferred to be noise: both do the same 1024-position matmuls. Pad tokens are not free. They pass through every layer, so each row costs its padded length rather than its real length. That is at most 63 extra tokens per row, and the padded harness arms still ran at 46-57 ms per 1k real tokens. What padding removes is the per-shape cost: each extra length adds about 185-275 MiB and a compile (64 shapes at about 1,000 tokens reached 17.6 GB and 5.6× the CPU). The padded 1022 arm peaked at 3.7 GB because it produced 16 shapes, not 1. So the footprint at a padded budget is set by how many buckets are reached, about 230 MiB per bucket near 1024 tokens. The MLX documentation says the same thing about its own compile cache: changing the shape of an input recompiles, so the cache ends up with one entry per shape. Its free-buffer cache also defaults to the memory limit, which fits the ~75% RAM plateau (inferred; the plugin's limit was not read directly).

   Grounding: memory/code: the probe above (`eq.py`, run 2026-09-25, `process_memory.memory_kib` from PR #738); `docs/work/2026-09-25-chunk-size-vs-attention-window-mlx.md` F2/F3 and `docs/work/chunk-window-mlx-pad64/*.json`. Web: MLX "Compilation" docs (recompile on shape change); MLX `set_cache_limit` docs (cache limit defaults to the memory limit).

## Open / unanswered questions

- Does the onnxruntime MLX plugin EP expose MLX's cache or memory limit? If it does, setting it low (or to 0) would bound the footprint without padding. Resolve by reading the plugin's EP options or source.
- Would 32-token buckets be a better trade (up to 32 shapes, at most 31 pad tokens) than 64? A padded sweep at `--pad-to 32` would answer it.

## Source links

- `docs/work/2026-09-25-chunk-size-vs-attention-window-mlx.md`
- `docs/work/chunk-window-mlx/*.json`, `docs/work/chunk-window-mlx-pad64/*.json`
- `scripts/src/retrieval_tuning/process_memory.py`, `scripts/src/retrieval_tuning/padding.py`
- https://github.com/Arasz/ai-raccoon/pull/738, https://github.com/Arasz/ai-raccoon/issues/739
- https://ml-explore.github.io/mlx/build/html/usage/compile.html
- https://ml-explore.github.io/mlx/build/html/python/_autosummary/mlx.core.set_cache_limit.html
