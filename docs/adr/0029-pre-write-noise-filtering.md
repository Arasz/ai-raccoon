# 0029. Pre-Write Noise Filtering Pipeline

Date: 2026-08-13

## Status
Accepted

## Context
Analysis of graded search quality results (averaging ~2.6) revealed that AI agents frequently send raw tool output, such as background process completion logs, as memory search queries. This litters the search index with non-semantic noise, returning irrelevant results and dragging down retrieval quality. 
Relying entirely on post-write degradation sweeps means we still pay the cost of computing embeddings (via external APIs) and polluting the vector store before the sweep can clean it up. Simple regex filtering at the API edge is fragile.

## Decision
We will implement a pre-write noise filtering pipeline (`INoiseFilteringService`) within `SqliteMemoryStore.WriteAsync`.

1. **Policies (`INoiseFilterPolicy`)**: We will use a DI-injected collection of policies to evaluate incoming `MemoryWriteRequest` content. The first policy, `HermesProcessNoisePolicy`, will use structural signature matching rather than regex to catch known tool logs.
2. **Noise Trash Bin (`INoiseStore`)**: When a policy flags an entry as noise, the write is intercepted. Instead of throwing an error (which could break agent workflows), we return a dummy success entry and redirect the raw content to a new `noise_entries` SQLite table.
3. **Trash TTL**: The `noise_entries` table will have a hardcoded 14-day TTL via an `expires_at` column, ensuring it doesn't grow unbounded, while preserving the raw noise data for future evaluation or training of local ML filtering models.

## Consequences
- **Positive:** We stop expensive embedding API calls for pure noise.
- **Positive:** The semantic vector index (`vec0`) and FTS tables remain clean, immediately boosting overall search retrieval quality.
- **Positive:** We collect a high-quality dataset of true negative "noise" for future ML training.
- **Negative:** We introduce synchronous string parsing into the hot path of `WriteAsync`, though structural matching is highly optimized.
