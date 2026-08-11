# AiRaccoon Retrieval Pipeline Architecture

Full trace from query to results. Source files referenced by path relative to repo root.

## Core Pipeline

### 1. Query Entry
- `SearchQuery` record: `ProjectId`, `Query`, `Scope`, `Limit`, `MinScore`, `RrfK`, `FtsWeight`, `VectorWeight`
- Defaults: Limit=20, MinScore=0.7, RrfK=60, FtsWeight=1, VectorWeight=1
- Source: `src/AiRaccoon.Core/Memory/SearchQuery.cs`

### 2. Search Orchestration
`SqliteMemoryStore.SearchAsync()` in `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs`
1. Read embedding settings from DB → resolve embedding generator
2. Generate query vector (if engine configured)
3. Normalize query for FTS5 via `FtsQueryNormalizer`
4. For each search context (shared, project, workspace):
   - Run FTS5 batch → `QueryFtsBatchAsync`
   - Run vector batch → `QueryVectorBatchAsync`
   - RRF-fuse within context → `ReciprocalRankFusion.Fuse`
5. Merge per-context batches → `SearchResultMerger.Merge`
6. Apply minScore, limit, bump access counts

### 3. Context Resolution
`SearchContexts.For(query)` in `src/AiRaccoon.Infrastructure/Sqlite/SearchContexts.cs`
- `SearchScope.All` → shared + project (+ workspace if WorkspaceId set)
- `SearchScope.Project` → project only
- `SearchScope.Shared` → shared only
- Each context produces its own FTS5 + vector batch before intra-context RRF

### 4. FTS5 Query Normalization
`FtsQueryNormalizer.Normalize()` in `src/AiRaccoon.Infrastructure/Sqlite/FtsQueryNormalizer.cs`
- Extracts `[\p{L}\p{N}_]+` tokens (Unicode letters, numbers, underscores)
- Drops FTS5 reserved words: AND, OR, NOT, NEAR
- Joins remaining tokens with ` OR ` (always OR, never AND)
- Lowercases everything
- Result: "Is TDD required?" → "is OR tdd OR required"

Pitfall: "What is ADR-0070 about?" → "what OR is OR adr OR 0070 OR about"
The stopwords drown the signal in BM25 scoring.

### 5. FTS5 Search SQL
`MemorySql.SearchByFilter` in `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs`
```sql
SELECT e.hash, bm25(entries_fts) AS Ranking, e.path,
       snippet(entries_fts, 0, '', '', '…', 12) AS Snippet, e.value
FROM entries_fts JOIN entries e ON e.id = entries_fts.rowid
WHERE entries_fts MATCH @query AND {filter}
ORDER BY bm25(entries_fts)
LIMIT @limit
```
- Uses FTS5 external-content index (entries_fts references entries table)
- BM25 scoring, native SQLite FTS5 implementation
- Snippet extracts 12 tokens around match with '…' separator

### 6. Vector Search SQL
`MemorySql.VectorSearchByFilter` in `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs`
```sql
SELECT e.hash, e.path, e.value
FROM vec_entries v JOIN entries e ON e.id = v.rowid
WHERE {filter}
ORDER BY vec_distance_cosine(v.embedding, @queryVector), e.path
LIMIT @limit
```
- Uses sqlite-vec0 extension: `vec_distance_cosine`
- No snippet — fallback generated in C# from entry value
- Only returns rows where embed_state='embedded'

### 7. Reciprocal Rank Fusion
`ReciprocalRankFusion.Fuse()` in `src/AiRaccoon.Infrastructure/Sqlite/ReciprocalRankFusion.cs`
- For each ranked list with weight w: score = Σ(w / (k + rank))
- k=60 by default (from SearchQuery.DefaultRrfK)
- Scores normalized to max=1.0
- First list carrying a result supplies its payload (FTS5 snippet wins)
- Applied intra-context (each context produces one fused list)

### 8. Context Merger
`SearchResultMerger.Merge()` in `src/AiRaccoon.Infrastructure/Sqlite/SearchResultMerger.cs`
- All per-context batches fused again with uniform weight (1.0 each)
- Identical to intra-context RRF but all contexts treated as equal lists
- minScore and limit applied here

### 9. Candidate Window
`SqliteMemoryStore.CandidateWindowFor(limit)` = max(limit×3, 100)
- With limit=5 → window=100
- Each modality retrieves up to 100 candidates before RRF fusion
- With ~762 total chunks (jsaa HEAD 0bb8ff8a, verified 2026-08-04), this is ~13% of the corpus

## Chunking

### MarkdownChunker
`MarkdownChunker.Split()` in `src/AiRaccoon.Core/Chunking/MarkdownChunker.cs`
- Line-granular: each line is a unit
- Code-fence-aware: ``` and ~~~ blocks are atomic (never split mid-fence)
- Token-bounded: chunks fit within maxTokens
- Overlay: previous chunk's last overlayTokens tokens prepend to next chunk (context continuity)
- Defaults: maxTokens=256, overlayTokens=48

### TokenizerChunker
`TokenizerChunker` in `src/AiRaccoon.Infrastructure/Chunking/TokenizerChunker.cs`
- Wraps MarkdownChunker with o200k_base tokenizer for accurate token counting

## Embedding

### EmbeddingService
`EmbeddingService` in `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs`
- Provider "local": bundled all-MiniLM-L6-v2 ONNX model (~23MB, int8)
  - 384-dimensional vectors
  - 256-token context window (BundledModelContextTokens)
  - Loaded from `Models/all-MiniLM-L6-v2/` (must be copied to bin/Models/ after build)
- Provider "openai": any OpenAI-compatible endpoint
  - 8191-token context window (OpenAiEmbeddingContextTokens)
  - Requires AIRACCOON_OPENAI_API_KEY env var or api_key arg

## Schema

`MemorySchema` in `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs`
- `entries` table: id, hash, path, value, scope, project_id, context_label,
  workspace_id, agent_id, created_at, updated_at, access_count, last_accessed_at,
  rating, ttl_days, embed_state, embedding
- `entries_fts`: FTS5 external-content virtual table over entries(value)
- `vec_entries`: vec0 virtual table over float[384] embeddings
- Triggers: FTS5 sync (INSERT/UPDATE/DELETE on entries), vec0 sync (UPDATE embed_state)
- Indexes: scope+project_id, hash, workspace_id, embed_state+project_id

## Key Source Files

| File | Purpose |
|------|---------|
| `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs` | Search orchestrator, write, ingest |
| `src/AiRaccoon.Infrastructure/Sqlite/ReciprocalRankFusion.cs` | RRF fusion algorithm |
| `src/AiRaccoon.Infrastructure/Sqlite/SearchResultMerger.cs` | Cross-context merger |
| `src/AiRaccoon.Infrastructure/Sqlite/SearchContexts.cs` | Context resolution |
| `src/AiRaccoon.Infrastructure/Sqlite/FtsQueryNormalizer.cs` | Query → FTS5 expression |
| `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs` | All SQL queries |
| `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs` | DDL and triggers |
| `src/AiRaccoon.Core/Chunking/MarkdownChunker.cs` | Chunking algorithm |
| `src/AiRaccoon.Infrastructure/Chunking/TokenizerChunker.cs` | Tokenizer wrapper |
| `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs` | Embedding engine |
| `src/AiRaccoon.Core/Memory/SearchQuery.cs` | Query parameters |
| `tests/AiRaccoon.Tests/Unit/Retrieval/RetrievalMetrics.cs` | nDCG, MRR, Recall@k |
| `tests/AiRaccoon.Tests/Integration/RetrievalBaselineTests.cs` | Baseline test runner |
| `scripts/baseline-queries.json` | Query definitions |
| `scripts/run-baseline-queries.py` | Python baseline runner |
