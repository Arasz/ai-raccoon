---
name: dotnet-ai-embeddings
description: Use when integrating or benchmarking .NET embedding models.
version: 1.0.0
author: Hermes Agent
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [dotnet, embeddings, llamasharp, lm-studio, benchmarking, microsoft-extensions-ai, retrieval, rag, sqlite-vec, framework-evaluation]
---

# .NET AI embeddings — integration & benchmarking

Integrating embedding backends into a .NET app, or benchmarking retrieval quality/latency across models. Verified end-to-end on a real project (benchmarks/AiRaccoon.Benchmarks): local GGUF (21 MB all-MiniLM) vs LM Studio (qwen3-0.6b,
EmbeddingGemma-300m).

## Architecture: everything behind IEmbeddingGenerator

Use `Microsoft.Extensions.AI`'s `IEmbeddingGenerator<string, Embedding<float>>`
as the seam — one interface for every backend, so quality metrics are comparable across models:

| Backend                          | Package                                                | Wiring                                                               |
|----------------------------------|--------------------------------------------------------|----------------------------------------------------------------------|
| Local GGUF (llama.cpp)           | `LLamaSharp` + `LLamaSharp.Backend.Cpu`                | `LLamaWeights.LoadFromFile` + `LLamaEmbedder`                        |
| LM Studio / OpenAI-compatible    | `OpenAI` + `Microsoft.Extensions.AI.OpenAI`            | `OpenAIClient` → `GetEmbeddingClient(model).AsIEmbeddingGenerator()` |
| In-process ONNX (bundled MiniLM) | `Microsoft.ML.OnnxRuntime` + `Microsoft.ML.Tokenizers` | `InferenceSession` + `BertTokenizer` + mean-pool/L2 adapter          |

Call through the interface type — the extension overloads resolve `TInput`
ambiguously against `string[]` collection expressions. Use an explicit array variable and cast to `IEmbeddingGenerator<string, Embedding<float>>` before
`GenerateAsync(values, options: null, ct)`.

## CRITICAL: LLamaSharp 0.27 embedder bug

`LLamaEmbedder`'s built-in interface `GenerateAsync` implementation touches a **disposed context handle** (`ObjectDisposedException: SafeLLamaContextHandle`). The working path:

```csharp
var @params = new ModelParams(path) { PoolingType = LLamaPoolingType.Mean }; // NOT Embeddings=true
var weights = LLamaWeights.LoadFromFile(@params);                            // sync load
var embedder = new LLamaEmbedder(weights, @params, NullLogger.Instance);     // 2-3 arg ctor
var embeddings = await embedder.GetEmbeddings(text);                         // works, returns Embeddings (float[][])
```

Then wrap `GetEmbeddings` in a small adapter class implementing
`IEmbeddingGenerator<string, Embedding<float>>` (loop values → `GetEmbeddings`
→ `new Embedding<float>(vector)`), exposing the official abstraction without the broken interface path. Dispose the adapter AND the weights separately (`LLamaEmbedder` does not own `LLamaWeights`). See
`references/llamasharp-lmstudio-apis.md` for the full API surface.

## LM Studio via the OpenAI SDK

- Use the plain **`OpenAI`** package (2.12.x), NOT `Azure.AI.OpenAI` — the Azure client returned **0 embeddings** against LM Studio.
- `new OpenAIClient(new ApiKeyCredential("lm-studio"), new OpenAIClientOptions { Endpoint = new Uri(baseUrl.TrimEnd('/') + "/v1") })`
  — LM Studio does not validate the key; any placeholder works.
- `client.GetEmbeddingClient(model).AsIEmbeddingGenerator()` (factory extension; the `OpenAIEmbeddingGenerator` ctor is internal — always use the factory).
- Probe first (read-only): `GET {base}/v1/models` lists loaded models;
  `POST {base}/v1/embeddings` with `{"model","input"}` returns `data[].embedding`.
- **Any** OpenAI-compatible `baseUrl` works via `OpenAIClientOptions.Endpoint` — no hardcoded endpoint:
  `new EmbeddingClient(modelId, new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = new Uri(baseUrl) })`. (`ApiKeyCredential` lives in `System.ClientModel`; `OpenAIClientOptions.Endpoint`
  defaults to the official OpenAI endpoint when omitted.) The `EmbeddingClient`
  direct ctor is equivalent to `OpenAIClient` → `GetEmbeddingClient(model)`.

## In-process ONNX Runtime local engine (bundled MiniLM)

A real offline default engine with no sidecar/process/download: ONNX Runtime over a bundled int8 all-MiniLM-L6-v2 export + BERT WordPiece tokenizer (verified 2026-08, AiRaccoon FR-NM-3, net10):

- **The package is `Microsoft.ML.OnnxRuntime`** — NOT `Microsoft.OnnxRuntime` (the flat-container 404s and NuGet search returns 0 hits). Pin centrally (`Directory.Packages.props`), Infrastructure layer only.
- The standard sentence-transformers MiniLM export has int64 inputs
  `input_ids`/`attention_mask`/`token_type_ids` and one float output
  `last_hidden_state [batch, seq, 384]`. **Inputs must be Int64** — Int32 throws
  `OnnxRuntimeException: Tensor element data type discovered: Int32 metadata
  expected: Int64`. Padding to a per-batch maxLen (≤ model max, 256 for MiniLM)
  keeps one `session.Run` per batch.
- `InferenceSession.Run` is synchronous — wrap in `Task.Run` to satisfy the async MEAI interface. `AsTensor<float>()` returns the base `Tensor<T>` which has NO
  `Buffer`; cast to `DenseTensor<float>` first (row-major flat buffer = your
  [batch, seq, dim] slice math).
- Tokenizer: `BertTokenizer.Create(vocabPath, new BertOptions { LowerCaseBeforeTokenization = true, ApplyBasicTokenization = true, SplitOnSpecialTokens = true, IndividuallyTokenizeCjk = true, RemoveNonSpacingMarks = true })`.
  `EncodeToIds(text, addSpecialTokens: true, considerPreTokenization: true,
  considerNormalization: true)` — parameter *names* matter: the 4-bool overload is
  `(addSpecialTokens, considerPreTokenization, considerNormalization)`; a
  `considerPreTokenizers`-named call fails to compile. Output is `[CLS]…[SEP]`
  (101/102 for this vocab). Truncate ids to the model max length.
- Pooling is NOT in the model: implement mean-pool over the attention mask + L2-normalize in C# (sentence-transformers semantics) as a pure static function (`EmbeddingMath`) — unit-testable with hand-computed 2×3 matrices, zero vector when
  no active tokens.
- **A factory-cached generator must never be disposed by callers.** Cache the engine (23 MB session) keyed by fingerprint in the factory; a caller's
  `using var generator = …` disposes the shared instance and the NEXT embed dies with an NRE inside `InferenceSession.RunImpl`. The factory owns the lifetime; document it. Cache OpenAI generators too (HttpClient churn otherwise) — note the
  apiKey resolves at creation, so an env-key swap needs a re-configure.
- `IEmbeddingGenerator<TInput, TEmbedding>` extends IDisposable AND the non-generic `IEmbeddingGenerator` (member: `object GetService(Type, object?)`) — implement it explicitly returning null.
- Bundled-asset pattern (fail-not-skip): gitignore the ~23 MB .onnx, commit the small vocab.txt; csproj wildcard `Models/*.onnx` + explicit vocab.txt with
  `CopyToOutputDirectory="PreserveNewest" Pack="true" PackagePath="Models/"`; resolve at runtime by walking up from `AppContext.BaseDirectory` for
  `Models/<file>` with an env override (`AIRACCOON_EMBEDDING_MODEL`) for custom paths; pinned SHA-256 in the download script AND a gate test that FAILS (never skips) when the asset is missing. Works for both plain pack and PackAsTool RID
  payloads (`tools/<tfm>/<rid>/Models/` — the walk-up covers it).
- vec0 store: serialize floats as little-endian float32 blobs (4 bytes/elem); vec0 validates the declared dim (384), so wrong-length blobs fail loudly. Keep vec rows in sync with `embed_state` via triggers (delete-then-insert on
  'embedded', delete on row delete) — vec0 has no triggers of its own.

See `references/onnx-minilm-meai-apis.md` for the probed model metadata, exact tokenizer/MEAI API surface, and the re-embed/pending-queue semantics.

## Cosine similarity: BCL, no package

`System.Numerics.Tensors.TensorPrimitives.CosineSimilarity(a, b)` — part of the .NET BCL, hardware-accelerated, used by the .NET AI stack itself. Never hand-roll a dot-product cosine loop.

## Microsoft.Extensions.AI.Evaluation: skip for ranked retrieval

The Evaluation libraries (10.8.0) are **LLM-as-judge text-quality** evaluators (coherence/relevance/groundedness/fluency over a chat response). They compute no Recall@K/MRR/nDCG and their `EvaluateAsync(ChatMessage[], ChatResponse)`
shape does not fit ranked hit lists. For deterministic retrieval benchmarks, hand-rolled `RetrievalMetricsEvaluator` (RecallAtK, Mrr, NdcgAtK — standard IR definitions) is correct and CI-friendly (table + exit code). The Evaluation
libraries only become relevant for evaluating agent *answers* synthesized from retrieved memories (a RAG/chat path).

## Benchmark corpus design: real-world > synthetic

- **Synthetic corpora hit metric ceilings**: a small topic-clustered synthetic set (48 docs / 16 queries) gave every model R@10 = 1.0, MRR = 1.0 — zero discrimination. A real-world corpus (174 docs / 68 queries from actual project ADRs,
  invariants, skills, agent-memory notes) dropped models to R@5 ~0.33, MRR 0.84–0.86 and exposed a real nDCG gap (0.61 vs 0.70).
- **Honest ground truth** is the hard part. Methods that work:
  (a) doc-derived — restate a doc's heading/decision as a query, that doc is relevant; (b) cross-repo topic clusters — docs from different repos sharing a topic; (c) agent-memory notes as query sources. Verify by reading candidate docs;
  record the judgment as a `// judgment:` comment per query.
- **Exclude daily agent-memory notes from relevance sets** — they *mention*
  every topic but don't *cover* decisions; including them gives 15+ relevant docs per query and a metric ceiling. Notes are query sources, not cluster members.
- Generate the corpus from real repos with a script (read-only over sources, verbatim 2-4 sentence bodies, idempotent — two sequential runs byte-identical). See `references/real-world-corpus-design.md`.

## Pitfalls

- **Central package management**: a project may have BOTH a root
  `Directory.Packages.props` and a per-directory one (e.g. `tests/`); MSBuild uses the *closest* one. Adding a PackageVersion to the root does nothing for the test project — put it in the matching directory's props (NU1010).
- **BenchmarkDotNet project**: add `TreatWarningsAsErrors` — the harness generates per-benchmark projects; keep warnings visible. `--job short` for a quick run; `--filter '*ClassName*'` to select. Artifacts land in
  `./BenchmarkDotNet.Artifacts/` — gitignore it.
- **Parallel repo writers**: if files change under you mid-edit (user/another agent co-committing), verify on-disk content before patching and use
  `write_file` for whole-file rewrites rather than fighting partial reverts.
- **Removing a `PackageVersion` pin breaks every project still referencing it**
  (NU1010 under CPM) — grep ALL projects (src, tests, benchmarks) for the PackageReference before deleting a pin. Removing `Microsoft.Extensions.AI`
  while benchmarks still referenced it broke the whole-solution build.
- **RID-specific pack needs a matching restore first**: `dotnet pack
  -p:RuntimeIdentifiers=osx-arm64` fails with NETSDK1047 unless
  `dotnet restore -p:RuntimeIdentifiers=osx-arm64` ran after the last default restore — subsequent plain `dotnet build`/`test` overwrite
  `project.assets.json` without the RID target, so re-restore with the RID before re-packing.
- **Kestrel minimal-API test doubles reject synchronous body reads** —
  `JsonDocument.Parse(context.Request.Body)` throws
  `InvalidOperationException: Synchronous operations are disallowed` (surfaces as a 500 from the OpenAI client). Read async:
  `using var body = new StreamReader(context.Request.Body); JsonDocument.Parse(await body.ReadToEndAsync())`.
- **An interface signature change ripples into every test stub**: when the port (e.g. `IMemoryStore.ConfigureEmbeddingAsync`) gains a parameter, a mechanical pass over all `FakeStore`/`StubStore` implementations is required; a Python regex
  script over the test tree beats hand-editing 7 files.

## Presenting benchmark results (user preferences, `f:` enforced)

Numbers are not the deliverable — the explanation is. Three rules from the field (2026-08):

1. **Results go into the project docs tree**, not just the benchmark's own README. Create `docs/reference/embedding-benchmark.md` (or the equivalent lookup page for the repo's doc layout) with full tables, methodology, and recommendation;
   link it from the docs index. `benchmarks/README.md` keeps the harness + numbers; the docs page is the reference.
2. **Every table header gets a plain-language legend — never assume the reader knows IR metrics.** Add a "What each column means" list per table:
   dim = vector length; R@5/R@10 (Recall@k) = fraction of relevant docs found in the top-k; MRR = how high the first relevant hit ranks (1.0 = always first); nDCG@10 = rewards relevant docs ranked high in the top-10; Mean = wall-clock per
   search; Allocated = managed memory per search.
3. **The root README summary answers a decision question, not a data dump.**
   This user's framing: "do you want/need a bigger embedding model, or is the smallest one good enough — looking at speed and size?" Write an explicit verdict ("the smallest one is good enough for most uses"), back it with a small
   size/quality/speed table, state the trade-off plainly (4–10x latency, 15–30x disk for a quality gain visible only in top-10 ordering), and link the docs page for full numbers.

## Token counting & chunking (Microsoft.ML.Tokenizers 2.0)

RAG chunk sizing is measured in tokens, not characters. Verified against Microsoft.ML.Tokenizers 2.0.0 on net10 (2026-08, AiRaccoon FR-NM-10):

- **`O200kBase` class is GONE in 2.0.0** (it existed in 1.x). The factory is
  `TiktokenTokenizer.CreateForEncoding("o200k_base")` — it loads the embedded BPE vocab from the `Microsoft.ML.Tokenizers.Data.O200kBase` package; reference BOTH packages. `CountTokens(string)` works with one argument (the
  `considerPreTokenization`/`considerNormalization` bools are optional) and the tokenizer is NOT `IDisposable`.
- **Method-group trap**: `tokenizer.CountTokens` does NOT convert to
  `delegate int TokenCount(string)` — optional parameters don't participate in method-group conversion. Wrap it: `private int CountTokens(string t) => _tokenizer.CountTokens(t);`
- **Vulnerability gate**: the data package targets netstandard2.0 only and drags
  `Microsoft.Bcl.Memory 9.0.4` (GHSA-73j8-2gch-69rq) into any TFM. Under TreatWarningsAsErrors that is a NU1903 restore FAILURE. Pin the patched version centrally AND reference it directly in the project — with
  `CentralPackageTransitivePinningEnabled=false`, a `PackageVersion` entry alone never pins a transitive dependency.
- **Keep the splitter pure** (TextChunker pattern): Core owns a `TokenCount`
  delegate + `IChunker` + static line-granular splitter (fences \`\`\`/`~~~` are atomic units — never split even past maxTokens; overlay = maximal tail-unit suffix within the overlay budget); Infrastructure owns the tokenizer instance.
  Enforce `overlay < maxTokens` and `maxTokens > 0` with guards.
- **Probe a changed library API before writing tests**: a /tmp scratch console project + reflection on the restored package beats guessing — measured o200k counts used to lock test fixtures in `references/ml-tokenizers-2.md`.

## Support files

- `references/llamasharp-lmstudio-apis.md` — probed API surface, versions, working code snippets for both backends.
- `references/real-world-corpus-design.md` — corpus source selection, query ground-truth methodology, generator structure.
- `references/sqlite-vec-and-rag-storage.md` — RAG storage on SQLite: loading sqlite-vec in Microsoft.Data.Sqlite, FTS5 hybrid fusion, provider pluggability via IEmbeddingGenerator, schema-agnostic CRDT sync (sqlite-sync), sqlite-memory
  parity hard items, RAG-framework evaluation verdicts (typical-rag-dotnet, SmartRAG), and the self-describing single-DB memory model (workspaces as FK entities). Consult when building or evaluating .NET RAG memory storage.
- `references/onnx-minilm-meai-apis.md` — ONNX Runtime + MEAI 10.8.3 probed API surface: MiniLM ONNX input/output metadata, BertTokenizer options and EncodeToIds overloads, OpenAI-compatible endpoint override, hooking a local engine into a
  memory store (write-embed, embed_pending, engine-change re-embed), vec0 blob/trigger sync.
