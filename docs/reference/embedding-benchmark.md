# Embedding benchmark

Measured retrieval quality and latency for the embedding models this server can
use, on a fixed corpus, so the numbers are reproducible. Full runnable harness
and per-run instructions: [`benchmarks/README.md`](../../benchmarks/README.md).

## What is being compared

The server stores memories as text and searches them with embeddings (vector
similarity). Which embedding model you configure changes both **how well**
search finds the right memory and **how fast** each query takes. This page
compares three options:

| Backend | What it is | Where it runs |
|---|---|---|
| `local:all-MiniLM-L6-v2.Q5_K_M.gguf` | The smallest verified model (~21 MB on disk) | In-process, via LLamaSharp (llama.cpp) |
| `lmstudio:text-embedding-qwen3-embedding-0.6b` | A mid-size served model (~639 MB) | LM Studio over the network (OpenAI-compatible API) |
| `lmstudio:text-embedding-embeddinggemma-300m` | A small served model (~334 MB) | LM Studio over the network (OpenAI-compatible API) |

All three run the **same retrieval**: each query's embedding is compared to
every corpus document's embedding by cosine similarity, and the top-10 most
similar documents are returned. Only the embedding model differs — that is
what the numbers isolate.

## Quality results — real-world corpus (2026-08-03, historical)

**Historical**: measured on the 174-document/68-query corpus later re-derived from private
sources onto this repository's own public docs (ai-raccoon#455) — the current corpus is 195
documents / 77 queries (see `benchmarks/README.md`); these figures were not re-measured against
it (`measure-when-it-pays` — a full re-run needs a live LM Studio server).

| embedder | dim | R@5 | R@10 | MRR | nDCG@10 |
|---|---:|---:|---:|---:|---:|
| local:all-MiniLM-L6-v2.Q5_K_M.gguf | 384 | 0.325 | 0.378 | 0.836 | 0.607 |
| lmstudio:text-embedding-qwen3-embedding-0.6b | 1024 | 0.326 | 0.378 | 0.854 | 0.606 |
| lmstudio:text-embedding-embeddinggemma-300m | 768 | 0.343 | 0.404 | 0.858 | 0.704 |

**What each column means** (all scores are averages over the 68 queries; 1.0 is
perfect):

- **dim** — the length of each embedding vector. Higher-dimensional vectors
  carry more information per document but cost more to store and compare.
- **R@5 (Recall@5)** — of the documents that *should* be found for a query,
  the fraction that actually appear in the top-5 results. 0.325 means roughly
  one in three relevant documents shows up in the top five.
- **R@10 (Recall@10)** — same, but for the top-10 results. Always ≥ R@5
  because a bigger result window can only catch more of the relevant set.
- **MRR (Mean Reciprocal Rank)** — how high the *first* relevant document
  ranks: 1/(rank) averaged over queries. 1.0 = the best match is always first;
  0.836 means on average the first relevant hit sits around rank 1.2.
- **nDCG@10 (normalized Discounted Cumulative Gain at 10)** — rewards getting
  relevant documents into the top-10 *and* having them ranked high (earlier
  positions count for more). The most complete single quality number here.

## Quality results — synthetic regression corpus

The original 48-document / 16-query synthetic set, kept so old numbers stay
comparable. Note how everything hits R@10 = 1.0 — this corpus is too easy to
tell the models apart.

| embedder | dim | R@5 | R@10 | MRR | nDCG@10 |
|---|---:|---:|---:|---:|---:|
| local:all-MiniLM-L6-v2.Q5_K_M.gguf | 384 | 0.812 | 1.000 | 1.000 | 0.997 |
| lmstudio:text-embedding-qwen3-embedding-0.6b | 1024 | 0.833 | 1.000 | 1.000 | 0.998 |
| lmstudio:text-embedding-embeddinggemma-300m | 768 | 0.823 | 1.000 | 1.000 | 0.998 |

## Latency results (BenchmarkDotNet, ShortRun)

Wall-clock time for one query end-to-end (embed the query, rank 174 documents,
return the top-10), and the memory allocated while doing it.

| Method | Embedder | Mean | Allocated |
|---|---:|---:|---:|
| Search | local:all-MiniLM-L6-v2.Q5_K_M.gguf | 9.2 ms | 25.9 KB |
| Search | lmstudio:…embeddinggemma-300m | 36.8 ms | 143.8 KB |
| Search | lmstudio:…qwen3-embedding-0.6b | 90.4 ms | 183.9 KB |

**What each column means:**

- **Mean** — average time for one search, lower is better. The local model is
  ~4–10× faster because there is no network round-trip; the served models pay
  HTTP latency on every query.
- **Allocated** — managed memory used per search. The local model allocates
  ~6× less, which matters under sustained load (fewer GC pauses).

## So: smallest model, or a bigger one?

For a memory server answering agent queries interactively, the measured
trade-off is:

- **The smallest model is good enough on quality.** all-MiniLM-L6-v2's MRR is
  0.836 vs 0.854–0.858 for the served models — the first relevant hit lands
  essentially as high. Its recall gap (R@5 0.325 vs 0.343) is real but small,
  and nDCG 0.607 vs 0.704 is the one column where a served model clearly wins.
- **The smallest model wins decisively on speed and footprint.** ~9 ms vs
  37–90 ms per query, ~21 MB on disk vs 334–639 MB, no server process, no
  network dependency, works offline.

**Recommendation:** start with the local model (`scripts/download-embedding-model.py
all-minilm`). Only move to a served model if retrieval quality — especially
nDCG — proves insufficient on your own corpus: the served models are 4–10×
slower per query and 15–30× heavier, for a quality gain visible only in the
top-10 ranking, not in whether the right memory is found first.

## How to reproduce

```bash
scripts/download-embedding-model.py all-minilm
AIRACCOON_TEST_GGUF=$HOME/.ai-raccoon/models/all-MiniLM-L6-v2.Q5_K_M.gguf \
LMSTUDIO_BASE_URL=http://localhost:1234 \
LMSTUDIO_MODELS="text-embedding-qwen3-embedding-0.6b,text-embedding-embeddinggemma-300m" \
dotnet run --project benchmarks/AiRaccoon.Benchmarks          # quality
dotnet run --project benchmarks/AiRaccoon.Benchmarks -- --synthetic   # regression
dotnet run -c Release --project benchmarks/AiRaccoon.Benchmarks \
  -- --bench --filter '*EmbeddingLatencyBenchmark*' --job short       # latency
```

Corpus provenance: `scripts/generate-benchmark-corpus.py` extracts documents
verbatim from this repository's own public docs (`docs/`, `.ai-badger/`,
ai-raccoon#455, ADR-0090 precedent) and emits the C# corpus files with
per-query `// judgment:` relevance comments. Reproducible from a fresh
clone; override the source root with `AIRACCOON_BENCHMARK_CORPUS_ROOT`.
