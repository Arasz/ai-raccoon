# Research: follow-up measurements after the granite engine switch

**Date:** 2026-09-24
**Question:** After ADR-0108, is the byte-identical-chunk dedupe loss worth a fix, does the test-file ranking penalty (eval P3) clear the keep rule on granite, and what happens to the harness follow-ups A1, D, F1 and F2?

```chart:bars
title: eval P3 on granite, overall nDCG@5 by test-file weight
w=1.0 (off): 0.8238
w=0.8: 0.8099
w=0.6: 0.7959
w=0.4: 0.7928
```

## Findings

### F1 — Byte-identical chunks cost 0.045% of chunks and 0.012% of lines on the author's bank, below the 0.5% fix threshold [MEASURED]

Two byte-identical chunks in one file share a content hash (`SHA256(path‖value)`), and the unique index `uq_code_chunk(project_id, path, hash)` keeps a single row. That row ends up with the later occurrence's line range. On the owner's live bank, 2 of 7,884 paths lose chunks. 25 of 55,207 chunks are lost (0.045%), which leaves 161 non-blank lines unaddressable (0.012%). One of the two files is a repetitive data file, `benchmarks/AiRaccoon.Benchmarks/Corpus/RealWorldQueries.cs`, which accounts for 24 of the lost chunks. The other is a test spec, which loses one. Under the owner's ruling (fix only above 0.5% of lines), this is recorded and not fixed.

**Evidence:** a sqlite backup-API copy of `~/.ai-raccoon/memory.db`, taken 2026-09-24 about 02:00Z (55,182 code rows). `prevalence.py` computes per `(project_id, path)` the loss `total_chunks − COUNT(DISTINCT chunk_index)`, and counts non-blank on-disk lines covered by no row. No path had mixed `total_chunks`, and no row had `total_chunks ≤ 0`. The metric fires on real cases, so it can report a loss. No synthetic control was run.

### F2 — Eval P3 (test-file ranking penalty) is still a DROP on granite, at every weight [MEASURED]

The P3-A re-rank multiplies test-path hits by w over the top 20 hits and cuts to 10. It lifts the distractor category by +0.112, but it breaks the test-intent floor at every weight: −0.38 at w=0.8, −0.64 at 0.6 and 0.4, against a floor of −0.05. The held-out mean is +0.005, CI [−0.015, +0.025], at w=0.8. The tuning split moves the other way (−0.023). This is the same verdict as on code-daemon (`2026-09-23-code-retrieval-eval-results.md` F5). The baseline nDCG@5 of 0.8238 equals the earlier granite figure, so the bm25 hash tie-break (#702) moved nothing on this corpus.

**Evidence:** Release build of main at b5232ff0 with VERSION 1.49.3. `run_code_eval.py --fetch-limit 20 --save-hits --arm p3-base` gave n=332 and a drain of 50 s. `spike_p3_rerank.py` ran at w ∈ {1, 0.8, 0.6, 0.4}, and `compare_code_eval.py results-p3a-w1.json results-p3a-w<w>.json --target-category distractor` returned DROP for all three. Apple M4, with another session's eval server running.

### F3 — Eval P4 (path header) and P5 (AST chunking) are already re-measured on granite, and neither is kept [READ]

P5: `2026-09-23-ast-chunking-on-granite-and-magika.md` F1 (DROP) and `2026-09-24-ast-chunking-on-whole-repos.md` F1/F2, which found no effect on two whole repositories. P4: `2026-09-24-symbol-path-header-on-line-chunks.md`, which did not keep a path or symbol header on line chunks.

**Evidence:** the three records cited, on main.

### F4 — Harness A1 no longer has a clean comparison, so the seam is accepted (A3) and a re-baseline on granite is filed [INFERRED]

A1 would give the Python harness the bank's own embedder to separate fusion from embedding. The harness embeds with a gte checkpoint (`scripts/retrieval_tuning/llamaindex_harness/ingest.py:48, 208-234`). The frozen golden came from the pre-ADR-0108 bank engine, and the bank now embeds with granite. Running A1 today would change the engine and the fusion together against that golden. The only clean A1 would use the original bank's stored vectors, which describe a runtime the product no longer ships. Owner ruling (2026-09-24): accept A3 and file "re-baseline the harness on granite", which absorbs harness package D (per-row leg ranks).

### F5 — F1 (FTS tie-break) is fixed; F2 (ORT thread default) is accepted as it is [READ]

F1: #702 adds `, e.hash` as the secondary key to both `ORDER BY bm25(...)` sites. It matches the harness port, and a seeded tie test (id order ≠ hash order, `LIMIT` inside the tie) went red, then green. F2: the default of half the cores for ORT threads (`EmbeddingService.cs:363-371`) stays. Granite runs on WebGPU first (ADR-0108). CPU is the path on linux-x64 CI and on hosts without WebGPU, where `embedding.threads=1` would buy order stability at near-ties by giving up throughput. `embedding.threads` remains the override.

**Evidence:** PR #702 (merged bedad8df); `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:363-371`.

## Still open

- Line coverage beyond dedupe: 3,322 further non-blank lines (0.26%) on the same bank are covered by no row, in paths that lost no chunks. Likely cause: files edited after their last ingest, leaving the stored positions stale. Not investigated.
- Every verdict above is scoped to osx-arm64 (ADR-0015); none was re-run on linux-x64.
- F2's order instability at near-ties on CPU was not measured on granite.
