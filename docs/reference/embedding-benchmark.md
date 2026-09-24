# Embedding benchmark

Measured retrieval quality and latency for the embedding models this server can use. The
current bundled default — for both memory and code, since 1.47.0 — is
`granite-embedding-small-english-r2` (fp16, 384-dim, ADR-0108). The sections below put that
model's own measurements first; the older multi-backend comparison further down predates it and
used a different harness (LLamaSharp/GGUF and LM Studio, not the ONNX path AiRaccoon ships), so it
is kept for the harness and methodology, not as a description of what runs today.

## Current default: granite-embedding-small-english-r2 (fp16)

Measured against the two engines it replaced (memory's bundled `all-MiniLM-L6-v2` int8, and the
code corpus's downloaded `faxenoff/code-daemon-embed-v1`) across four evals: a documented-method
code eval built from this repository, a 12-language/72-query code eval (`#673`'s corpus), the
174-document memory corpus, and a 150-document/1,037-chunk "title → document" eval built from a
copy of a live bank.

| engine | size | code (this repo) MRR | code (12 languages) MRR@10 | memory nDCG@10 | bank nDCG@10 |
|---|---:|---:|---:|---:|---:|
| all-MiniLM-L6-v2 int8 (memory default before 1.47.0) | 23 MB | 0.484 | 0.643 | 0.605 | 0.347 |
| code-daemon-embed-v1 (code default before 1.47.0) | 179 MB | 0.324 | 0.391 | 0.573 | – |
| **granite-embedding-small-english-r2 fp16 (current default)** | 97 MB | –¹ | **0.788** | **0.632**² | **0.377** |

¹ Not separately measured for fp16 on this eval; the int8 export (measured here) scored 0.630, and
fp16/fp32 vectors agree with int8's architecture at cosine 1.00000 on CPU.<br>
² The fp32 export scored 0.636 on the same eval; fp16 and fp32 agree at cosine 1.00000 on CPU.

**Evidence:** [ADR-0108](../adr/0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md).

### Code retrieval, through the product's own eval harness

Full-pipeline numbers — identifier-aware keyword search, HTML/CSS/SQL indexing, and the embedding
engine together — on the 12-language, 332-query code corpus (`scripts/retrieval_tuning/`):

| engine / stack | nDCG@5 | hit@1 | hit@5 | drain (1,775 chunks) |
|---|---:|---:|---:|---:|
| code-daemon-embed-v1, pre-identifier-search baseline | 0.501 | 0.446 | 0.581 | 120 s |
| bundled granite fp16, same baseline stack | 0.603 | 0.554 | 0.660 | 44 s |
| granite + HTML/CSS/SQL indexing + identifier-split keyword column (shipped) | **0.824** | **0.768** | **0.892** | 32 s |

**Evidence:** [`docs/work/2026-09-23-code-retrieval-eval-results.md`](../work/2026-09-23-code-retrieval-eval-results.md) (F11, F12).

### Latency (Apple M4, ORT 1.30.0, one row per run)

| provider | precision | 128 tokens | 512 tokens |
|---|---|---:|---:|
| CPU | fp32 | ~12 ms | ~50 ms |
| WebGPU (macOS) | fp16 | ~9 ms | ~29 ms |

The WebGPU execution provider also costs 5–10× less process CPU per embed than the CPU provider,
at the same or better latency: on the fp32 export, 61–64 ms of CPU time on the CPU provider becomes
4.6–6.9 ms on WebGPU at 128 tokens, and 253–261 ms becomes 52–58 ms at 512 tokens.

**Evidence:** [ADR-0108](../adr/0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md).

---

## Historical: pre-granite backend comparison (2026-08-03)

**This section predates ADR-0108 and does not describe the bundled engine above.** It compared a
GGUF build of `all-MiniLM-L6-v2` — run through LLamaSharp, not the ONNX Runtime path AiRaccoon
actually ships as its bundled engine — against two models served remotely from LM Studio. Kept for
the harness and methodology; the model names below (`qwen3-embedding-0.6b`,
`embeddinggemma-300m`) are what was actually measured — neither is `text-embedding-3-small` nor
`bge-m3`, the models named in the how-to's OpenAI/Ollama recipes. Full runnable harness and
per-run instructions: [`benchmarks/README.md`](../../benchmarks/README.md).

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

## Quality results — real-world corpus (2026-08-03)

174 documents from real repositories (job-search-ai-assistant, ai-badger,
arasz-home-page ADRs, invariants, skills, `.remember` notes), 68 queries with
verified relevance judgments.

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

**Recommendation (as it stood for this comparison):** start with the local model. Only move to a
served model if retrieval quality — especially nDCG — proves insufficient on your own corpus: the
served models are 4–10× slower per query and 15–30× heavier, for a quality gain visible only in
the top-10 ranking, not in whether the right memory is found first. **This still holds today**,
but the local model to start with is the bundled granite-embedding-small-english-r2 above, which
needs no download — `ai-raccoon model embedding set local` activates it directly (see
[Configure embedding engines](../how-to/configure-embedding-engines.md)).

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

Corpus provenance: `scripts/generate-benchmark-corpus.py` extracts the
real-world documents verbatim from the three repositories (read-only) and
emits the C# corpus files with per-query `// judgment:` relevance comments.
