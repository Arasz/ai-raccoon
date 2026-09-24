# Research: chunk size vs granite's 128-token local attention window

**Date:** 2026-09-24
**Question:** The bundled engine (granite-embedding-small-english-r2, ADR-0108) attends locally over a 128-token window in 8 of its 12 layers. Memory chunks are 254 tokens and code chunks 510. Does recall fall off once a chunk outgrows that window, and what do bigger chunks cost in RAM, disk, CPU and latency? Three configurations were asked for, in this order: the baseline (memory 254, code 510), equalized (both 510), and both doubled (both 1022). A 128-token arm on each corpus pins the other end of the curve.

**Answer:** The 128-token window is not a cliff. Recall does not drop once a chunk passes 128 tokens. On code, targets that start past token 64 inside a 254 or 510 chunk are found as often as targets in the first 64. The cost curve bends at 512 instead. Doubling to 1022 raises embed CPU by about 45% for the same text and peak RSS by 56-82%. It also makes code retrieval worse: file MRR drops by 0.034, with an interval that excludes zero. Inside a 1022-token code chunk, targets past token 512 are found 15 points less often. Memory text goes the other way, and the 1022 arm scores best there (span MRR +0.165), but part of that gain is a bigger net, not a better vector. Equalizing memory at 510 is cheap (+33% RSS, about 8% more CPU, a third less disk) and buys a small span gain that doesn't clear noise.

```chart:bars
title: code file MRR@10 by chunk budget (240 queries, vector leg)
128: 0.831
254: 0.825
510 (baseline): 0.822
1022: 0.788
```

```chart:bars
title: memory section MRR@10 by chunk budget (75 queries, vector leg)
128: 0.520
254 (baseline): 0.551
510: 0.562
1022: 0.716
```

```chart:line
title: embed wall ms per 1k tokens, CPU provider, 2 threads (128,254,510,1022)
memory: 416,448,529,714
code: 419,432,518,756
```

## The three configurations

Each corpus runs in its own process, so RAM is per corpus. Deltas are against the baseline row of the same corpus.

| config | corpus | chunks | file R@5 | file MRR@10 | span R@5 | span MRR@10 | ingest wall s | ingest CPU s | embed ms/chunk (p50) | peak RSS MiB | index KiB |
|---|---|---|---|---|---|---|---|---|---|---|---|
| A baseline | memory 254 | 1,608 | 1.000 | 0.899 | 0.733 | 0.551 | 150 | 293 | 99 | 287 | 5,316 |
| A baseline | code 510 | 1,081 | 0.946 | 0.822 | 0.825 | 0.673 | 231 | 453 | 229 | 378 | 4,572 |
| B equalize | memory 510 | 775 | 0.987 | 0.897 | 0.800 | 0.562 | 161 (+7%) | 315 (+8%) | 230 | 381 (+33%) | 3,536 (−33%) |
| B equalize | code 510 | unchanged | | | | | | | | | |
| C both 2× | memory 1022 | 383 | 0.973 | 0.948 | 0.827 | 0.716 | 206 (+37%) | 405 (+38%) | 574 | 522 (+82%) | 2,724 (−49%) |
| C both 2× | code 1022 | 544 | 0.921 | 0.788 | 0.833 | 0.644 | 335 (+45%) | 659 (+46%) | 708 | 589 (+56%) | 3,524 (−23%) |
| extra | memory 128 | 3,608 | 1.000 | 0.897 | 0.747 | 0.520 | 168 | 330 | 46 | 278 | 9,852 |
| extra | code 128 | 4,299 | 0.950 | 0.831 | 0.771 | 0.597 | 189 | 370 | 45 | 281 | 10,992 |
| extra | code 254 | 2,191 | 0.938 | 0.825 | 0.825 | 0.651 | 194 | 380 | 92 | 304 | 6,688 |

"File" scores the first chunk from the expected file. "Span" scores a chunk that holds the expected section (memory) or overlaps the expected lines (code). GPU use was not measured (F7).

## Findings

### F1: Recall does not fall off past the 128-token local window [MEASURED]

Code file MRR@10 is flat from 128 to 510 tokens (0.831, 0.825, 0.822), and every paired interval against the 510 baseline crosses zero (128: −0.027..+0.009..+0.046, 254: −0.024..+0.004..+0.032). Memory file MRR is flat from 128 to 510 as well (0.897, 0.899, 0.897). With [CLS]/[SEP] counted, 7-10% of chunks exceed 128 tokens in the 128 arms, 85-89% at 254 and 94-97% at 510, so almost every baseline chunk already lives past the window. The within-chunk position test says the same thing. At 254, a code target that starts past token 64 of its chunk ranks in the top 5 as often as one inside the first 64 (+0.028, interval −0.077..+0.131, n=141/99). At 510 it is +0.046 (−0.065..+0.161, n=72/168).

**Evidence:** `docs/work/chunk-window/*.json` (per-query metrics and `gold_position` rows), paired bootstrap from `chunk_window.paired_bootstrap` (4,000 resamples, seed 1). Position buckets from `chunk_window.position_buckets`.

### F2: The window is local only in 8 of 12 layers, and [CLS] reaches the whole chunk through the other 4 [READ]

`global_attn_every_n_layers: 3` in the model's `config.json` makes layer `i` a sliding layer when `i % 3 != 0`, so layers 0, 3, 6 and 9 attend over the full sequence (`configuration_modernbert.py:115-118`, transformers 5.17.0). A sliding layer lets a token see keys with `abs(q_idx - kv_idx) <= local_attention // 2`, which is 64 tokens either side (`configuration_modernbert.py:160-162`, `masking_utils.py:150`). The engine pools [CLS] (position 0), so in a sliding layer [CLS] sees only the first 64 tokens. Every third layer hands it the rest. That is why F1 finds no cliff at 128: the pooled vector is never built from the window alone.

**Evidence:** `src/AiRaccoon/Models/granite-embedding-small-english-r2/config.json` (`local_attention: 128`, `global_attn_every_n_layers: 3`), the transformers 5.17.0 wheel from PyPI (files cited above), and `docs/work/2026-09-24-harness-granite-rebaseline.md` F2 for CLS pooling.

### F3: Doubling code chunks to 1022 costs file recall, and late targets are the ones lost [MEASURED]

Code file MRR@10 falls from 0.822 to 0.788 at 1022 (paired difference −0.066..−0.034..−0.002). Hit@1 falls from 0.725 to 0.696 and recall@10 from 0.971 to 0.950. Inside a 1022-token chunk, targets that start at token 512 or later reach the top 5 at 0.724 against 0.872 for earlier ones (difference −0.148, interval −0.264..−0.037, n=164/76). One vector for 800 tokens of code averages over more unrelated code, and what the chunk says late counts for less. This is inferred from the position split, not proven: late targets also sit in longer files, which may be harder queries on their own.

**Evidence:** `docs/work/chunk-window/code-510.json`, `code-1022.json`, bootstrap as in F1.

### F4: Memory text gains from 1022-token chunks, but part of the gain is a bigger net [MEASURED, cause INFERRED]

Memory section MRR@10 rises from 0.551 to 0.716 at 1022 (interval +0.077..+0.165..+0.253) and file hit@1 from 0.813 to 0.920 (file MRR interval −0.004..+0.049..+0.100). ADRs average about 2,500 tokens, so a 1022 chunk often carries Context, Decision and Consequences together. A query for one section then lands on a chunk that holds all three, and the section metric counts that as a hit. The file-level hit@1 gain is not exposed to this effect, which suggests a real part: ADR prose reads better as one long vector than code does. Equalizing at 510 moves section MRR by only +0.011 (−0.055..+0.079), inside the noise.

There is a cost the metrics do not show. The top 5 chunks at 1022 hand an agent about 3,700 tokens per search, against about 1,000 at 254.

**Evidence:** `docs/work/chunk-window/memory-*.json`. Token totals from `docs/adr/*.md` counted with the bundled `tokenizer.json` (277,305 tokens over 111 files).

### F5: Embed cost is linear up to about 256 tokens and superlinear after it [MEASURED]

The code corpus embeds the same text in every arm (440-447k tokens, no overlay), so its CPU column compares like with like. Ingest CPU is 370, 380, 453 and 659 s at 128, 254, 510 and 1022. Wall time per 1k tokens goes 419, 432, 518 and 756 ms. The 4 global layers are quadratic in sequence length, and from 512 up they dominate. Memory embeds less text as chunks grow (400k tokens at 128, 287k at 1022), because the fixed 48-token overlay repeats once per chunk. That is why memory's ingest CPU goes down from 128 to 254 before it rises.

**Evidence:** `ingest_cpu_s`, `tokens_embedded` and `embed_ms_per_1k_tokens` in `docs/work/chunk-window/*.json`.

### F6: Peak RSS follows the longest sequence; the index shrinks as chunks grow [MEASURED]

The model loads at 226-231 MiB in every arm. The ingest peak is 275-304 MiB at 128-254 tokens, 374-379 at 510, and 520-586 at 1022. ONNX Runtime keeps the activation peak of the largest run (the product runs one row per session run for this reason, `OnnxEmbeddingGenerator.cs:418-419`). The index, holding text, FTS5 and float32 vectors as the bank does, shrinks as chunks get larger: memory 5.3 → 3.5 → 2.7 MB and code 4.6 → 3.5 MB. There are fewer vectors (1,536 bytes each) and less overlay text.

**Evidence:** `rss_kib_after_load`, `rss_kib_peak_ingest`, `disk_bytes` and `vector_bytes` in `docs/work/chunk-window/*.json`. The layout mirrors, but is not, the product's vec0 bank.

### F7: GPU use was not measured here [UNVERIFIED]

This host has no GPU, and the Linux package runs ONNX Runtime on the CPU (ADR-0108, Consequences). The only GPU numbers on record are ADR-0108's Apple M4 WebGPU measurements. There, fp16 took 9 ms at 128 tokens and 29 ms at 512, which is 3.2× the time for 4× the tokens, a steeper curve than this CPU showed between those sizes. Nobody has measured a 1022-token GPU embed.

**Evidence:** `docs/adr/0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md`, "The CPU is shared" paragraph.

### F8: Query latency barely depends on chunk size; the harness's query CPU is not representative [MEASURED]

Query wall time was 17-37 ms in every arm, most of it the query embed (15-26 ms), and repeat runs moved it by 6 ms. Query CPU time ran 116-143 ms at 128-254 tokens against 33-41 ms at 510-1022, while wall time stayed near 30 ms. That is numpy's multithreaded matrix product spinning on the larger vector matrix. The product's vec0 search is single-threaded brute force, so there the only chunk-size effect is linear in the number of vectors.

**Evidence:** `query_ms`, `query_embed_ms` and `query_cpu_ms` in `docs/work/chunk-window/*.json`. The memory-128 re-run changed query mean from 30.9 to 36.6 ms with identical recall.

## Method

- **Engine:** the bundled `model_fp16.onnx` + `model_fp16.onnx_data` and `tokenizer.json`, taken from the published `ai-raccoon.linux-x64` 1.50.0 package, with SHA-256 matching the manifest pins. ONNX Runtime 1.30.0 (the product's version), CPU provider, `intra_op_num_threads = 2` (the product's half-the-cores default on this 4-core host), one row per run, `sentence_embedding` output, L2-normalized. Tokens are counted without specials; the embedded sequence adds [CLS]/[SEP].
- **Harness:** `scripts/retrieval_tuning/run_chunk_window_eval.py` with `scripts/src/retrieval_tuning/chunk_window.py`. The chunkers in `chunk_ports.py` are ports of `MarkdownChunker` (48-token overlay, cut sections deferred) and `CodeChunker` (blank-line blocks, brace-balanced boundaries), tested in `scripts/tests/test_chunk_ports.py` (stdlib-only, run by the `scripts-harness` CI job). Over-budget fences fall back to per-line units instead of being re-fenced. This is not the product binary: no .NET SDK could be installed in this session.
- **Corpora:** memory is all 111 ADRs with eval-set-100's 75 section-anchored queries. Code is the 12-language corpus minus CSS/HTML/SQL (no product chunker), 89 files and 240 queries.
- **Search:** the vector leg is brute-force cosine over all chunks. The hybrid leg adds FTS5 BM25 fused by RRF (k=60). The hybrid is an approximation and is kept in the JSON files but not used in the findings.
- **Machine:** 4 vCPU, 15 GiB, Linux, no GPU, no other load. The first memory-128 run overlapped a lockfile update and was re-run. Only its timings changed.
- **Code 1022 is not a settings change.** `EmbeddingService.MaxManifestChunkTokens` (510) caps the code budget. Memory 510 and 1022 need only the manifest's `chunkTokens`, which the validator accepts up to the window minus 2.

Reproduce with `python3 scripts/retrieval_tuning/run_chunk_window_eval.py --model-dir <granite dir> --out <dir>` (about 35 minutes on this host). `--report --out docs/work/chunk-window` reprints the tables and intervals from the committed results.

## Still open

- Run the same arms through the product on the GPU (osx-arm64, WebGPU) to fill F7, especially the 1022-token latency and GPU memory.
- Test F4's split: score memory at 1022 against a section-exact target that a multi-section chunk can't satisfy by containment.
- Code chunks between 510 and 1022 (for example 768) were not run. F3 locates the loss past 512 without saying where it starts.
