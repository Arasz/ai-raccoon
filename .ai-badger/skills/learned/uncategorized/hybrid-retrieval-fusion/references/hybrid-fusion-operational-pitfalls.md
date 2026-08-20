# Hybrid fusion — operational pitfalls (applied on SQLite + FTS5 + vec0, 2026-08)

Four pitfalls that surfaced while implementing RRF hybrid search on a managed SQLite store and proving parity against a reference extension. These materially change retrieval quality — check for each when fusing FTS5 BM25 + vec0 lists.

## 1. Candidate window starves overlap

Fusing per-modality lists truncated to `LIMIT @limit` (e.g. 20) silently drops documents that BOTH modalities would have ranked at positions 20–100 — RRF can only fuse candidates it is given. Fetch `K = max(limit*3, 100)` candidates per
modality, fuse over K, then apply limit + minScore.

Measured: this was the biggest single quality lever in a parity run — it closed a 0.0249 > 0.02 nDCG@10 miss at sweep point k=10, weights (1,1).

## 2. vec_distance_cosine is a DISTANCE, not a score

`vec_distance_cosine` ∈ [0, ~2] (0 = identical, = 1 − cosine). Rank the vector list ASCENDING by distance and feed only rank positions into RRF. An inverted sort, or feeding the raw distance as a "score" into a weighted sum, silently
destroys fusion (distance is unbounded-ish and inversely correlated with relevance). Pin with a test: vector results ordered by ascending distance.

## 3. Snippet gap for vector-only hits

FTS5 `snippet()` only produces output under a MATCH — vector-only results (no keyword overlap) come back with an empty snippet, violating any snippet-on-every-result contract. Fallback: deterministic trim of the stored value (~200 chars +
'…'), keyed by hash. Test: a vector-only query result carries a non-empty snippet.

## 4. Chunk size must respect the embedding model's context window

A default chunk size of 512 tokens exceeds all-MiniLM-L6-v2's 256-token window — every large chunk is truncated at embed time, exactly the dilution that small-focused-chunks findings describe (~340-char chunks beat 1.5KB multi-topic
chunks ~2.3× on similarity). Tie chunk maxTokens/overlay to the configured model's known context (256 / overlay 32–48 for MiniLM), and make chunk size a knob. A contract that pins "bounds, not sizes" (e.g. FR-NM-10) makes the default change
safe.

## Also worth sweeping

- k sensitivity: T2-RAGBench 2026 shows RRF k=10 (0.716) ≈ convex α=0.5 (0.726)
  > k=60 (0.695) — sweep k, never ship an assumed constant.
- FTS5 `rank` == bm25 () is unbounded NEGATIVE (lower = better); only rank positions matter for RRF, but any score-based fusion needs negation + per-query normalization with degenerate-list guards.

Sources: ceaksan.com hybrid-search-fts5-vector-rrf (fetched full 2026-08-03); rag-chunking-guide paywalled (public parts only). Findings graded in hybrid-fusion-findings-2026-08.md.
