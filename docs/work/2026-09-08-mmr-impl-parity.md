# Research: implementation differences vs the Python counterpart, and the parity ladder

**Date:** 2026-09-08
**Question:** What differs between our retrieval pipeline and the Python/LlamaIndex counterpart built for the leg-MMR PoC, and what would it take to make their results comparable?

```chart:range
title: fresh-pipeline vs stored-blob cosine, n=192 eval chunks (Apple M4, SFR ONNX)
doc parity gap: 0.903..0.988..0.997
```

```chart:matrix
title: parity steps by implementation cost and gap closed
step, cost, closes
stored blobs as python doc vectors, low, doc-side fully
ContentHash dedupe port, low, pre-fusion shape
per-stage RBO harness, low, localizes all divergence
FTS5-via-sqlite BM25 leg, medium, keyword-leg scores
structure-leg port, medium, vector-leg scores
tokenizer id-dump match, low, query-side fully
full-bank python corpus, high, scope asymmetry
```

## Findings

### F1 — Chunking is identical by construction: same 41 files, same 1079 chunk texts [MEASURED]

The Python corpus was built by selecting exactly the source files appearing in either
ours-cell's memory top-8s and indexing every embedded chunk value verbatim — no
re-chunking, no truncation beyond the bank's own. Whatever differs downstream cannot be
blamed on segmentation.

**Evidence:** `/tmp/mmr-py-eval.py` corpus build run 2026-09-08 on Apple M4, read-only on
`/tmp/mmr-bank-copy/p0a/memory.db`, printing `files: 41` / `chunks: 1079`.

### F2 — Same weights, different pipeline: fresh embeddings reproduce stored blobs at 0.903–0.997, never 1.0 [MEASURED]

Cosine(fresh ONNX embed, stored blob) over 192 eval chunks: min 0.9032, p25 0.9821, p50
0.9882, p75 0.9923, max 0.9970, zero at ≥0.999. The five newest rows (embedded same-day)
plateau at 0.982–0.994 too, so this is NOT stale weights across bank epochs — it is a
systematic pipeline gap. Special-token variants narrow it slightly (no-special 0.99581 >
CLS-only 0.99507 > CLS+SEP 0.99294 on one row) but never close it, so the residual is in
subword splitting, not framing. Consequence: the Python side is internally consistent
(same pipeline for query and docs, so python±MMR stands), but absolute-score comparison
against ours is off-limits; rank-order comparison is approximately valid.

**Evidence:** three ONNX scripts run 2026-09-08 on Apple M4 (`SFR-Embedding-Code-400M_R`
`model.onnx`, CPUExecutionProvider, WordPiece lowercase, CLS-pool, no norm), read-only on
the P0a copy; n=192 sample (5 chunks × 41 files ordered by rowid, first 5 taken), fresh-row
check ordered by `created_at desc`, single-row special-token sweep; distribution saved to
`/tmp/mmr-stale.json`.

### F3 — Fusion math is identical on both sides: RRF k=60, weights 1:1 [READ]

Both fuse two legs as score = Σ weight/(k+rank) and take the top-8 of the fused order.
The Python RRF is a six-line port of the C# formula, same k, same unit weights. Fusion is
the one stage that needs no parity work.

**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/Memory/ReciprocalRankFusion.cs:8`
(doc: "score = sum of weight / (k + rank)") and `:65` (the accumulation);
`/tmp/mmr-py-eval.py:52-57` (the port).

### F4 — Everything around fusion differs, in six named places [READ]

1. **Corpus scope.** Ours searches the whole bank per scope (committed + shared +
   custom + workspace, ~53k embedded rows, cross-project shared tier included);
   Python searches 41 files. The shared-tier distractors that decided Q2 exist only
   on one side.
2. **Vector leg.** Ours is dual-vector: content + structure KNN per context fused by
   `StructureFusion` at α=0.5 (`StructureFusion.cs:6-26`, driven at
   `SqliteMemoryStore.cs:838`); Python is brute-force content cosine, global top-50,
   no headings, no α, no per-context windows.
3. **Keyword leg.** Ours is FTS5 BM25 over the bank index with the C# query planner
   (normalization, fallback); Python is `rank_bm25` Okapi over tokenizer-split text —
   different tokenization, different score scales (FTS ranks feed RRF, so only order
   matters, but the orders come from different matchers).
4. **Pre-fusion dedupe.** Ours collapses byte-identical content per leg, project copy
   wins (`ModalityCandidates.cs:26-55`); Python has none (its corpus was deduped by
   selection, not by rule).
5. **Post-fusion.** Ours applies source-affinity boost + consolidation + max-normalize +
   relative floor + limit (`SearchResultMerger.cs`); Python takes the fused top-8 raw.
6. **Framework objects.** `QueryFusionRetriever` requires a hosted embed model (hit the
   `llama-index-embeddings-openai` ImportError wiring precomputed vectors in), and no
   `get_top_k_mmr_embeddings` exists in core 0.14.24 — so Python fusion and MMR are
   hand-rolled to the same formulas, not the LlamaIndex objects. "LlamaIndex" on the
   Python side is honestly `BM25Retriever` + node/schema types only.

**Evidence:** `src/AiRaccoon.Infrastructure/Embedding/StructureFusion.cs:6-26` and `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs:838` (dual-vector leg); `src/AiRaccoon.Infrastructure/Sqlite/Memory/ModalityCandidates.cs:26-55` (pre-fusion dedupe); `/tmp/mmr-py-eval.py:52-57` and the BM25Retriever wiring near `:81-96` (brute-force cosine top-50, hand-rolled RRF/MMR); the `llama-index-embeddings-openai` ImportError hit wiring precomputed vectors into `VectorStoreIndex` on 2026-09-08.

### F5 — The parity ladder, cheapest first [INFERRED]

Reasoning from F1–F4, ordered by cost per unit of gap closed: **L0 chunking — done (F1).**
**L1 stored blobs as Python doc vectors** (one SELECT; exact doc-side parity free, kills
the F2 gap on the doc side while leaving the query side). **L2 ContentHash dedupe port**
(`OfValue` = lowercase hex SHA256 of UTF-8 bytes — verified trivially portable — so the
pre-fusion shape matches). **L3 per-stage RBO harness** (compare legs, fused, final
separately with rank-biased overlap; localizes every future divergence to a stage instead
of relitigating the whole pipeline). **L4 FTS5-via-sqlite BM25 leg** (run the normalized
query through the copy's FTS5 index from Python; reuses the real matcher without porting
the planner). **L5 structure-leg port** (markdown heading-path parse + heading embeds +
α=0.5). **L6 tokenizer id-dump match** (dump C# ids for fixed strings, diff against
Python ids — no model runs — closes F2's query side). **L7 full-bank Python corpus**
(~53k embeds, hours of CPU; needed only if scope asymmetry itself is under test — for
MMR questions the 41-file universe plus L1–L3 suffices).

### F6 — Tokenizer divergence isolated to three named causes, one fixed, residual bounded [MEASURED]

C# `EncodeToIds` output dumped via a temporary xunit test (deleted after use) for fixed
strings and diffed token-by-token against the Python pipeline — no model runs. Causes:
(a) MY framing bug: `tokenizers` `encode()` already adds the CLS/SEP pair and the eval
prepended a second one (fixed; single change lifts max stored-blob cosine to exactly 1.0000
and p50 0.9882→0.9915 on the n=192 sample); (b) the C# basic tokenizer drops standalone
`$ + < = > ^ \u0060 | ~` (measured on a 95-char ASCII sweep: Python 142 ids vs C# 133, the
9-char set exactly the difference; both C# implementations agree with each other);
(c) underscore asymmetry: C# glues `_xxx` into continuation pieces (`_share` → `##_ ##sha
##re`) while standard WordPiece splits `_` off (`_` + `share`) — full emulation means
porting their wordpiece loop, judged disproportionate for a PoC eval. Residual effect
bounded: p50 0.9915, worst row 0.898 on the reframed sample.

**Evidence:** `TempTokDumpTests` (temporary, in worktree `task/air-mmr-tokdump`, file removed
after the run) dumping `WordPieceEmbeddingTokenizer.Create` and
`OnnxEmbeddingGenerator.CreateTokenizer` ids for 5 fixed strings + full ASCII sweep +
one 2430-char real chunk (`/tmp/mmr-ids.txt`); Python diff (`280 vs 227` tokens on the
real chunk, 9-char drop-set on ASCII); reframed n=192 staleness re-run
(`/tmp/mmr-stale2.json`: min 0.8977, p50 0.9915, max 1.0000, 11% ≥0.999), Apple M4.

### F7 — L1+L2 change almost nothing cross-system: overlap 4–6/8, RBO 0.07–0.38, deltas ±0.03 [MEASURED]

Re-ran the Python side with stored blobs as doc vectors (L1) plus the ContentHash dedupe
port (L2) and compared all three Python variants against ours-off final top-8s with
rank-biased overlap (p=0.9) plus set overlap: set overlap 4–6/8 on every query in every
variant; RBO 0.072–0.377; applying L1+L2 moves RBO by ±0.03 and overlap not at all.
Doc vectors and dedupe are therefore NOT the binding parity constraints — the remaining
divergence lives in the legs (F4.2, F4.3) and post-fusion (F4.5), which is where the next
parity spend goes. Companion: `2026-09-08-mmr-impl-diff-table.md` row 14.

**Evidence:** `/tmp/mmr-rbo.py` run 2026-09-08 on Apple M4 (variants A-fresh, B-stored+dedup,
C-+mmr; RBO + overlap printed per query); `ContentHash.cs:30-34` (dedupe portability).

## Still open

- Full wordpiece-loop emulation (F6's residual): port-or-accept decision, currently accept.
- L4/L5/L7 (FTS leg, structure leg, full-bank corpus): unstarted; L7-lite (shared rows only)
is the only one any diversity claim needs.
- Whether the shared-tier distractor pool should be mirrored into the Python corpus before any
cross-system diversity claim is made.
