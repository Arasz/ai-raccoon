# Ours vs Python retrieval — difference table

Companion to `2026-09-08-mmr-impl-parity.md` (the graded record) and the leg-MMR PoC
(`task/air-mmr-leg-level-poc-branch`, `poc/eval-report.md`). Each row: the difference,
its measured or observed effect, whether ours is superior, and whether to adapt (and on
which side). "Superior" means better retrieval, not closer parity — the two columns
disagree on purpose wherever fidelity to ours would mean adopting the worse behavior.

| # | Difference | Effect | Ours superior? | Adapt? |
|---|---|---|---|---|
| 1 | Corpus scope: whole bank + cross-project shared tier vs 41 files / 1079 chunks | Different candidate pools; shared-tier distractors (the Q2 killers) exist only in ours; max cross-system overlap bounded by the 41-file universe | No — scope choice, not quality | Python only if cross-system diversity claims are needed (L7-lite: add the shared rows); otherwise no |
| 2 | Chunking | None — Python indexed bank chunk values verbatim | N/A (identical) | No |
| 3 | Doc embeddings: stored blobs (possibly multi-epoch weights) vs freshly computed | ~1% cosine noise; no order effect measured inside Python cells | No | Python: read stored blobs (L1) — free, exact, already proven in the RBO run |
| 4 | Query embeddings: C# pipeline vs Python pipeline | Residual gap p50 0.9915 after framing fix; absolute-score comparison off-limits, rank-order approx valid | Yes (reference by definition) | Python: C# id-dump match (L6) or accept the bound |
| 5 | Tokenizer: C# custom wordpiece (drops `$ + < = > ^ \` \| ~`, glues `_xxx` continuations) vs standard BertWordPiece | ~53-token divergence on a 2.4k-char chunk; the entire F2 gap | No — both self-consistent; C# is merely the reference | Python only, and only for parity work (emulation is fragile; prefer the id-dump test as the contract) |
| 6 | Vector leg: dual-vector content+structure α=0.5, per-context KNN vs content-only global brute-force top-50 | Structure signal (headings) missing in Python; per-context windowing missing; biggest leg-order diverger with #7 | Yes (ADR-0004 measured wins) | Python (L5) only if leg parity is needed |
| 7 | Keyword leg: FTS5 + query planner (normalization, fallback) vs `rank_bm25` | Different matcher, different score scales; RRF only sees order, but the orders differ | Yes (planner + fallback) | Python via FTS5-through-sqlite on the copy (L4) — reuses the real index, no planner port |
| 8 | Pre-fusion dedupe: content-collapse, project copy wins vs none | Exact mirrors double-serve in Python; impossible in ours | Yes | Python (L2): lowercase-hex SHA256 of UTF-8 value — trivially portable, proven in the RBO run |
| 9 | Fusion: RRF k=60, 1:1 both sides | None — verified identical formula | N/A (identical) | No |
| 10 | Post-fusion: affinity boost + consolidation + floor + limit vs raw Take(8) | Same-file clusters and weak-tail handling differ systematically | Yes (ADR-0005/0047 measured) | Python only to localize divergence (port for the harness, never for quality) |
| 11 | MMR formula | None — hand port verified behaviorally (reorders on synthetic input) | N/A (identical) | No (sim source converges after L1) |
| 12 | Framework objects: no `QueryFusionRetriever`/MMR helper usable with precomputed vectors in core 0.14.24 | None on results; "LlamaIndex" on the Python side is honestly BM25Retriever + schema types | N/A | No (document, don't disguise) |
| 13 | No-regression reorder | None — flag off in bank, absent in Python | N/A | No |
| 14 | RBO cross-check (L1+L2 applied): overlap 4–6/8, RBO 0.07–0.38; L1+L2 move RBO ±0.03, overlap unchanged | Proves doc-vectors + dedupe are NOT the binding parity constraints; divergence lives in legs (#6, #7) and post-fusion (#10) | — | Next parity spend goes to legs/post-fusion, not embeddings |

Net: adopt L1+L2 on the Python side (done, free); everything else adapts Python toward ours
only when a specific comparison needs it — never the reverse. The one direction that must
never flow backwards is #10: porting affinity into Python for parity must not be mistaken
for evidence about affinity's quality.
