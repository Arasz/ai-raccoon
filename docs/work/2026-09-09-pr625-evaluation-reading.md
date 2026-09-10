# Research: what the PR 625 evaluation tells us about harness-vs-bank parity

**Date:** 2026-09-09
**Question:** What does the evaluation built and run in the PR 625 (LlamaIndex+Chroma fusion-retrieval harness) task tell us about parity between the replica harness and ai-raccoon memory retrieval?

```chart:bars
title: subset-10 hit-rate and mean F1 by system (single run, n=10)
harness hit-rate: 0.4
ai-raccoon hit-rate: 1.0
harness mean F1: 0.089
ai-raccoon mean F1: 0.222
```

## Findings

### F1 — The task's evaluation is a paired per-query comparison at uniform limit 8 with exact-hash singleton scoring [READ]

Each corpus query runs on both legs — the harness `FusionRetriever.retrieve()` and the ai-raccoon leg through a scratch server — and each side is scored as hit (exact expected hash in the served set), precision/recall/F1 against the singleton expected set, plus the inter-system agreement MCC over paired binary hits. Both legs run at limit 8 with the 0.6 floor, overriding the corpus `searchLimit: 5`. A single-system MCC is deliberately not computed (degenerate: every trial has a known relevant doc); only the agreement phi is reported, null-with-reason on a zero denominator.

**Evidence:** `scripts/retrieval_tuning/llamaindex_harness/evaluate.py:36-79` (EVAL_LIMIT, hit/F1/MCC definitions), `:149-153` (harness leg), `:186-187` (prod leg limit + floor), `:205-216` (fail gates).

### F2 — The only end-to-end numbers ever produced are a 10-query subset: harness hit-rate 0.400 / F1 0.089 vs bank 1.000 / 0.222 [MEASURED]

Ran once, on a 300-rows-per-bucket slice (914 rows) with cached HF weights offline and a scratch server over the slice on an ephemeral port: `n=10 paired=10 harness hit-rate=0.400 f1=0.089 | ai-raccoon hit-rate=1.000 f1=0.222 | mcc=None cont={'a': 4, 'b': 0, 'c': 6, 'd': 0}`. The results file was deleted with the scratch directory during post-merge cleanup, so the printed summary line is the surviving record — treat these as single-run subset observations, not the comparison the plan specified (100 queries, full corpus).

**Evidence:** `python3 -m llamaindex_harness.evaluate --corpus corpora/eval-set-100.json --store-dir /tmp/llamaindex-625/store-300 --scratch-data-root /tmp/llamaindex-625/scratch-300 --out /tmp/llamaindex-625/results-10.json --limit-queries 10 --offline`, run 2026-09-09 from `scripts/retrieval_tuning` on the task worktree; exit 0; run once, not repeated.

### F3 — On the subset, every harness hit was also a bank hit; the gap is six bank-only hits [MEASURED]

Contingency `a=4, b=0, c=6, d=0`: 4 queries both systems hit, 6 only the bank hit, none harness-only, none neither. That is also why MCC is null — with `b+d=0` the agreement denominator is zero, exactly the degenerate case the metric design anticipates.

**Evidence:** Same single run as F2 (contingency printed on the same summary line).

### F4 — The evaluation pipeline fits comfortably in memory: 3.1 GB to ingest 914 rows with the real model, 4.8 GB for the 10-query two-leg eval [MEASURED]

Slice ingest with the 400M-parameter HF model in fp32 on CPU peaked at 3.1 GB (exit 1 — the FTS-probe scope failure, fixed afterwards, not a memory event); the subset eval holding the model plus a scratch dotnet server peaked at 4.8 GB (exit 0); the full 95-test unit suite peaked at 1.0 GB. All under a 12 GB kill-cap on a 25.7 GB machine. Single runs each, sampled every 2 s over the whole process tree, so brief spikes between samples could hide.

**Evidence:** `/tmp/step3-ingest-mem.log` (peak=3099MB), `/tmp/step4-eval10-mem.log` (peak=4804MB), `/tmp/full-suite2-mem.log` (peak=1017MB), via `/tmp/memwatch.py` (ps-tree RSS sampling, SIGTERM past cap), 2026-09-09.

### F5 — The subset deficit reads as the documented embedding/structure gap, not a fusion-math defect [INFERRED]

Reasoning from three inputs: the plan's own M3 hypothesis (harness runs content-only public HF weights while the bank fuses content+structure ONNX vectors at α=0.5, with a diagnostic split for exactly this); the reviewer's line-by-line port-fidelity check of RRF/affinity/merger/window/stopwords against the C# sources; and my own check that bank-vs-harness bm25 order is identical (147/147) once compared per-bucket. None of these measures the structure-leg contribution directly — that column exists in the harness output (`fts_hit`/`vector_hit` per query) but was never aggregated because the full eval never ran.

### F6 — No evaluation report markdown was merged; the comparison the plan promised does not exist as an artefact [READ]

The merged PR carries 21 files including `report.py` (the renderer) but no `llamaindex-fusion-eval-<date>.md`. P3 AC1 (all 100 queries on both systems) and AC2 (report with per-query F1, hit-rates, MCC, parity-gap discussion) have no artefact; only the wiring (P3 AC3 on a 10-query subset) was demonstrated.

**Evidence:** `git ls-tree -r --name-only HEAD | grep llamaindex` on main post-merge (only `report.py` + `test_..._report.py` match an eval/report name); the merge commit is `fd69a2d4`.

### F7 — CI covers the harness's stdlib-runnable tests, including a new lane created for them [READ]

`scripts-harness` passed in 20 s alongside build-fast/bdd/slow on the fix commit — the first CI coverage `scripts/tests` ever had (the dotnet `code` filter skips scripts-only diffs). Heavy files (ingest/retrieve/cli) remain a documented local gate only.

**Evidence:** `gh pr checks 625` output read 2026-09-09 (all six checks green on run 34375882360); lane definition at `.github/workflows/build.yml` (`scripts-harness` job, added in the merged PR).

### F8 — Which diff hunk the question's anchor points to could not be established [UNVERIFIED]

The `diff-8c0c…6643b` anchor matches the md5 of none of the 21 filenames in PR 625. It does not change the answer — every evaluation artefact in the PR was examined regardless — but a claim about "this specific hunk" cannot be grounded.

### F9 — Whether any of the 10 subset queries carried a stale anchor is unknown [UNVERIFIED]

Six corpus anchors are known-stale upstream (re-chunked, unhittable by either leg) and the eval warns-and-records them, but the subset run's warning lines were cut by output truncation and its results file was deleted. A stale anchor in the 10 would depress both systems' hit-rates equally, so it cannot explain the gap — only the absolute levels.

## Still open

- The full 100-query comparison on the full-corpus store (the plan's P3 AC1/AC2): needs a full ingest (~12k rows, est. under 10 min at the measured rate) plus 100 two-leg queries, then `report.py`. Until then every parity statement is a 10-query subset observation.
- Aggregating the per-query `fts_hit`/`vector_hit` diagnostics to split the observed deficit into embedding-gap vs structure-gap vs fusion-gap columns (the harness already records them; nobody has summed them).
- Re-running the subset with fixed seeds/weights to put error bars on F2–F4 (all single runs).
- Resolving the anchor to a file, if the questioner needs a hunk-level answer rather than a task-level one.
