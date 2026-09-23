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

## Still open

- **P5 value.** The trigger stands with P4 dropped. The AST and regex-boundary chunking spike is next.
- **Whether these verdicts hold on linux-x64 CI.** They are scoped to this machine (ADR-0015).
- **Whether they hold for other code engines.** A parallel session found `code-daemon-embed-v1` last of 15 models on a C#-only doc-comment eval. A code-engine swap could change every delta here.
