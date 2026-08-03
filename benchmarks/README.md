# Retrieval benchmark — embedding model comparison

Compares retrieval **quality** and **latency** across embedding backends on a
fixed synthetic corpus, so the numbers are reproducible and comparable.

## What it measures

- **Corpus**: 48 synthetic documents across 8 topics (C# DI, sqlite-vec,
  middleware, async, Docker, git rebase, unit testing, REST) with distinctive
  per-topic vocabulary, so ranking quality actually depends on the model.
- **Queries**: 16 (2 per topic), each judged relevant to its topic's documents.
- **Quality metrics** (top-10 retrieval): Recall@5, Recall@10, MRR, nDCG@10.
- **Latency**: BenchmarkDotNet (`--bench`) — single-query search wall time +
  allocation per backend.

## Backends

| Backend | How it retrieves |
|---|---|
| `local:*` | Real production path: `SqliteMemoryStore` + sqlite-memory's llama.cpp engine over a local GGUF model |
| `lmstudio:*` | LM Studio OpenAI-compatible `/v1/embeddings` + brute-force cosine top-k |

The sqlite-memory extension's remote engine hardcodes the vectors.space URL,
so LM Studio models cannot run through `memory_search` — the LM Studio backend
embeds via REST and ranks with cosine, the same ranking shape the local path
produces, keeping quality metrics comparable.

## Run it

```bash
# 1. local GGUF model (once):
scripts/download-embedding-model.sh all-minilm

# 2. quality comparison (default):
AIRACCOON_TEST_GGUF=$HOME/.ai-raccoon/models/all-MiniLM-L6-v2.Q5_K_M.gguf \
LMSTUDIO_BASE_URL=http://localhost:1234 \
LMSTUDIO_MODELS="text-embedding-qwen3-embedding-0.6b,text-embedding-embeddinggemma-300m" \
dotnet run --project benchmarks/AiRaccoon.Benchmarks

# 3. latency benchmark (BenchmarkDotNet):
#    (same env vars) + --bench --filter '*EmbeddingLatencyBenchmark*' --job short
```

Environment:
- `AIRACCOON_TEST_GGUF` — local GGUF path (same variable as the embedding tests)
- `LMSTUDIO_BASE_URL` — LM Studio server (default `http://localhost:1234`)
- `LMSTUDIO_MODELS` — comma-separated LM Studio model ids (default: the two
  models verified on the dev box)

## Results (2026-08-03, macos-arm64, LM Studio on 192.168.50.102)

### Quality — Recall@5 / Recall@10 / MRR / nDCG@10 (16 queries)

| embedder | dim | R@5 | R@10 | MRR | nDCG@10 |
|---|---:|---:|---:|---:|---:|
| local:all-MiniLM-L6-v2.Q5_K_M.gguf | 384 | 0.292 | 0.292 | 0.750 | 0.385 |
| lmstudio:text-embedding-qwen3-embedding-0.6b | 1024 | 0.833 | 1.000 | 1.000 | 0.998 |
| lmstudio:text-embedding-embeddinggemma-300m | 768 | 0.823 | 1.000 | 1.000 | 0.998 |

### Latency — single-query search (BenchmarkDotNet, ShortRun)

| Method | Embedder | Mean | Allocated |
|---|---:|---:|---:|
| Search | lmstudio:…embeddinggemma-300m | 13.8 ms | 41.8 KB |
| Search | local:all-MiniLM-L6-v2.Q5_K_M.gguf | 21.6 ms | 10.7 KB |
| Search | lmstudio:…qwen3-embedding-0.6b | 40.9 ms | 50.5 KB |

### Reading

- The small local model (21 MB, Q5_K_M) is fast but weak at retrieval on this
  corpus (R@5 0.29 vs 0.83): it finds *a* relevant doc (MRR 0.75) but misses
  the rest of the topic cluster. The two LM Studio models both retrieve every
  relevant doc by rank 10.
- Latency includes query embedding + ranking; the local path also pays
  sqlite-memory's in-process inference. LM Studio's EmbeddingGemma-300m is the
  fastest served backend here.
- Sizes on disk: all-MiniLM ~21 MB, EmbeddingGemma-300m ~334 MB,
  Qwen3-0.6b ~639 MB (LM Studio downloads). Trade-off: model size vs
  retrieval quality, with 100 MB-class models (e.g. nomic-embed-text-v1.5,
  mxbai-embed-large) as the middle ground — see
  `scripts/download-embedding-model.sh nomic`.
