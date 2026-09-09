# Research record: LlamaIndex + Chroma replica of ai-raccoon memory retrieval

Date: 2026-09-08. Task: air-llamaindex-chroma-fusion-retrieval-harness (low effort).
Every finding cites its source path; unverified claims are labelled HYPOTHESIS.

## Retrieval pipeline (what the script must replicate)

1. **Legs** — two, per scope-filtered contexts (`src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.Search.cs`, `LegsFor`):
   - `fts` leg: FTS5 BM25 candidates (`QueryFtsBatchAsync`, `BuildFtsResults`; ranking = BM25, ascending = better).
   - `vector` leg: cosine-similarity candidates over the memory embedding (`ModalityCandidates.ByCosine`, descending).
   - A leg that is never queried (empty FTS plan or empty query vector, or its weight = 0) is SKIPPED, not empty (`SearchQueryExtensions.IsFtsQueried/IsVectorQueried`).
2. **FTS plan** (`FtsQueryNormalizer.BuildPlan`): lowercase alphanumeric tokens; drop reserved {and,or,not,near} and stopwords {what,is,the,how,does,about,are,do,can,should,will,would,could,has,have,been,was,were,being,a,an,in,on,at,to,for,of,by,with,from}; 0 tokens → no FTS; 1 token → term; ≤4 tokens → `AND` with `OR`-fallback (raw tokens + bigrams); >4 tokens → plain `OR` of raw tokens.
3. **Candidate window** (`CandidateWindowFor`): default mode `Max3X100` → `clamp(limit*3, min 100)`; alt `Max5X50`.
4. **Fusion** (`ReciprocalRankFusion.FuseWithEvidence`): score = Σ weight/(k+rank) per leg, k = rrfK; normalized to max 1.0; first list carrying a result supplies payload. Cross-context candidate lists dedupe by content hash first, best-score wins, ordered by absolute score (`ModalityCandidates`).
5. **Post-fusion rank** (`SearchResultMerger.Merge` → `SourceAffinityRanker.Rank`): source-affinity boost λ (sibling-chunk count), doc-score formula (Max default), consolidation threshold, tie-breaks (doc score, path, chunk index), re-normalize to max. λ = 0 is a no-op. Path queries force λ = 0.
6. **Floor + limit** (`SearchResultMerger`): relative floor `ranking >= minRelativeScore` (fraction of response top hit), then `Take(limit)`.

## Resolved defaults (live bank + code)

From live bank settings (`~/.ai-raccoon/memory.db`, read-only query 2026-09-08) and
`src/AiRaccoon.Core/Memory/SearchParameterSettingsKeys.cs`, `SearchDefaults.cs`, `SearchQuery.cs`:

| Knob | Value | Source |
|---|---|---|
| rrfK | 60 | bank `retrieval.rrfK` = code default |
| ftsWeight / vectorWeight | 1 / 1 | bank = code default |
| limit | 8 | `SearchDefaults.Limit` |
| minRelativeScore | 0.6 | `SearchDefaults.MinRelativeScore` |
| sourceLambda | 0.1 | code default (absent from bank) |
| consolidationThreshold | 0.1 | code default (absent from bank) |
| docScoreFormula | Max | code default (absent from bank) |
| candidateWindow | Max3X100 | code default (absent from bank) |
| fusion no-regression | false (off) | bank `fusion.noRegression.enabled.global=false` |
| embedding model | local `Salesforce__SFR-Embedding-Code-400M_R` (ONNX, 1024-dim code model; `embedding.codeDimensions=1024`) | bank `embedding.*` |
| scope for this task | `scope=project`, project `ai-raccoon`, memory kind only (no code leg) | task brief ("Memory only") |
| chunking | markdown line-granular, token-bounded, maxTokens = model-resolved chunk budget, overlay = min(48, max-1) (`ChunkingDefaults.OverlayTokens=48`, `FileIngestor.ChunkSizeForAsync`) | code |

## Corpus

- `scripts/retrieval_tuning/corpora/eval-set-100.json`: 100 queries, each with `query`, `expectedHash`, `expectedSource` (or null), `targetProjectId`, `targetScope`, `searchLimit`, `relevanceGrade`, `negativeTest` flag. E001 example targets `docs:adr:0004-...#context` in project `ai-raccoon`.
- `scripts/retrieval_tuning/build_eval_corpus.py`: generator (deterministic, SEED=42) from a read-only bank copy.
- `scripts/retrieval_tuning/make_memory_copy.py`: sanctioned read-only live-bank copy pattern (`file:...?mode=ro` + `.backup`) — reuse for the harness dataset.

## Replication mapping (brief for the planner)

- Vector leg → Chroma persistent collection, embedding = HF `Salesforce/SFR-Embedding-Code-400M_R` via llama_index `HuggingFaceEmbedding` (HYPOTHESIS: nearest public equivalent of the bank's local ONNX model; expect a parity gap, document it).
- Keyword leg → SQLite FTS5 over the same chunks (mirrors bank) with the `FtsQueryNormalizer` plan ported to Python; rank-bm25 is an acceptable fallback (document).
- Fusion → `QueryFusionRetriever` from llama_index with `num_queries=1` + RRF-equivalent? NOTE: stock `QueryFusionRetriever` generates multiple queries via LLM — for parity, either set `num_queries=1` or bypass LLM query-gen and fuse the two legs manually with score = Σ w/(60+rank). Planner to decide; manual RRF in Python is ~20 lines and exactly matches `ReciprocalRankFusion`.
- Post steps (source-affinity λ=0.1, consolidation 0.1, doc Max, floor 0.6, limit 8) → port `SourceAffinityRanker` semantics to Python (small, pure).
- Comparison: for each corpus query, run ai-raccoon `memory_search` (memory kind, scope=project, project ai-raccoon) and the harness; score hit-sets vs `expectedHash` with F1; aggregate MCC over binary hit/miss per query per system (user wrote "MMC" — read as MCC, Matthews correlation coefficient; confirm in PR description).
- Dataset: ingest the same per-project memory entries (project `ai-raccoon` chunks: hash, value, source_file, chunk_index, path) from a read-only bank copy into Chroma; persist under the harness dir.

## Constraints / stop condition

Memory-only, worktree-only, PR at end. Stop: harness runs end-to-end on corpus (full 100 or documented subset), metrics computed for both systems, PR open with CI green.
