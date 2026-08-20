---
name: hybrid-retrieval-fusion
description: >-
  Use when fusing vector + BM25 retrieval into one ranking.
---

# Hybrid dense+sparse retrieval fusion

Merging two ranked lists (keyword/BM25 + vector similarity) into one ranking. Consensus answer for most systems: **Reciprocal Rank Fusion (RRF) with a swept k and optional per-modality weights** — zero-shot, scale-free, robust to a missing
modality. Graded findings with URLs: `references/hybrid-fusion-findings-2026-08.md`.

## Decision framework

1. **RRF** (`Σ 1/(k+rank)` per list; Cormack, Clarke, Büttcher, SIGIR 2009, k=60):
   default choice. Rank-only → immune to score-scale mismatch (BM25 unbounded vs cosine bounded), outlier-resistant, handles empty lists via COALESCE. Cost:
   rank quantization discards score magnitude.
2. **Weighted RRF**: per-modality weights (Elasticsearch rrf retriever, Qdrant v1.17). Default (1,1); only tune weights against a held-out eval set — Qdrant docs: hand-tuned weights without measurement "unlikely to beat the default".
3. **Score fusion** (CombSUM/CombMNZ; min-max/robinson; Qdrant DBSF 3σ): better when raw scores carry real magnitude, but needs per-query normalization; min-max is outlier-sensitive, per-list scale varies across queries, empty list →
   div-by-zero. Weaviate made min-max (`relativeScoreFusion`) default in v1.24.
4. **Learned fusion / reranking**: cross-encoder rerank over an RRF shortlist is the production-standard stage-2; learned fusion itself is rare. Skip unless a reranker is in scope.
5. Evidence (2025–2026): hybrid beats single-method (recall +15–30%; BEIR nDCG@10 43.4 → 52.6), but RRF vs convex score combination is near-parity when k is swept (T2-RAGBench 2026: RRF k=10 0.716 vs CC α=0.5 0.726 Recall@5; RRF k=60
   0.695). Hybrid + reranker dominates everything.

## Production defaults (as of 2026-08)

| System                 | Fusion                             | Defaults / tuning                                                                          |
|------------------------|------------------------------------|--------------------------------------------------------------------------------------------|
| Elasticsearch 8.12+    | RRF retriever                      | `rank_constant` 60, `rank_window_size` 10, per-retriever `weight` 1.0                      |
| OpenSearch 2.19 (2025) | RRF                                | chose RRF over min-max/L2 explicitly (stability, outliers)                                 |
| Qdrant v1.10+          | RRF + DBSF                         | k default **2** (zero-based), k config v1.16, weighted RRF v1.17; DBSF = (s−(μ−3σ))/6σ     |
| Weaviate               | rankedFusion / relativeScoreFusion | relativeScoreFusion (min-max) default since v1.24; `alpha` 0..1 = vector-vs-keyword weight |
| Vespa                  | none built-in                      | hand-written rank expr, e.g. `closeness * (1 + bm25)`                                      |
| Azure AI Search        | RRF                                | k "small value, such as 60"                                                                |
| sqlite-vec guide       | RRF                                | rrf_k 60, weight_fts/weight_vec 1.0                                                        |

**k sensitivity**: small k → aggressive top-rank emphasis (T2-RAGBench k=10 best; Qdrant ships k=2); large k (60) → top ranks nearly equal (1/60 vs 1/61 ≈ 1.6%). ES `rank_window_size` 10 means docs outside a retriever's top-10 contribute
zero. → Always sweep k; never ship an assumed constant. A sweep that confirms the current defaults are the gate-holding optimum is a VALID outcome, not a failed wave: document it as a measured negative result (full matrix + why each
higher-scoring point violates a gate), close the acceptance criteria the sweep was meant to satisfy (e.g. a previously-degraded query now holding rank 1), and do not force a parameter change the measurement rejects. Sweep through the REAL
pipeline — the harness must call the store's actual search path with only the swept parameters varying, or the grid validates a reimplementation.

## Pure SQL on FTS5 + vec0

RRF is fully expressible (Alex Garcia, Oct 2024):

```sql
with vec_matches as (
  select article_id, row_number() over (order by distance) as rank_number
  from vec_articles where embedding match :q and k = :k),
fts_matches as (
  select rowid, row_number() over (order by rank) as rank_number
  from fts_articles where body match :q limit :k)
select coalesce(1.0/(:rrf_k + f.rank_number), 0.0) * :wf +
       coalesce(1.0/(:rrf_k + v.rank_number), 0.0) * :wv as score
from fts_matches f full outer join vec_matches v on v.article_id = f.rowid
order by score desc;
```

- FTS5 `rank` == `bm25()` result: **lower is better, unbounded negative**
  (sqlite.org/fts5.html §5.1.1) — negate for score fusion.
- `vec_distance_cosine` ∈ [0,2] (= 1 − cos); mapping to similarity gives [1,−1].
- SQLite FULL OUTER JOIN requires ≥ 3.39 (2022); any recent bundle qualifies.
- vec0 KNN needs `k = N` clause; FTS5 MATCH needs `limit`.

## FTS5 query normalization for free-text queries (measured P6 lesson)

**AND-joining user query tokens kills the keyword modality on natural-language queries.** `MATCH 'what does the project decide document about adr 0001 …'` requires a doc to contain EVERY token — on real corpora nothing does, so FTS returns
zero and the sweep silently degrades to vector-only. Measured on 174 docs / 68 queries: AND → nDCG@10 0.6002 (identical at ALL 9 sweep points — the tell), OR → 0.654–0.688 with the sweep differentiating.

- **Join tokens with `OR`** (recall for the keyword list); BM25 + RRF re-rank. Precision is the fusion stage's job, not the MATCH string's.
- **FTS5 grammar traps in raw queries**: `:` parses as a column filter (`"about: ADR-0001"`
  → `no such column: about`), quotes become phrases, bare `AND`/`OR`/`NOT`/`NEAR` become operators. Normalize to alphanumeric tokens only (`[\p{L}\p{N}_]+`), drop the reserved words, lowercase, join with ` OR `. Empty expression → skip the
  FTS list entirely (vector-only COALESCE), never `MATCH ''`.
- **Dead-modality debugging signature**: a k × weights sweep returning IDENTICAL metrics at every point means one list is empty/absent — fix the retrieval, not the fusion constants. Verify per-modality list population before tuning
  k/weights.

## Validating rank changes: content-first triage (user rule, 2026-08-04)

When a fusion/ranking change moves an expected-source rank, the rank delta alone is NOT evidence. Before accepting a regression or celebrating an improvement: **read the actual chunk contents of the competing results**. Three outcomes,
three verdicts:

- **Same-knowledge alternative** — the new rank-1 carries the same decision/info as the expected chunk and often cross-links it ("The formal decision record is ADR-0011 §1"; "The MCP server was deleted; see ADR-0060"). Rank movement is a
  tie-break artifact; the ground-truth file staying in the top-2 bounds the cost.
- **Within-file sibling competition** — the top-1 is another chunk of the SAME document (metadata header above the decision chunk). File rank holds; the exact chunk slips. This is a document-ranking problem (consolidation/document-first),
  not a fusion problem.
- **Real regression** — the outranker is irrelevant content. Fix or gate it.

Also: the expected-source ground truth is a corpus-design artifact (canonical ADR chunks); a rank-1 that is genuinely the better answer for the query is not a bug. Document the trade with the content evidence and amend the gate, exactly
like a measured degradation.

**Knob-sensitivity probes**: when a new fusion component (e.g. a structure-signal α)
is suspected of moving ranks, sweep the knob BEFORE blaming it. If the setting lives in a plain SQLite settings table, write it directly (`UPDATE settings SET value=...`
— no vec0 module needed, unlike the vector tables) and re-probe the same queries. Measured insensitivity (rank unchanged across the whole knob range) means the cause is elsewhere — this session traced an A1 "structure-signal regression" to
the probe using the WRONG query string and to a stale db copy, after the α sweep showed no effect. Verify the probe's query text against the actual catalog, not an assumed paraphrase.

## Structural (heading-path) signal side effects

Adding a structure/heading-path vector modality helps section-targeted queries but has two measured side effects:

- Heading paths contain the query's own words (a section titled "The gluestack → shadcn/ui pivot" beats the canonical ADR titled "ADR-0011: Frontend chassis stack")
  — the structure list can outrank the content list's top hit for content-targeting queries. The fix is document-first/consolidation ranking, not α tuning (the gap is too large for any α in [0.5, 0.9]).
- A structure list with near-uniform similarities (short heading paths) is NOISE for non-section queries — its fused top can be semantically unrelated. Section-targeted queries get strong, stable structure matches; content queries get weak
  ones. Diagnose by probing the pure-structure arm (α=0) per query, not by reading the fused list.

## Score contract when RRF feeds a public API

Fuse → **normalize to max (top result = 1.0)** → apply `minScore` → apply `limit`. The threshold maps onto rank positions, not raw scores: single-list k=60, minScore 0.7 ≈ keeps top ~26 (score at rank r = (k+1)/ (k+r)). Do NOT apply
minScore to raw BM25 (unbounded negative) before normalization. For scope-multiplexed stores, fuse in TWO layers: per context (FTS list + vec list with the configured weights), then merge the per-context batches with the same RRF at uniform
weight — the merger no longer picks "best ranking".

## vec0 integration notes (sqlite-vec, .NET)

- vec0 blob = float32 little-endian, one element per 4 bytes (hand-rolled `BinaryPrimitives`
  writer is fine; no external lib needed).
- Scope-filtered KNN: `FROM vec_entries v JOIN entries e ON e.id = v.rowid WHERE {filter}
  ORDER BY vec_distance_cosine(v.embedding, @queryVector), e.path LIMIT @n` — row position is the rank. Distance ∈ [0,2] (= 1 − cos).
- FTS5 `snippet()` requires a MATCH query context — it CANNOT be used in a scalar subquery for vector-only hits. Give vec-only results a plain text teaser (`substr(value,1,160)`) and let the FTS payload win (first-list payload) when a doc
  is retrieved by both modalities.

## Failure modes to test

- **Absent modality**: keyword-only query with zero FTS5 hits (or no vector hits). RRF fine (COALESCE); min-max score fusion → div-by-zero / undefined branch.
- Outlier scores distort min-max; per-query scale drift (short vs long queries, IDF drift).
- One-element / all-identical lists degenerate normalization (Qdrant DBSF emits 0.5 in that case).
- **Identical sweep output at every point** → one list is empty (see normalization section above).
- **Weight-0 ablation lists still register payloads (score 0)** in weighted RRF: with a vector modality present, (fts=0, vec=1) gives a pure-vector top-N as long as ≥N vector candidates exist (zero-score docs sort below positive scores);
  with NO vector modality (no provider / no embeddings) the vector list is empty and search degrades to FTS-only without crashing. Trap: when the FTS weight is 0 AND the vector list is absent, every fused score is 0/0 = NaN, which the
  minScore filter drops → the query returns EMPTY, not FTS results — you cannot use a (0,1) point to fall back to FTS on a DB without embeddings. Probe the modality's list population explicitly (a vector-liveness test), and provision the
  embedding model before any test that can embed: query-time embedding is provider-gated (no provider ⇒ no query vector ⇒ vector list empty by construction).

## .NET landscape

No mainstream library does fusion. Microsoft.Extensions.VectorData (10.x) and Semantic Kernel are storage abstractions; SK hybrid is connector-level preview and
"sparse vector based hybrid search is not currently supported". Lucene.NET: no RRF (it is an ES-level feature). Only tiny packages exist (`drittich.ReciprocalRankFusion`
1.0.1, ~500 downloads, unverified). Closest prior art: FieldCure.Mcp.Rag (MCP RAG server, FTS5 + cosine + RRF in .NET). → Hand-compose: two ranked lists fused in LINQ, ~20 lines; or the SQL CTE pattern above.

**Checking NuGet for a capability**: discovery endpoint
`curl "https://azuresearch-usnc.nuget.org/query?q=<terms>&take=N"` → JSON `data[]`
with id/version/description/totalDownloads/verified. Use it to confirm a package exists at all and gauge adoption (download counts) — e.g. confirmed Microsoft.Extensions.VectorData 10.8.0, Lucene.Net 3.0.3, drittich.ReciprocalRankFusion
1.0.1 (489 downloads) on 2026-08-03. For versions of a known package, use
`https://api.nuget.org/v3-flatcontainer/<id>/index.json`.

## Golden retrieval harness (parity proof)

When replacing a working search implementation, prove parity empirically:

- **Fixed corpus** snapshot (same FTS5 + vec0 tables).
- **Query set** incl. degenerate cases: exact-keyword, paraphrase/semantic, keyword-only-no-vector-hits, vector-only-no-keyword-hits, misspellings.
- **Labels**: old top-k as weak oracle + manual spot-check.
- **Metrics**: nDCG@10/@20, MRR, Recall@k, plus Kendall-τ rank agreement vs old.
- **Sweep**: k ∈ {10, 30, 60} × weights { (1,1), (1,2), (2,1)}; report degenerate subset separately; p95 latency at corpus size.
- **Pass**: nDCG within tolerance (≈0.02) of old + no degenerate regression.

## References

- references/hybrid-fusion-findings-2026-08.md — graded findings bank: papers (Cormack 2009; T2-RAGBench 2026; BEIR 2025), production docs, NuGet checks — each with URL, date, and MEASURED/READ/INFERRED/UNVERIFIED grade.
- references/fts5-vec0-implementation-notes.md — worked P6 example: the AND→OR FTS5 normalization fix with measured nDCG before/after, the safe normalizer, two-layer RRF for scope-multiplexed stores, vec0 blob/KNN/snippet gotchas, and the
  no-engine degradation path.
- references/hybrid-fusion-operational-pitfalls.md — four applied pitfalls from the ceaksan sources: candidate window K=max (limit*3,100) (starves overlap otherwise — closed a 0.0249 nDCG parity miss), vec_distance_cosine is a DISTANCE
  (rank ascending), snippet fallback for vector-only hits, and chunk size must respect the embedding model's context window (512 > MiniLM's 256 → truncation).
