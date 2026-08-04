# Retrieval Improvement Plan C — Measured Structure

> **Based on:** Plans A (source-first architecture) and B (measurement-first, algorithmic)
> **Approach:** B's measurement discipline as foundation, A's structural depth as the build
> **Merged:** 2026-08-04 — takes best parts of both, resolves tensions, closes gaps

---

## 0. The Foundation: The Baseline Must Be Reproducible

Plan B discovered that the committed baseline is unreproducible:

1. The committed `jsaa-memory.db` has 6675 entries (not 681), 0 embeddings, and 71% from excluded `docs/work/`
2. `RetrievalBaselineTests` hardcodes `IsExpectedSource=false` and `ExpectedSourceMatchesAtTop3=0`
3. The report's hybrid numbers cannot come from the committed DB (no vectors exist)
4. `scripts/chunk-hash-map.json` is absent — expected-source detection is disabled

**Every improvement in this plan is gated on Wave 0.** No structural or algorithmic change ships until the baseline reproduces on a clean checkout.

---

## 1. What Works (verified against the committed DB)

| Query | Expected | FTS-only rank (committed DB) | Report claim |
|-------|----------|------------------------------|-------------|
| A1 shadcn/ui | ADR-0011 | 1 | 2 |
| A3 offer-page fetch | ADR-0006 | 1 | 4 |
| A4 MCP server | ADR-0060 | 1 | 5 |
| A6 data erasure | ADR-0067 | 4 (ADR-0068 at 1) | NOT FOUND |
| A7 ADR-0070 | ADR-0070 | NOT FOUND | NOT FOUND |

1. Whole-file-path indexing works — distinctive tokens ("mcp", "offer-page") find the right file
2. 100% coverage holds even in FTS-only mode
3. Short, keyword-dense documents (invariants) win at rank 1 — BM25's length normalization is an ally
4. Fusion/sweep measurement infra exists (`SweepRunner`, `SweepMatrix`, `RetrievalMetrics`, `ManagedHarness`)

---

## 2. What's Broken — Root Causes

### 2.1 The measurement harness is disconnected (critical — from B)

The report, the committed DB, the C# test, and the Python runner are four different realities. No improvement can be trusted until they converge.

### 2.2 FTS query construction destroys precision (from A §4.2D + B §2.3)

`"What is ADR-0070 about?"` → `what OR is OR adr OR 0070 OR about`. Stopwords drown the signal; BM25's length normalization then ranks short docs (README.md) above the actual ADR.

### 2.3 ADR chunk sibling competition (from A §2.1)

ADR-0067's chunks compete against each other and against ADR-0068's chunks. The system knows they're from the same file but has no structural boost for source affinity.

### 2.4 Source identity is write-only (from B §2.2)

`memory_write(context="docs:adr")` stores `scope='custom'` — invisible to `SearchContexts.For`. The ingest script smuggles the context into content text. The `## Source:` header and `[context]` prefix pollute BM25 scores and make hash matching fragile.

### 2.5 No modality attribution (from B §2.5)

We don't know which queries are keyword-carried vs semantic-carried. The committed DB has no vectors, yet the report claims hybrid numbers.

### 2.6 Corpus pollution (from B §2.6)

71% of the committed DB is from excluded `docs/work/` — negative tests are meaningless.

### 2.7 Baseline blind spots (from A §2.5 + B §2.6)

No per-query relevance grading, no ablation analysis, no difficulty stratification, no cross-chunk relevance scoring.

---

## 3. Improvement Plan

### Wave 0 — Reproducible Baseline (gate for ALL subsequent waves)

**From B §4.1, hardened with A's measurement ideas:**

1. **Regenerate the canonical corpus** via `ingest-jsaa-docs.py` with the curated pipeline. Use the jsaa tree pinned at the commit recorded in `ai-raccoon`'s submodule or dependency manifest. Commit `jsaa-memory.db` + `chunk-hash-map.json` together. **Exclusion list:** `docs/work/`, `docs/state.json`, `docs/now.md`, `.ai-badger/state.json`, and any other non-ADR, non-invariant content. The exact exclusion globs must be documented in the ingestion script and asserted in the harness.
2. **Fix `RetrievalBaselineTests`:** compute expected-source matches from the hash map or path prefix; assert actual match rate; emit machine-readable JSON report.
3. **Embed the corpus** before measuring hybrid: one `memory_configure` + `embed_pending` step using the bundled all-MiniLM-L6-v2 ONNX model; assert `embedded_count == entry_count`.
4. **Wire `SweepRunner`/`RetrievalMetrics` to the JSAA baseline:** nDCG@5, MRR, recall@5 per query category, with ablation (FTS-only, vector-only, hybrid) per query.
5. **Corpus integrity assertions** (from B §3.1 R2): excluded dirs absent, included files present, embed_state='embedded' for 100%.

**Gate:** Baseline reproduces on clean checkout; two consecutive runs produce identical top-5 results per query (identical hashes at identical ranks — the report JSON is the determinism target); report includes modality attribution (FTS-only / vector-only / hybrid per query) and retrieval metrics (nDCG@5, MRR, recall@5).

---

### Wave 1 — Query Construction (algorithmic quick win)

**From B §4.2, merged with A §4.2D:**

1. **Stopword removal** in `FtsQueryNormalizer`: strip `what, is, the, how, does, about, are, do, can, should, will, would, could, has, have, been, was, were, being, a, an, in, on, at, to, for, of, by, with, from`
2. **AND for identifier queries:** detect `\bADR-\d+\b` → emit `adr AND <number>`. Also detect bare numbers after ADR context, UUIDs, and other identifier patterns. For all other queries ≤4 tokens: use implicit AND. For longer: keep OR for recall.
3. **Bigram phrase extraction** (from A): for queries with ≥3 content tokens under OR semantics (longer queries), add adjacent token pairs as quoted phrases. Under AND semantics (short queries), bigrams add no additional constraint — skip them to avoid redundant FTS terms.
4. **Re-run full baseline** against the reproducible corpus, including the diagnostic triplet: Q1 "What is ADR-0070 about?" (full question), Q2 "ADR-0070" (identifier-only), Q3 "documentation structure trust model" (content-only, no number). This isolates whether failures are tokenization (Q2 fails, Q3 works), content spread (all fail), or ranking (found, wrong rank).

**Expected effect:** ADR-0070 query transforms from `what OR is OR adr OR 0070 OR about` to `adr AND 0070` — noise tokens disappear, BM25 concentrates on signal.

**Gate:** All existing expected-source queries (see Appendix A) maintain or improve rank. ADR-0070 (Q1/Q2) found at rank ≤3. Invariant queries (C1 "Is TDD required?", C2 "screaming architecture", C5 "hardcoded secrets") unchanged at rank 1.

---

### Wave 2 — Source as First-Class Citizen (structural foundation)

**Merged from A §4.1 and B §4.3:**

#### 2a. Schema: add `source_file` column

```sql
ALTER TABLE entries ADD COLUMN source_file TEXT;
```

- For `IngestFileAsync`/`IngestDirectoryAsync`: set to the original relative path (e.g. `docs/adr/0011-frontend-chassis-stack.md`)
- For `memory_write`/`AddContentAsync`: NULL or caller-provided label
- All chunks from the same file share the same `source_file`

#### 2b. Expose source identity in `MemorySearchResult`

```csharp
public sealed record MemorySearchResult(
    string Hash, int Seq, double Ranking, string Path,
    string Snippet,
    string? SourceFile,      // original file path
    int ChunkIndex,           // position within source (0-based)
    int TotalChunks           // total chunks in source
);
```

#### 2c. Index source separately for FTS (B's key addition)

Add the structured source path to the FTS index as an additional column with a column weight, so `ADR-0070` and `decision` match the *source path*, not the content text. This requires rebuilding `entries_fts` (currently an external-content table on `entries(value)`) to include `source_file` as a weighted column — a non-trivial schema migration involving FTS5 content-table rebuild and updated sync triggers. FTS5's `bm25(fts, 0.0, 1.0)` per-column weights control the relative importance of source vs content matches.

#### 2d. Stop embedding provenance in content (B's insight)

The `## Source:` header and `[context]` prefix currently pollute BM25 and make hash matching fragile. Move provenance to the `source_file` column and stop prepending it to chunk text.

**Hash stability note:** Wave 0 commits a canonical DB with the old format (provenance embedded in content). Wave 2d changes ingestion so *new* writes omit provenance from content text. The canonical DB is NOT regenerated after Wave 2d — provenance-removal only benefits future writes. Metrics in Waves 3-5 are measured against the Wave 0 corpus (which includes `## Source:` headers), so the true benefit of 2d is verified by comparing a fresh re-ingestion against the old corpus after 2d lands.

#### 2e. Make context labels searchable (B §4.3.4)

Either include `scope='custom'` rows when a context filter is requested, or map context labels into the project scope with a `context_label` filter on search. This restores the design's §2.2 promise.

**Gate:** Q4 "What does ADR-0011 decide?" finds the Decision-section chunk at rank ≤3. Results carry source identity (SourceFile, ChunkIndex, TotalChunks populated for ingested content). A query for a source path (e.g. `docs/adr/0011-frontend-chassis-stack.md#decision`) returns that exact chunk. No regression on invariants (C1, C2, C5 at rank 1).

---

### Wave 3 — Source-Affinity Scoring (ranking improvement)

**From A §4.1C, tuned with B's sweep discipline:**

1. **Adjacent chunk boost:** If chunk N from source S ranks well, chunk N±1 from the same source gets a boost (λ ∈ {0.05, 0.1, 0.2} — sweep to select).
2. **Source consolidation:** For each source file, take the best-scoring chunk and optionally merge its adjacent siblings into a single result (reducing sibling competition noise).
3. **Document-first ranking:** After per-chunk scoring, compute a document score = max(chunk scores) or mean(top-3). Use as a secondary sort key so documents with any strong match rank as a block before documents with only weak matches.
4. **Parameter sweep** over boost λ, consolidation threshold, and document-score formula against the full baseline.
5. **BM25 length normalization investigation** (from B §4.4.1): Long ADR chunks lose to short docs (README.md) purely on length. Analyze whether BM25's length normalization is the root cause of remaining ranking failures after Wave 1 query construction. Options: (a) reduce chunk max tokens for ADR-like content, (b) accept it and let source-column matching carry the load, (c) test FTS5 column weights to zero-out content-length normalization for source matches. Decide with a sweep.

**Gate:** At least 8/10 expected sources (see Appendix A) at rank ≤3. No regression on invariants.

---

### Wave 4 — RRF Parameter Optimization

**From B §4.4, extended with A §4.3G:**

Grid search using the existing `SweepMatrix`:
- k ∈ {10, 30, 60, 120}
- Weight ratios: (1:1), (1:2), (2:1), (1:0 FTS-only), (0:1 vector-only)
- minScore ∈ {0.0, 0.3, 0.5, 0.7}
- Candidate window: max(limit×3, 100) vs max(limit×5, 50) (from A §4.3I — a smaller window may reduce sibling competition noise with 6675+ entries)

Select the Pareto-optimal point on nDCG@5 and MRR. Document the choice in an ADR with sweep data.

Additionally, measure what fraction of *correct* results sit below minScore=0.7 (the MCP default). If the default silently drops rank-3+ results that are correct, the sweep data makes the trade-off visible.

**Gate:** Chosen parameters beat the current defaults (k=60, 1:1, minScore=0.0) on nDCG@5 without regressing invariants. RRF hybrid must be ≥ max(FTS-only, vector-only) in rank terms for every expected-source query (no fusion regression — from B §3.3). Sweep results committed alongside ADR.

---

### Wave 5 — Baseline Enrichment & Corpus Hygiene

**From A §4 Wave 4 and B §4.5:**

1. Add query difficulty stratification (easy/medium/hard/very-hard)
2. Add per-query relevance grading (1-5 scale, not just binary isExpectedSource)
3. Compute nDCG@5, recall@5, MRR for all queries including new structural cases
4. Add new structural and cross-document test cases from both plans
5. Re-ingest with curated exclusions so negative tests are meaningful
6. Add permanent corpus-integrity assertions to the harness

**Gate:** Baseline report includes ablation breakdown, retrieval metrics, difficulty stratification, and corpus integrity checks. H1-H3 negative tests (see Appendix A) pass because the corpus excludes the content, not by luck of ranking.

---

## 4. Success Metrics

| Metric | Current | Target |
|--------|---------|--------|
| Baseline reproducible from clean checkout | No | Yes |
| Expected-source match rate @3 | Unverifiable | ≥80% |
| ADR-0070 found in results | No | Rank ≤3 |
| ADR-0067 + ADR-0068 both in top 5 for "erasure" | 0068@1, 0067@4 | Both in top 3 |
| Invariant match rate @1 | 100% | 100% (no regression) |
| Modality attribution per query | None | In report |
| nDCG@5 / MRR / recall@5 computed | No | Yes |
| Excluded-corpus integrity | 71% pollution | 0% pollution |
| RRF parameters | Unswept defaults | Swept, Pareto-selected, ADR'd |
| Source identity in results | No | Yes (SourceFile, ChunkIndex, TotalChunks) |
| Context labels searchable | No | Yes |

---

## 5. Out of Scope

- Embedding model change (all-MiniLM → OpenAI text-embedding-3) — separate evaluation
- Chunk size changes (requires model change first)
- Cross-encoder re-ranking — separate infrastructure
- LLM query expansion — separate feature
- Shared-context / workspace-scoped search improvements
- Cross-project retrieval

---

## 6. Open Questions

1. **Canonical corpus pinned:** Regenerate via `ingest-jsaa-docs.py` from the jsaa tree at the commit pinned in Wave 0 Step 1. The current DB (6675 entries, raw ingest) is the wrong corpus — it was produced by a different pipeline and cannot reproduce the report. (RESOLVED)
2. **Source-file contract across repos:** If jsaa ingests via its own pipeline, the structured path format must be agreed between repos — both `source_file` schema and FTS source column depend on that contract.
3. **Stopword list:** Fixed list (as specified) vs corpus-derived token frequency. Start fixed; revisit only if a query regresses.
4. **minScore default:** Is 0.7 a product decision or an accident? Sweep data will show the cost of each threshold; the product owner decides.
5. **Context-label searchability semantics:** Should `memory_search(context="docs:adr")` filter only that label, boost it, or both? Plan B §4.3.4 proposes filter; A is silent.
6. **ChunkIndex stability:** When a file is re-ingested and chunks change, do indices shift? Current dedup by path+hash means unchanged content is a no-op, but new chunks shift indices for the same file.
7. **Document-first ranking vs shared context:** Shared entries have no `source_file`. Should they participate in document-first ranking or be treated as independent atoms?

---

## 7. Dependency Graph

```
Wave 0 (reproducible baseline)
 ├── Wave 1 (query construction) ── independent, can start after 0
 ├── Wave 2 (source identity)    ── independent, can start after 0
 │    └── Wave 3 (source-affinity scoring) ── depends on 2 (needs source_file)
 │         └── Wave 4 (RRF sweep) ── depends on 3 (ranking changes affect sweep)
 └── Wave 5 (baseline enrichment + hygiene) ── runs after 4 to measure the final state
```

Waves 1 and 2 can run in parallel after Wave 0 since they touch different code paths (`FtsQueryNormalizer` vs `MemorySchema`/`SqliteMemoryStore`/`MemorySearchResult`). Waves 3 and 4 are sequential (3 changes ranking, 4 tunes it).

---

## 8. Differences From Plans A and B

| Aspect | Plan A | Plan B | Plan C |
|--------|--------|--------|--------|
| First step | Source schema changes | Reproducible baseline | Reproducible baseline (B's win) |
| Query construction | OR→AND for ≤4 tokens, bigrams, stopwords | Stopwords + identifier detection | Both, merged: stopwords + AND for identifiers + bigrams |
| Source identity | source_file column + ChunkIndex/TotalChunks in results | FTS source column weighting + searchable context | Both: schema + results API from A, FTS indexing from B |
| Ranking improvements | Adjacent boost, consolidation, document-first | RRF sweep, minScore tuning, BM25 length analysis | Both, sequenced: A's structural boosts first, B's sweep after |
| Measurement | New baseline cases | SweepRunner + RetrievalMetrics wired to JSAA | B's infrastructure + A's test cases |
| Corpus hygiene | Not addressed | Wave 4 dedicated to it | Included in Wave 5 with permanent assertions |
| Context labels | Not addressed | Searchable context filter | Included in Wave 2 |

---

## 9. Appendix A — Query Catalog

All queries referenced by gates in this plan. The 10 expected-source queries (A1-A10) are the primary evaluation set; invariants (C1-C5) are the regression guard; hygiene queries (H1-H3) are negative tests.

### Expected-Source Queries (A1-A10)

| ID | Query | Expected Source |
|----|-------|----------------|
| A1 | "Why was shadcn/ui chosen for the frontend?" | ADR-0011 |
| A2 | "How are UUIDs generated?" | ADR-0004 |
| A3 | "How does offer-page fetching handle security?" | ADR-0006 |
| A4 | "What happened to the MCP server?" | ADR-0060 |
| A5 | "What NFRs govern LLM cost?" | ADR-0046 |
| A6 | "How does data erasure work?" | ADR-0067 |
| A7 | "What is ADR-0070 about?" | ADR-0070 |
| A8 | "What does ADR-0011 decide?" | ADR-0011 (Decision section) |
| A9 | "ADR-0046 vs ADR-0022: which was retired?" | ADR-0046 |
| A10 | "List all ADRs that reference Cosmos DB" | Multiple ADRs |

### Invariant Queries (C1-C5 — must stay at rank 1)

| ID | Query | Expected Source |
|----|-------|----------------|
| C1 | "Is TDD required?" | TDD invariant |
| C2 | "What is the screaming architecture rule?" | Screaming architecture invariant |
| C3 | "Are hardcoded secrets allowed?" | No hardcoded secrets invariant |
| C4 | "What is the PR rule?" | One PR per task invariant |
| C5 | "How should logging be done?" | High-performance logging invariant |

### Hygiene Queries (H1-H3 — must NOT return excluded content)

| ID | Query | Must NOT Return |
|----|-------|-----------------|
| H1 | "What changed yesterday?" | docs/work/ content |
| H2 | "What happened today?" | docs/work/ content |
| H3 | "aspire config" | docs/work/plans content |

### Structural Queries (S1-S6 — source-aware, from A §3 + B §3)

| ID | Query | Expected |
|----|-------|----------|
| S1 | "Show me all chunks from ADR-0011" | 3+ chunks, all source_file=docs/adr/0011-... |
| S2 | "What does ADR-0011 decide?" | Decision-section chunk only |
| S3 | "What does ADR-0067 say about erasure and what does ADR-0068 add?" | Chunks from both ADRs in top 5 |
| S4 | "What are the consequences of ADR-0011?" | Consequences-section chunk |
| S5 | "ADR-0070" | Any ADR-0070 chunk (identifier-only diagnostic) |
| S6 | "documentation structure trust model" | Any ADR-0070 chunk (content-only diagnostic, no number) |
