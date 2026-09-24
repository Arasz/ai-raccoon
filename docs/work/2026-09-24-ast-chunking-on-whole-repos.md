# Research: AST chunking on two whole repositories (JSAA C#/TS, ai-badger Python)

**Date:** 2026-09-24
**Question:** When a whole real repository is indexed, does tree-sitter AST chunking improve code retrieval over the line chunker for C# and TypeScript (job-search-ai-assistant) and for Python (ai-badger)?

```chart:range
title: span-hit MRR@10 change, AST vs line chunker (95% CI low..mean..high)
JSAA all (144): -0.064..-0.010..0.041
JSAA C# (96): -0.063..-0.004..0.050
JSAA TS (26): -0.086..-0.016..0.051
ai-badger Python (96): -0.039..0.014..0.071
```

## Findings

### F1 — On the whole JSAA repository, AST chunking changes nothing measurable: span-hit MRR −0.010 [−0.064, +0.041], nDCG@5 +0.005 [−0.030, +0.039] [MEASURED]

There are 144 queries on 72 targets: 96 C# and 48 TS/TSX. The index holds all 16,760 code chunks of the tracked tree, 2,103 `.cs`, 428 `.ts` and 452 `.tsx` files, with the tests included as distractors. AST moved the span rank of 69 queries, 33 up and 36 down. Every slice's interval contains zero: C# −0.004 [−0.063, +0.050], TS −0.016, TSX −0.027. Plain-English queries lean positive (+0.011 span MRR, +0.030 nDCG@5) and identifier-fragment queries lean negative (−0.030, −0.020), but neither is resolved.

**Evidence:** `git archive HEAD` of job-search-ai-assistant at f783e291e with `.md` removed (memory rows; `kind=code` search never reads them). The binary was AiRaccoon Release sha256 86d3fee1…, the same one the 2026-09-23 study (`2026-09-23-ast-chunking-on-granite-and-magika.md`) used. The line arm is `run_code_eval.py` fresh ingest plus drain (422 s). The AST arm is `chunk.py --mode ast --only-ext cs,ts,tsx` (spike_p5 generalised, granite tokenizer, 510-token budget). That script re-chunks only those extensions and keeps every other row as ingested. Its output was "VERIFY OK", 14,504 chunks, 0 fallbacks, and it was drained by the server (579 s). `compare.py` computes paired deltas with a bootstrap of 4,000 resamples over targets (so a query pair moves together), seed 1. Apple M4, 24 GB, WebGPU EP, with a live 1.46 server running alongside.

### F2 — On ai-badger's Python, AST chunking also changes nothing measurable: span-hit MRR +0.014 [−0.039, +0.071], nDCG@5 −0.010 [−0.048, +0.027] [MEASURED]

There are 96 queries on 48 targets, over the 537 tracked `.py` files (6,137 line chunks, 6,541 AST chunks). Of the 33 changed ranks, 16 went up and 17 down. The two query categories and the three size bands all straddle zero.

**Evidence:** the same pipeline on ai-badger at 6c0153f0 (`--only-ext py`, "VERIFY OK", 0 fallbacks, drain 275 s). The harness's nDCG@5 matches the exact path only; the span and file MRR also accept any byte-identical copy of the expected file (see F4).

### F3 — AST chunks are much cleaner in Python and C#, and the retrieval gain still does not appear [MEASURED]

Share of chunks that start at a declaration, doc comment, attribute or decorator and also end at a closing point (for Python: the next non-blank line is not indented deeper than the chunk's first line):

| | line chunker | AST |
|---|---|---|
| ai-badger `.py` | 32% | 67% |
| JSAA `.cs` | 74% | 85% |
| JSAA `.ts` | 63% | 61% |
| JSAA `.tsx` | 49% | 40% |

Median chunk length is unchanged (29–34 lines). Python is where the line chunker is most ragged: its brace-balance preference has no braces to work with. Even there, doubling the share of whole-unit chunks does not move retrieval. This is the C# pattern from `2026-09-23-ast-chunking-on-granite-and-magika.md` F2 on a second language and a far larger index.

**Evidence:** `shape.py` over `code_entries` of each arm's bank, by extension. The heuristics are coarse regexes, so the absolute percentages are indicative; the between-arm differences use one and the same rule on both arms.

### F4 — A quarter of ai-badger's Python files are byte-identical copies, which depresses path-exact scores for both arms [MEASURED]

241 of the tracked `.py` files duplicate another file, in 98 groups (framework sources mirrored into `features/…`, `skills/…` and `.ai-badger/…`). The harness's nDCG@5 (0.48) counts a hit on a copy as a miss, while copy-aware span MRR is 0.68. The miss affects both arms equally, so it does not bias the delta, but it adds noise.

**Evidence:** `git ls-files '*.py' | xargs shasum` grouped by hash; `compare.py`'s `same_file` accepts a hit whose file sha256 equals the expected file's.

### F5 — Rank churn is large relative to the net effect: 48% of JSAA queries changed rank, split almost evenly [MEASURED]

69 of 144 JSAA queries and 33 of 96 ai-badger queries changed span rank, with close to even gains and losses. The losses are of two kinds. One is a tidier chunk of the wrong code, often a test (jcs-090, jcs-034, bpy-024 and bpy-020 lose their top-1 to a test file). The other is a boundary that moves the answer out of the chunk that used to hold it (jts-025/026: AST's L1-37 against the answer's L17-59). The wins mirror these.

**Evidence:** `sbs-job-search-ai-assistant.md` and `sbs-ai-badger.md` in the session scratch, with the top-1 chunk of both arms in full for every changed query.

### F6 — Taken with the 2026-09-23 result, AST chunking has now failed to clear the keep rule on three corpora and three language families [INFERRED]

Inferred from F1, F2 and the earlier record's F1/F2: the 12-language eval corpus (−0.009 held-out), this repo's C#, JSAA C#/TS, and ai-badger Python. All point estimates are within ±0.03, and every interval includes zero. The 510-token budget with granite leaves enough context that boundary quality is not the bottleneck. Keeping AST alive would need a different mechanism, such as a symbol or path header per chunk, rather than cleaner cuts.

## Still open

- *(Answered in `2026-09-24-symbol-path-header-on-line-chunks.md` F1: re-embedding unchanged chunks reproduces every metric exactly, so the churn is real.)* The measured churn has no noise floor. A control arm that re-embeds the line chunks unchanged would show how much of the 69 changes is WebGPU embedding nondeterminism rather than chunking. It does not change the null result, but it would say whether any per-query story in F5 is real.
- The queries were written by one model family (Sonnet) from a size-stratified file sample. They are unbiased toward either chunker, since the authors never saw chunks, but they may not match how the owner actually searches.
- *(Measured in `2026-09-24-symbol-path-header-on-line-chunks.md`: not kept.)* Would an AST-derived *header* (enclosing type/member name prepended to each line chunk) help where boundaries did not? That was not tested.
- Only the four extensions above were tested on whole repositories. Go, Rust and C++ were covered only by the small eval corpus.
