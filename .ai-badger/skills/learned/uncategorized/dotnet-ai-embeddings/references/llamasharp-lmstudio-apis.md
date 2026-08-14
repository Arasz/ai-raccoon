# LLamaSharp + LM Studio probed API surface (verified 2026-08, versions pinned)

All shapes below were confirmed by reflection probing the actual DLLs, not guessed.

## Packages (central-version compatible)

| Package                        | Version | Purpose                                  |
|--------------------------------|---------|------------------------------------------|
| LLamaSharp                     | 0.27.0  | llama.cpp .NET bindings                  |
| LLamaSharp.Backend.Cpu         | 0.27.0  | native backend (adds ~100 MB of natives) |
| Microsoft.Extensions.AI        | 10.8.3  | abstractions (IEmbeddingGenerator)       |
| Microsoft.Extensions.AI.OpenAI | 10.8.3  | OpenAIEmbeddingGenerator factory         |
| OpenAI                         | 2.12.0  | the OpenAI client (works with LM Studio) |
| BenchmarkDotNet                | 0.15.8  | latency harness                          |

## LLamaSharp key API (LLamaSharp.dll)

- `LLamaWeights.LoadFromFile(IModelParams)` / `LoadFromFileAsync` — load weights. Use the SYNC form for embedding; the async form worked in probes but sync is canonical.
- `ModelParams` (implements `IContextParams`, `IModelParams`): key properties —
  `PoolingType` (LLamaPoolingType), `Embeddings` (bool), `ContextSize`, `BatchSize`,
  `Threads`, `ModelPath`. **For embeddings use `PoolingType = LLamaPoolingType.Mean`**; setting `Embeddings = true` instead caused the disposed-handle crash.
- `LLamaEmbedder(LLamaWeights weights, IContextParams params, ILogger logger)` —
  `GetEmbeddings(string) → Task<Embeddings>` (collection of `float[]`, one per pooling). **BUG (0.27):** its explicit `IEmbeddingGenerator<,>.GenerateAsync` implementation reads `SafeLLamaContextHandle.PoolingType` on a disposed handle and
  throws
  `ObjectDisposedException` on the very first call. Workaround: adapter over GetEmbeddings.
- `LLamaEmbedder` does NOT own `LLamaWeights` — dispose both.
- Loading prints llama.cpp progress lines to stderr (`llama_*`, `ggml_*`, `graph_reserve`,
  `load_tensors`, `set_embeddings`, `decode:`...) — filter them out of benchmark output.

## OpenAI SDK + Microsoft.Extensions.AI (OpenAI.dll, Microsoft.Extensions.AI.OpenAI.dll)

- `OpenAIClient(ApiKeyCredential key, OpenAIClientOptions)` — LM Studio: any placeholder key ("lm-studio"), `Endpoint = new Uri("http://host:1234/v1")`.
- `client.GetEmbeddingClient(model) → OpenAI.Embeddings.EmbeddingClient`
- `EmbeddingClient.AsIEmbeddingGenerator(int? defaultModelDimensions)` — the factory extension (`OpenAIClientExtensions`). The `OpenAIEmbeddingGenerator` ctor is internal — always use the factory.
- **Azure.AI.OpenAI (`AzureOpenAIClient`) returned 0 embeddings against LM Studio** — use the plain `OpenAI` package instead. Verified: same endpoint, plain client works.
- `IEmbeddingGenerator<string, Embedding<float>>.GenerateAsync(IEnumerable<string>, EmbeddingGenerationOptions?, CancellationToken)`
  → `GeneratedEmbeddings<Embedding<float>>` (list-like: Count, indexer, Add).
  `Embedding<float>` has `Vector` (ReadOnlyMemory<float>), `Dimensions`, `ModelId`.
- **Overload trap**: the single-value extension `GenerateAsync(TInput, ...)` makes
  `generator.GenerateAsync(new[] { "x" }, ...)` ambiguous. Fix: assign to an explicit
  `IEmbeddingGenerator<string, Embedding<float>>` variable, pass a `string[]` variable, and call `GenerateAsync(inputs, options: null, ct)`.

## LM Studio REST probes (read-only)

```bash
curl -s http://host:1234/v1/models          # -> data[].id (loaded model ids)
curl -s -X POST http://host:1234/v1/embeddings \
  -H "Content-Type: application/json" \
  -d '{"model":"<id>","input":"text"}'      # -> data[0].embedding (float[])
```

- Known-good model ids observed: `text-embedding-nomic-embed-text-v1.5` (768 dims),
  `text-embedding-qwen3-embedding-0.6b` (1024), `text-embedding-embeddinggemma-300m` (768).
- `POST /api/v1/models/load` returned `model_load_failed` for an unloaded embedding model — but `/v1/embeddings` auto-loads on first request, so no explicit load needed.

## Reference results (macos-arm64, LM Studio on LAN, 2026-08)

Real-world corpus (174 docs / 68 queries): local all-MiniLM Q5_K_M 384-dim R@5 0.325 / nDCG 0.607; qwen3-0.6b 0.326/0.606; EmbeddingGemma-300m 0.343/0.704. Latency (ShortRun):
local ~9.2 ms/25.9 KB, EmbeddingGemma ~36.8 ms/143.8 KB, qwen3 ~90.4 ms/183.9 KB.
