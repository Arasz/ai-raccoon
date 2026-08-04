# Retrieval Improvement Plan A — Source-First Architecture

> **Based on:** baseline-retrieval-report.md (2026-08-04)
> **Approach:** Structural — memory source as first-class citizen
> **Parallel plan:** retrieval-improvement-B (algorithmic focus, separate agent)

---

## 1. What Works Well

### 1.1 Invariants: 100% precision at rank 1

Three invariant queries matched perfectly:
- QC1 "Is TDD required?" → rank 1
- QC2 "What is the screaming architecture rule?" → rank 1
- QC5 "Are hardcoded secrets allowed?" → rank 1

**Why:** Invariant documents are short (3-10 lines), keyword-dense, and single-chunk. The
chunk is the document. BM25 loves keyword density; the vector finds it too because there's
only one embedding per invariant.

**Lesson:** Short, self-contained documents with high keyword density are the ideal retrieval unit.

### 1.2 100% coverage — all 35 queries return results

No query returns empty. The pipeline consistently surfaces *something* relevant. This is
the safety net working: when FTS5 can't match, vector cosine similarity provides a
fallback, and vice versa.

### 1.3 Hybrid fusion is functioning

The RRF(k=60) fusion of BM25 + cosine KNN is correctly combining both modalities. 8 of 10
expected sources appear somewhere in results — the pipeline finds them, it just doesn't
rank them high enough.

### 1.4 Chunking infrastructure is solid

The MarkdownChunker is line-granular, code-fence-aware, and token-bounded — all the right
foundations. The o200k_base tokenizer gives accurate token counts.

---

## 2. What Doesn't Work — Root Cause Analysis

### 2.1 ADR Chunk Sibling Competition (core problem)

**Observation:** ADRs are chunked by heading sections. A query targeting the "Decision"
section of ADR-0067 competes against:
- Other sections of ADR-0067 (Context, Consequences)
- Sections of other ADRs on similar topics (ADR-0068, other erasure ADRs)
- Cross-referencing content from other docs mentioning "erasure"

**Why it matters:** The current `path` column stores the raw file path (e.g.
`docs/adr/0067-registry-driven-erasure...md`). All chunks from the same file share this
path. The system knows they're from the same file, but **there is no structural boost**:
adjacent chunks from the same source are not promoted, and sibling-competition has no
signal to resolve it.

**Concrete mapping to features:**

| Symptom | Missing Feature | Query |
|---------|----------------|-------|
| ADR-0006 at rank 4 | No source grouping / adjacent-chunk scoring | "offer-page fetching security" |
| ADR-0060 at rank 5 | No source grouping | "What happened to the MCP server?" |
| ADR-0067 NOT FOUND | No source affinity in vector space | "How does data erasure work?" |
| ADR-0070 NOT FOUND | No document-level identity | "What is ADR-0070 about?" |

### 2.2 No Source Identity in Search Results

`MemorySearchResult` carries `Hash`, `Seq`, `Ranking`, `Path`, `Snippet`. It has no
`SourceId` or `SourceFile` field. The `Path` doubles as the source file path for ingested
content, but for `memory_write` entries it's `SHA256(content).hex + ".md"` — a content
hash, not a source indicator.

An agent receiving search results cannot tell:
- Which chunks came from the same file
- Whether a chunk is a primary document or a cross-reference
- What the original file name was

### 2.3 No Document-Level Grouping in RRF

When RRF fuses FTS5 + vector lists, it operates on individual chunk hashes. Two chunks
from the same ADR document that both score well are treated as independent evidence — they
compete rather than cooperate. A document-level "this entire ADR is relevant" signal is
absent.

### 2.4 Query-ADR Name Distance

ADR-0070 "What is ADR-0070 about?" was NOT FOUND. The query explicitly names the ADR by
number, but:

- The chunk containing "ADR-0070" in a heading may have been split from the chunk
  containing its decision text
- FTS5 uses `OR` token joining — `"What" OR "is" OR "ADR" OR "0070" OR "about"` —
  drowning the signal in noise tokens
- The vector embedding of the query may not strongly match because "0070" is just a number

### 2.5 Baseline Blind Spots

The current baseline has 10 expected-source queries and 25 "returns results" queries.
There is no:
- **Per-query relevance grading** (binary isExpectedSource only)
- **Retrieval-level metrics** (nDCG@k, recall@k, MRR — implemented in code but not applied to baseline)
- **Ablation analysis** (FTS-only vs vector-only vs hybrid)
- **Difficulty stratification** (how hard is each query?)
- **Cross-chunk relevance** (if a query matches multiple chunks from the same ADR, are
  any of them acceptable?)

---

## 3. New Baseline Cases to Expose More Detail

### 3.1 Source-Aware Relevance Tests

```
Category: Source Tracking
Q: "Show me all chunks from ADR-0011"
Expected: 3+ chunks, all path=docs:adr:0011-frontend-chassis-stack.md
Measures: source grouping recall

Q: "What does the frontend-chassis-stack ADR decide?"
Expected: exactly the decision section chunk (not header/context/consequences)
Measures: section-level precision within a document
```

### 3.2 Cross-Document Competition Tests

```
Category: Document Disambiguation
Q: "What does ADR-0067 say about erasure and what does ADR-0068 add?"
Expected: chunks from both ADRs in top 5
Measures: multi-document recall

Q: "ADR-0046 vs ADR-0022: which one was retired?"
Expected: ADR-0046's chunk ranking higher than ADR-0022's
Measures: temporal/state discrimination
```

### 3.3 Structural Query Tests

```
Category: Heading-Aware Retrieval
Q: "What are the consequences of ADR-0011?"
Expected: the "Consequences" section chunk
Measures: heading-level chunk targeting

Q: "List all ADRs that reference 'Cosmos DB'"
Expected: multiple ADR chunks with Cosmos DB references
Measures: cross-document term recall
```

### 3.4 Query Difficulty Stratification

```
Easy:   "Is TDD required?" — 3 tokens, high keyword density, single chunk
Medium: "Why was shadcn/ui chosen?" — specific technical term, multiple ADR sections
Hard:   "How does data erasure work?" — general concept, multiple ADRs, cross-cutting
Very Hard: "What is ADR-0070 about?" — relies on numeric match
```

### 3.5 Ablation Test Suite

For each expected-source query, run three variants:
1. FTS5-only (vector weight = 0)
2. Vector-only (FTS weight = 0)
3. Hybrid (current)

This reveals which modality carries each query and surfaces fusion failures.

---

## 4. Improvement Axes

### 4.1 Source as First-Class Citizen (Structural)

#### A. Add `source_file` column to entries table

```sql
ALTER TABLE entries ADD COLUMN source_file TEXT;
```

For `IngestFileAsync`/`IngestDirectoryAsync`: set to the original file path relative to
the ingestion root (e.g. `docs/adr/0011-frontend-chassis-stack.md`).

For `memory_write`/`AddContentAsync`: leave NULL (no file origin) or set to a caller-provided
label.

For chunks: all chunks from the same file share the same `source_file`.

**Impact:**
- Enables source-grouped retrieval: "give me all chunks from file X"
- Enables source-aware ranking: chunks near an already-relevant chunk get a boost
- Enables source identity in search results (the agent knows *which file* a chunk
  came from, not just which content hash)

#### B. Add source grouping to `MemorySearchResult`

```csharp
public sealed record MemorySearchResult(
    string Hash, int Seq, double Ranking, string Path,
    string Snippet,
    string? SourceFile,      // NEW: original file path
    int ChunkIndex,           // NEW: position within the source file (0-based)
    int TotalChunks           // NEW: total chunks in the source file
);
```

`ChunkIndex` and `TotalChunks` tell the agent "this is chunk 2 of 4 from ADR-0011" —
enabling navigation within a document.

#### C. Source-affinity scoring in RRF

After RRF fusion, apply a post-processing pass:

1. **Adjacent chunk boost:** If chunk N from source S ranks well, chunk N+1 from the same
   source gets a small boost (λ=0.05-0.1). Adjacent chunks tend to be semantically
   continuous.

2. **Source consolidation:** For each source file, take the best-scoring chunk and
   optionally merge its adjacent siblings into a single result (reducing noise from
   multiple chunks of the same document competing for ranks).

3. **Document-first ranking:** After initial per-chunk scoring, apply a "document score"
   = max(chunk scores) or mean(top-3 chunks). Use this as a secondary sort key so
   documents with any strong match rank as a block before documents with only weak
   matches.

**Parameter space:**
- Adjacent boost λ: 0.0 (off) to 0.5
- Consolidation threshold: minimum score to include adjacent chunks
- Document score formula: max vs mean(top-3)

### 4.2 Algorithmic Improvements

#### D. FTS5 query construction: phrase-aware instead of OR-only

Current: `FtsQueryNormalizer.Normalize("Is TDD required?")` → `"is OR tdd OR required"`

Problem: The `OR` operator destroys phrase semantics. "Is TDD required?" becomes any
document containing any of those tokens, in any order.

Improvement options:
1. **Implicit AND for short queries (≤4 tokens):** `"is AND tdd AND required"` — higher
   precision for short, focused queries.
2. **Bigram phrase extraction:** Extract adjacent token pairs as quoted phrases:
   `"is OR tdd OR required OR \"is tdd\" OR \"tdd required\""`
3. **Drop stopwords before FTS5 construction:** Strip question words and low-signal
   tokens: `"what"`, `"is"`, `"the"`, `"how"`, `"does"`, `"are"`, `"about"` — these
   contribute noise, not signal.

**Expected impact:** ADR-0070 "What is ADR-0070 about?" → `"adr OR 0070"` instead of
`"what OR is OR adr OR 0070 OR about"`. The signal-to-noise ratio improves dramatically.

#### E. Query-specific chunk expansion

Some queries need more context than a single chunk provides. When a chunk matches:

1. **Dynamic expansion:** Include adjacent chunks (index ±1) from the same source file in
   the result, annotated as "context" rather than primary matches.
2. **Chunk merging at query time:** For a matched chunk, retrieve its siblings and
   concatenate them into a single result. This reconstructs the document section.

#### F. Vector dimension / model quality

The bundled all-MiniLM-L6-v2 has a 256-token context window — the current 256-token
chunk size is already optimal for this model. Upgrading to a stronger model (e.g.,
text-embedding-3-small via OpenAI) would:
- Support larger chunks (up to 8191 tokens)
- Better capture the semantic content of ADRs
- Cost: API dependency, latency, monetary cost

This is an orthogonal improvement axis; pair it with source-first for maximum impact.

### 4.3 Parameter Tuning

#### G. RRF parameters

Current: k=60, FTS weight=1, Vector weight=1, minScore=0.7

Tuning options:
- **k=30** (lower) → rank position matters more, top results dominate
- **k=120** (higher) → more egalitarian, deeper results get more weight
- **Vector weight=2, FTS=1** → vector modality dominates (better for conceptual queries
  like "how does data erasure work?")
- **FTS weight=2, Vector=1** → keyword modality dominates (better for exact matches like
  "ADR-0070")

**Grid search:** Evaluate k ∈ {30, 60, 120} × weight ratios ∈ {1:1, 2:1, 1:2} against
all expected-source queries. Sweep is cheap — no code changes needed, just parameter
variation.

#### H. Chunk size

Current: 256 tokens max, 48 token overlap

Consider:
- **Increase to 512** (only with OpenAI model): Merged sections = fewer chunks competing
- **Decrease to 128 with 32 overlap:** Finer granularity = more precise section targeting,
  but more chunks and more competition

The 256-token default is correct for the bundled model's 256-token window.

#### I. Candidate window

Current: `CandidateWindowFor(limit)` = max(limit×3, 100)

With limit=5 → candidate window=100. This is generous but might drown RRF in noise.
Consider: max(limit×5, 50) for more aggressive pre-filtering, or a dynamic window based
on result score distribution.

---

## 5. Prioritized Implementation Roadmap

### Wave 1: Source Identity (structural foundation)

1. Add `source_file TEXT` column to entries table
2. Populate `source_file` during `InsertChunksAsync` from the file path
3. Expose `source_file` in `MemorySearchResult` (data model only, no ranking changes)
4. Update `RetrievalBaselineTests` to verify source_file is populated
5. Add new baseline queries for source-grouped retrieval

**Gate:** `dotnet test --filter RetrievalBaselineTests` passes, source_file non-null for
ingested queries.

### Wave 2: Query Construction (algorithmic quick win)

1. Drop stopwords in `FtsQueryNormalizer`: question words, articles, prepositions
2. Use AND for ≤4-token queries, OR for longer
3. Add bigram phrase extraction (adjacent token pairs as quoted phrases)
4. Run full baseline — measure rank improvement

**Gate:** Existing queries maintain or improve rank position. ADR-0070 "What is
ADR-0070" shows measurable improvement.

### Wave 3: Source-Affinity Scoring (ranking improvement)

1. Implement adjacent-chunk boost in RRF post-processing
2. Implement document-first ranking (secondary sort by source file score)
3. Add `ChunkIndex` and `TotalChunks` to search results
4. Parameter sweep for boost λ and consolidation threshold

**Gate:** At least 8/10 expected sources at rank ≤3 (up from 6/10). No regression on
invariant queries.

### Wave 4: Baseline Enrichment (measurement infrastructure)

1. Add query difficulty stratification
2. Add ablation suite (FTS-only, vector-only, hybrid)
3. Add per-query relevance grading (not just isExpectedSource)
4. Compute nDCG@5, recall@5, MRR for all queries
5. Add new structural and cross-document test cases

**Gate:** Baseline report includes ablation breakdown and retrieval metrics.

### Wave 5: Parameter Optimization (tuning)

1. Grid search over RRF(k, FTS weight, Vector weight)
2. Evaluate against full baseline including new queries
3. Select Pareto-optimal parameters (best nDCG without regression on invariants)

**Gate:** Parameter selection backed by sweep data; documented in ADR.

---

## 6. Success Metrics

| Metric | Current | Target |
|--------|---------|--------|
| Expected-source match rate @3 | 60% (6/10) | ≥80% (≥8/10) |
| Invariant match rate @1 | 100% (3/3) | 100% (no regression) |
| ADR-0067 found in results | No | Yes (any rank) |
| ADR-0070 found in results | No | Yes (any rank) |
| Ablation coverage | None | FTS/vector/hybrid per query |
| Retrieval metrics computed | None | nDCG@5, recall@5, MRR |
| Source identity in results | No | Yes (sourceFile, chunkIndex, totalChunks) |

---

## 7. Out of Scope (for this plan)

- Embedding model change (all-MiniLM → OpenAI text-embedding-3) — separate evaluation needed
- Chunk size changes (requires model change first)
- Cross-project shared-context search improvements
- Workspace-scoped search improvements
- Re-ranking with a cross-encoder (separate infrastructure)
- Query expansion via LLM (separate feature)

---

## 8. Open Questions

1. **source_file NULL policy:** Should `memory_write` entries have a `source_file` at
   all? If not, how do we distinguish "direct memory" from "ingested file" in ranking?

2. **ChunkIndex semantics:** When a file is re-ingested and chunks change, do chunk
   indices shift? Should they be stable? (Currently dedup by path+hash, so re-ingestion
   of unchanged content is a no-op.)

3. **Document-first ranking interaction with shared context:** Shared entries have no
   source_file. Should they participate in document-first ranking or be treated as
   independent?

4. **Stopword list:** Is a fixed stopword list sufficient, or should we use token
   frequency statistics from the indexed corpus?

5. **Adjacent-chunk boost vs deduplication:** If we boost adjacent chunks, does the
   agent see "duplicate" content? Should adjacent chunks be merged before returning?
