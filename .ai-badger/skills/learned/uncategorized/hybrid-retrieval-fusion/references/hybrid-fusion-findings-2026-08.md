# Hybrid fusion findings — graded evidence bank (2026-08-03)

Research for ai-raccoon's SqliteMemoryStore search layer (replacing the pinned sqlite-memory 1.3.5 extension whose fusion math is unreadable C). Grades:
MEASURED (primary source fetched/executed), READ (official doc/paper quoted), INFERRED (reasoned, no direct source), UNVERIFIED (conflicting/secondary only).

## Papers

- **RRF origin** — Cormack, Clarke, Büttcher, SIGIR 2009: `RRFscore(d) = Σ 1/(k+rank)`, k=60; beat best individual system, Condorcet Fuse, CombMNZ on TREC (MAP 0.6051). **MEASURED** (PDF text
  extracted): https://cormack.uwaterloo.ca/cormacksigir09-rrf.pdf ; ACM https://dl.acm.org/doi/10.1145/1571941.1572114
- **RAG-Fusion** — Rackauckas, arXiv:2402.03367 (Feb 2024): RRF over LLM-generated query variants; **manual evaluation only, no NDCG numbers**. **MEASURED** (abstract).
- **⚠️ Misattribution to check** — blogs claim "hybrid improves NDCG 26–31%" citing 2402.03367; that paper contains no such numbers. Real support: COLING 2025 Li et al. "Enhancing RAG: best practices" (cited in 2604.01733 as [16]) — hybrid
  improves **recall** 15–30% over single-method. **UNVERIFIED** for the 26–31% figure.
- **T2-RAGBench (2026)** — "From BM25 to Corrective RAG", arXiv:2604.01733 (Apr 2026), 23,088 queries / 7,318 financial docs: RRF k=10 best (Recall@5 0.716); convex score combination α=0.5 (0.726) beats RRF k=60 (0.695); hybrid RRF +
  cross-encoder rerank dominates all methods. **READ** — https://arxiv.org/html/2604.01733v1
- **BEIR (2025)** — "From Retrieval to Generation", arXiv:2502.20245 (Feb 2025):
  hybrid nDCG@10 43.42 (BM25) → 52.59. **READ** (snippet).
- **NQ hybrid** — DPR 48.7% → hybrid 53.4% top-1 (Abdallah et al., 2025-02-27). **UNVERIFIED** (secondary via emergentmind).

## Production systems (fetched 2026-08-03)

- **Elasticsearch** rrf retriever: `rank_constant` 60 (≥1), `rank_window_size` 10, per-retriever `weight` 1.0; weighted score = Σ wᵢ·rrfᵢ. **READ** —
  https://www.elastic.co/docs/reference/elasticsearch/rest-apis/retrievers/rrf-retriever
- **OpenSearch 2.19** (Feb 12, 2025): RRF in Neural Search; rationale: RRF >
  min-max/L2 normalization (stability across score distributions, outlier resistance, cross-list consistency). **READ** —
  https://opensearch.org/blog/introducing-reciprocal-rank-fusion-hybrid-search/
- **Vespa**: no RRF; hybrid = OR of top-k ops (`weakAnd` + `nearestNeighbor`) + hand-written rank expression, documented example `closeness * (1 + bm25(title) + bm25(text))`.
  **READ** — https://docs.vespa.ai/en/learn/tutorials/hybrid-search.html
- **Weaviate**: vector + BM25F; `rankedFusion` (RRF) vs `relativeScoreFusion`
  (min-max: max→1, min→0), **relativeScoreFusion default since v1.24**; `alpha`
  0..1 (0 = BM25 only, 1 = vector only). **READ** —
  https://weaviate.io/blog/hybrid-search-fusion-algorithms (Aug 29, 2023) + docs.
- **Qdrant** (v1.10+): RRF (**k default 2**, zero-based; k configurable v1.16; **weighted RRF v1.17**) + **DBSF** (v1.11: ŝ = (s− (μ−3σ))/6σ, unclipped; degenerate all-equal sets → 0.5) + Formula Query. Docs ship `tune_rrf_weights`
  grid-search notebook; "hand-tuned weights without measurement are unlikely to beat the default". **READ** —
  https://qdrant.tech/documentation/search/hybrid-queries/ (raw markdown on GitHub).
- **Azure AI Search**: RRF `1/(rank+k)`; "performs best when you set k to a small value, such as 60". **READ** — https://learn.microsoft.com/en-us/azure/search/hybrid-search-ranking
- **sqlite-vec ecosystem**: Alex Garcia hybrid guide (Oct 2, 2024) — 3 patterns:
  keyword-first (UNION ALL), vector-first re-rank, RRF in pure SQL (rrf_k 60, weight_fts/weight_vec 1.0). **MEASURED** (SQL extracted) —
  https://alexgarcia.xyz/blog/2024/sqlite-vec-hybrid-search/index.html

## .NET / NuGet (fetched 2026-08-03)

- **Microsoft.Extensions.VectorData** 10.8.0: storage abstraction, **no fusion API**. **MEASURED** (NuGet API) + **INFERRED**.
- **Semantic Kernel**: hybrid search connector-level preview; "sparse vector based hybrid search is not currently supported" at the abstraction. **READ** —
  https://learn.microsoft.com/en-us/semantic-kernel/concepts/vector-store-connectors/hybrid-search
- **Lucene.NET** 3.0.3: no RRF (ES-level feature). **INFERRED**.
- **drittich.ReciprocalRankFusion** 1.0.1: pure RRF merge, 489 downloads, unverified. **MEASURED** (NuGet API).
- **FieldCure.Mcp.Rag** 2.5.1: MCP RAG server, FTS5 + cosine + RRF in .NET — closest prior art to ai-raccoon's planned layer. **MEASURED** (NuGet API).
- Discovery endpoint used: `https://azuresearch-usnc.nuget.org/query?q=<terms>&take=N`.

## SQLite feasibility facts

- FTS5 `rank` == `bm25()` result; "better matches are assigned numerically lower values" (unbounded negative). **MEASURED** — https://www.sqlite.org/fts5.html §5.1.1
- `vec_distance_cosine` ∈ [0,2] (= 1 − cos); range **INFERRED** from definition (function listed in https://alexgarcia.xyz/sqlite-vec/api-reference.html).
- SQLite FULL OUTER JOIN needs ≥ 3.39 (2022). **INFERRED** (bundle is newer; verify once with `select sqlite_version()`).
- RRF pure-SQL pattern: row_number () per list → FULL OUTER JOIN → COALESCE (1/ (k+rank),0)·weight → ORDER BY score DESC. **MEASURED** (Alex Garcia SQL).

## Harness spec (parity vs old extension)

Fixed corpus snapshot; query set incl. degenerate cases (keyword-only-no-vector, vector-only-no-keyword, misspellings); labels = old top-k as weak oracle + manual spot-check; metrics nDCG@10/@20, MRR, Recall@k, Kendall-τ vs old; sweep k
{10,30,60} × weights { (1,1), (1,2), (2,1)}; p95 latency; pass = nDCG Δ ≤ 0.02 and no degenerate regression.
