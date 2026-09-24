# Research: measured code-retrieval improvements on the multi-language code eval

**Date:** 2026-09-23
**Question:** On the 12-language code eval corpus, which of the planned code-search changes (identifier-split keyword column, test-file down-weighting, path header on embedded text, AST chunking) improves held-out retrieval enough to keep, under the keep/drop rule fixed in `docs/work/2026-09-23-code-retrieval-eval-plan.md`?

Setup for every run:
- **Corpus:** `scripts/retrieval_tuning/code-corpus/`. 116 files: 108 source plus 8 tests, 12 languages, 3 small/medium/large per language. 332 validated queries: 227 tuning, 105 held-out.
- **Runner:** `scripts/retrieval_tuning/run_code_eval.py`, `kind=code`, top 10.
- **Keep/drop verdicts:** `scripts/retrieval_tuning/compare_code_eval.py`.
- **Machine and engine:** macOS arm64 dev machine, code engine `faxenoff/code-daemon-embed-v1`.
- **Load:** heavy background load throughout (load average 5–70, from other repos' test runs). It affects drain seconds only; scores are deterministic (F1).

```chart:bars
title: overall nDCG@5 by arm (all 332 queries)
B0 current code: 0.501
B1 +html/css/sql: 0.654
P2-A identifier column: 0.689
P3-A tests x0.8: 0.655
P4-A path header: 0.663
P4-A path+decl header: 0.653
P5-A AST chunks (+P2): 0.701
P5-A regex chunks (+P2): 0.690
Final B1+P2 (shipped): 0.689
Granite + P1 + P2: 0.824
```

## Findings

### F1 — Retrieval scores are deterministic run to run; the noise band is 0.000 [MEASURED]

Three B0 runs (one of them scoring a reused bank, two with fresh ingest and drain) and two fresh B1 runs gave identical metrics to four decimals on every metric. Drain time varied (88 s–231 s for B0) with machine load. The keep rule's noise band is therefore 0, and a verdict rests on the bootstrap CI alone.

**Evidence:** `run_code_eval.py` arms `b0-run1..3`, `b1-run1..2`, binaries `1.44.4+85d7d7da` (B0) and `1.46.2+107110d4` (B1), 2026-09-23, same machine; summary lines `nDCG@5=0.5014 … spanHit@5=0.4669` (B0 ×3) and `nDCG@5=0.6535 … spanHit@5=0.6355` (B1 ×2).

### F2 — Baseline B0: nDCG@5 0.501 overall, 0.607 held-out; HTML, CSS and SQL score 0 because they are not indexed [MEASURED]

1,169 chunks from 89 of the 116 files. The 27 HTML/CSS/SQL files were skipped because their extensions were not in `CodeExtensions`. Weakest indexed language: C++ at 0.435. Identifier-fragment queries were the weakest category at 0.421.

**Evidence:** arm `b0-run1`, `results-b0-run1.md` (per-language and per-category tables); `code_entries` count 1,169 over 89 `source_file`s, read from the drained bank.

### F3 — Indexing .html/.htm/.css/.scss/.sql (P1) lifts overall nDCG@5 to 0.654; the new languages land at 0.67–0.76 [MEASURED]

1,481 chunks. HTML 0.674, CSS 0.764, SQL 0.745. C++ moved from 0.435 to 0.387 and JSX from 0.816 to 0.790. The new files compete for some queries, and B1 also carries main's embedding changes (1.44.4 → 1.46.2). So B1, not B0, is the reference for every spike.

**Evidence:** arms `b1-run1`, `b1-run2`; `results-b1-run1.md`.

### F4 — P2 identifier-split keyword column: KEEP (held-out +0.052, 95% CI [0.021, 0.088]) [MEASURED]

The spike was an extra FTS5 column on a copy of the B1 bank, holding every multi-part identifier in the chunk, split and lowercased (`WatchOverlapResolver` → `watch overlap resolver`). There was no server change and no re-embed.
- Identifier-fragment held-out: +0.110.
- Behaviour-NL held-out: +0.033.
- Tuning split: +0.028.
- Worst language: Python at −0.010.
- Overall nDCG@5: 0.654 → 0.689.

The plan's variant 1b (splitting query terms and weighting the column in bm25) was not run. The unweighted, document-side column alone cleared every rule, and a query-side split would have to live outside the `FtsQueryNormalizer` that memory search shares.

**Evidence:** `spike_p2_identifiers.py` on `bank-b1` → arm `p2a`; `compare_code_eval.py results-b1-run1.json results-p2a.json --target-category identifier-fragment` → `KEEP`, bootstrap 2000 resamples, seed 20260923.

### F5 — P3 test-file down-weighting: DROP; it trades "where is this tested" queries away for a small gain [MEASURED]

The spike multiplied the fused score of test-path hits (from B1's top 20) by w, re-sorted, and re-scored the top 10:

| w | distractor | test-intent | overall nDCG@5 |
|---|---|---|---|
| 1.0 | 0.801 | 0.525 | 0.6535 |
| 0.8 | 0.844 | 0.277 | 0.6547 |
| 0.6 | 0.844 | 0.054 | 0.6455 |
| 0.4 | 0.844 | 0.000 | 0.6379 |

At w=0.8 the held-out CI was [−0.011, …] with a mean of +0.007, and the test-intent floor was breached (−0.32). w=1.0 reproduces B1 exactly, which confirms the re-scoring path is faithful.

**Evidence:** `spike_p3_rerank.py` on `hits-b1-hits.json` (arm `b1-hits`, `--fetch-limit 20 --save-hits`); `compare_code_eval.py … --target-category distractor` → `DROP` for w=0.8 and w=0.6.

### F6 — A path header fits under the 510-token window for most chunks, so no re-chunk is needed to try P4 [MEASURED]

The share of chunks with room for the path (and for path plus a ~20-token declaration line), counted with the model's own SentencePiece tokenizer on B0's chunks:
- **Large:** 80.5% (64.0%), median room 62 tokens.
- **Medium:** 85.8% (70.3%).
- **Small:** 90.2% (80.4%).

**Evidence:** `coverage_p4.py` over `bank-b0` (`sentencepiece.bpe.model` from the bank's `faxenoff__code-daemon-embed-v1`), via `uv run --with sentencepiece`.

### F7 — P5's AST-chunking trigger fires on the file-found/span-missed signal, not on span splitting [MEASURED]

On B1 plus P2:
- **Span-split rate** (answer spans crossing 2 or more chunks): 17.6%, 55 of 312, under the 20% trigger.
- **Wrong chunk from the right file:** of the 90 queries whose answer span is missing from the top 5, 34 (37.8%) had the right file in the top 5. That is over the 30% trigger.
On B1 alone the figures are 17.6% and 38/101 (37.6%).

**Evidence:** `p5_gate.py` over `bank-b1-p2` with `hits-p2a-hits.json`, and over `bank-b1` with `hits-b1-hits.json`.

### F8 — P4 path header on the embedded text: DROP in both forms [MEASURED]

The header is prefixed only when it fits under the window, and the stored value is untouched.

| arm | held-out Δ nDCG@5, 95% CI | path-context Δ | worst language | verdict |
|---|---|---|---|---|
| path only | +0.015 [−0.010, 0.040] | +0.018 | TS −0.017 | DROP (CI includes 0) |
| path + enclosing declaration | −0.004 [−0.034, …] | −0.015 | C# −0.056 | DROP (target down, C# floor) |

The enclosing-declaration line hurts C#, the language with the most doc-commented, declaration-dense code. So neither the file path nor the declaration context gives this engine a signal it lacks.

**Evidence:** spike branch `spike/p4-header` (`CodeEmbedder.SpikeText`, `AIR_SPIKE_HEADER=path|decl`, `AIR_SPIKE_ROOT=code-corpus/files`), fresh drains `p4a-path` (80 s) and `p4a-decl` (185 s); `compare_code_eval.py results-b1-run1.json results-p4a-*.json --target-category path-context` → `DROP`.

### F9 — P5 AST chunking: DROP; it reduces span splitting but does not lift held-out retrieval significantly [MEASURED]

Both arms re-chunked every file offline under the same 510-token budget and wrote pending rows into a copy of the B1+P2 bank. The server then re-embedded them. Baseline: B1+P2.

| arm | chunks | span-split | held-out Δ nDCG@5, 95% CI | worst floor | verdict |
|---|---|---|---|---|---|
| tree-sitter AST | 1,474 | 14.1% | +0.018 [−0.006, 0.044] | C# −0.049 | DROP (CI includes 0) |
| regex declaration boundaries | 1,436 | 19.9% | −0.003 [−0.036, …] | C# −0.053, test-intent −0.065 | DROP |

The AST arm is the closest miss of the task. Its point estimate is positive in five of six held-out languages, but not at this sample size. Four Bootstrap SCSS files fell back to regex chunks because the grammar returned an ERROR root.

The spike also exposed an existing behaviour. Two byte-identical chunks of one file share a content hash, so ingest keeps only one of them. The spike hit this in nlohmann-json (a copied Doxygen block) and in a C# test file (closing-brace boilerplate). It is low impact but real.

**Evidence:** `spike_p5_chunk.py --mode ast|regex` (py-tree-sitter + tree-sitter-language-pack), arms `p5a-ast` and `p5a-regex` (`--reuse-bank … --drain`); `compare_code_eval.py results-p2a.json results-p5a-*.json --target-category behaviour-nl` → `DROP`.

### F10 — The shipped P2 implementation reproduces the spike exactly [MEASURED]

A fresh ingest and drain with the product binary built from the PR branch (`1.46.2+5c9d0b5b`) scored nDCG@5 0.6890, the same as the P2 spike to four decimals on every metric. Against B1 the keep rule gives the same verdict and CI (KEEP, +0.052 [0.021, 0.088]). Across the whole task, from B0 to shipped, overall nDCG@5 goes from 0.501 to 0.689.

**Evidence:** arm `final`, `compare_code_eval.py results-b1-run1.json results-final.json --target-category identifier-fragment` → `KEEP`.

### F11 — Swapping the code engine to granite-embedding-small-r2 is worth more than any change measured here [READ]

The #687 session ran this corpus and runner through its product binary.

| engine | nDCG@5 | nDCG@10 | hit@1 | hit@5 | held-out nDCG@5 | drain |
|---|---|---|---|---|---|---|
| code-daemon-embed-v1 (main) | 0.501 | 0.506 | 0.446 | 0.581 | 0.607 | 120 s |
| bundled granite fp16 (#687) | 0.603 | 0.591 | 0.554 | 0.660 | 0.750 | 44 s |

Both runs are without this PR's HTML/CSS/SQL indexing, so those languages score 0 in both. The code-daemon row reproduces this record's B0 exactly. The two PRs compose: P1 and P2 change indexing and the keyword leg, #687 the vector leg. The combined gain is unmeasured.

**Evidence:** bus message from the #687 session to this session, 2026-09-23 20:44 UTC, citing `run_code_eval.py` on the #686 corpus.

### F12 — On granite, the identifier column still clears the keep rule, and the three changes together reach nDCG@5 0.824 [MEASURED]

After #687 made granite-embedding-small-r2 the bundled engine for memory and code, this branch was re-run with main merged in.
- **Everything together** (granite + HTML/CSS/SQL + identifier column): nDCG@5 0.824, hit@1 0.768, hit@5 0.892.
- **Ablation:** the same bank with `identifiers` blanked and `code_fts` rebuilt scores 0.801.
- **P2 against that ablation:** KEEP, held-out +0.037, 95% CI [0.008, 0.073], identifier-fragment +0.073.
- **Floors:** the worst language delta is Python at −0.013, and test-intent is −0.027.

The keyword-leg gain shrinks on a stronger vector engine (from +0.052 on code-daemon) but survives it.

**Evidence:** binary `1.48.0+9bf5d6f2` (merge of origin/main after #687). Arm `granite-p2` is a fresh ingest and drain (32 s). Arm `granite-noid` reuses that bank with `UPDATE code_entries SET identifiers=''` and an FTS `'rebuild'`. `compare_code_eval.py results-granite-noid.json results-granite-p2.json --target-category identifier-fragment` → `KEEP`.

## Still open

- **Whether a larger held-out set would settle AST chunking.** Its +0.018 point estimate needs about three times the held-out queries to resolve.
  *Answered 2026-09-24:* on granite the AST arm is a DROP here (−0.009) and shows no effect on two other whole repositories ([2026-09-24-ast-chunking-on-whole-repos.md](2026-09-24-ast-chunking-on-whole-repos.md)).
- **Whether these verdicts hold on linux-x64 CI.** They are scoped to this machine (ADR-0015).
- **Whether P3–P5 would flip on granite.** Only P2 was re-measured on the new engine.
  *Answered 2026-09-24:* P3, P4 and P5 were all re-measured on granite and none is kept — P3 in [2026-09-24-followup-batch-measurements.md](2026-09-24-followup-batch-measurements.md) F2, P4 in [2026-09-24-symbol-path-header-on-line-chunks.md](2026-09-24-symbol-path-header-on-line-chunks.md), P5 in [2026-09-23-ast-chunking-on-granite-and-magika.md](2026-09-23-ast-chunking-on-granite-and-magika.md) F1.
