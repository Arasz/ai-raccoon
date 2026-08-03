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

## Backends (official packages only)

Every backend is a `Microsoft.Extensions.AI.IEmbeddingGenerator<string, Embedding<float>>`
— the official .NET AI abstraction — and ranks with the same brute-force cosine
(top-k), so quality metrics are comparable across models:

| Backend | Model loading / embedding | Package |
|---|---|---|
| `local:*` | Local GGUF via llama.cpp .NET bindings | `LLamaSharp` + `LLamaSharp.Backend.Cpu` |
| `lmstudio:*` | LM Studio OpenAI-compatible `/v1/embeddings` | `OpenAI` + `Microsoft.Extensions.AI.OpenAI` |

Ranking uses `System.Numerics.Tensors.TensorPrimitives.CosineSimilarity`
(BCL, hardware-accelerated — same primitive the .NET AI stack uses). No
hand-rolled HTTP clients, GGUF parsing, or vector math.

**Notes on the LLamaSharp integration:** LLamaSharp 0.27's
`LLamaEmbedder` interface `GenerateAsync` implementation touches a disposed
context handle, so `LocalGgufEmbedder` wraps the working
`GetEmbeddings` call in a small adapter that still implements the official
`IEmbeddingGenerator` contract.

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
- `LMSTUDIO_MODELS` — comma-separated LM Studio model ids

## Results (2026-08-03, macos-arm64, LM Studio on 192.168.50.102)

### Quality — Recall@5 / Recall@10 / MRR / nDCG@10 (16 queries, top-10)

| embedder | dim | R@5 | R@10 | MRR | nDCG@10 |
|---|---:|---:|---:|---:|---:|
| local:all-MiniLM-L6-v2.Q5_K_M.gguf | 384 | 0.812 | 1.000 | 1.000 | 0.997 |
| lmstudio:text-embedding-qwen3-embedding-0.6b | 1024 | 0.833 | 1.000 | 1.000 | 0.998 |
| lmstudio:text-embedding-embeddinggemma-300m | 768 | 0.823 | 1.000 | 1.000 | 0.998 |

### Latency — single-query search (BenchmarkDotNet ShortRun)

| Method | Embedder | Mean | Allocated |
|---|---:|---:|---:|
| Search | local:all-MiniLM-L6-v2.Q5_K_M.gguf | 9.2 ms | 25.9 KB |
| Search | lmstudio:…embeddinggemma-300m | 36.8 ms | 143.8 KB |
| Search | lmstudio:…qwen3-embedding-0.6b | 90.4 ms | 183.9 KB |

### Reading

- **The small local model is competitive on this corpus**: with pure cosine
  retrieval the 21 MB all-MiniLM reaches R@5 0.81 and MRR 1.0 — close to the
  LM Studio models — at ~9 ms/query in-process vs 37–90 ms over the network.
  (An earlier run through the production sqlite-memory hybrid path scored
  R@5 0.29, so retrieval path matters as much as the model.)
- LM Studio's Qwen3-0.6b and EmbeddingGemma-300m both retrieve every relevant
  doc by rank 10 (R@10 1.0), with EmbeddingGemma the faster served backend.
- Trade-off: local all-MiniLM (~21 MB on disk, offline, fastest, no API) vs
  LM Studio models (334–639 MB, needs the server) — for this corpus the
  quality gap is small, but real corpora with more topics may widen it.
