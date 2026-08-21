# ONNX Runtime + MEAI 10.8.3 — probed API surface (2026-08, AiRaccoon FR-NM-3)

Verified by probing the restored packages (reflection + scratch console app)
before writing production code. Versions: `Microsoft.ML.OnnxRuntime` 1.28.0,
`Microsoft.ML.Tokenizers` 2.0.0, `Microsoft.Extensions.AI.Abstractions` 10.8.3,
`Microsoft.Extensions.AI.OpenAI` 10.8.3 (depends on `OpenAI` 2.12.0),
`Microsoft.Bcl.Memory` 9.0.14 (GHSA pin).

## Bundled MiniLM ONNX export (sentence-transformers/all-MiniLM-L6-v2)

File `onnx/model_qint8_arm64.onnx` (~23 MB), opset 14, ir_version 7:

- Inputs (all int64, [batch, seq]): `input_ids`, `attention_mask`, `token_type_ids`
- Output: `last_hidden_state` float32 [batch, seq, 384] — pooling is NOT in the graph; implement mean-pool + L2-normalize in C#.
- Pooled vectors are unit-norm; raw mean-pooled norm ~5.5 for a normal sentence.
- Tokenizer is not in the ONNX file — bundle `vocab.txt` (231 KB) alongside.

## Order of operations that actually worked

1. `BertTokenizer.Create(vocabPath, new BertOptions { LowerCaseBeforeTokenization = true, ApplyBasicTokenization = true, SplitOnSpecialTokens = true, IndividuallyTokenizeCjk = true, RemoveNonSpacingMarks = true })`
2. `ids = tokenizer.EncodeToIds(text, addSpecialTokens: true, considerPreTokenization: true, considerNormalization: true)`
   — returns [CLS]…[SEP]; "The quick brown fox jumps over the lazy dog" → 11 ids, first `101,1996,4248,2829,4419` (the/quick/brown/fox), last `13971,3899,102`
   (lazy/dog/[SEP]). Truncate to 256.
3. Build int64 `DenseTensor<long>(flat, [batch, maxLen])` per input; zeros for token_type_ids.
4. `session.Run([NamedOnnxValue.CreateFromTensor("input_ids", …), …])` — synchronous; wrap in `Task.Run` for the async MEAI interface.
5. `((DenseTensor<float>)results.First(r => r.Name == "last_hidden_state").AsTensor<float>()).Buffer`
   — base `Tensor<T>` has no `Buffer`; only `DenseTensor<T>` does. Flat buffer is row-major [batch, seq, dim].
6. Mean-pool per item over its mask row, L2-normalize; `new Embedding<float>(vector)`.

`BertTokenizer.EncodeToIds` overloads (parameter names matter):
- `(string text, bool considerPreTokenization, bool considerNormalization)`
- `(string text, bool addSpecialTokens, bool considerPreTokenization, bool considerNormalization)`
- `(string text, int maxTokenCount, bool addSpecialTokens, out string normalizedText, out int charsConsumed, bool considerPreTokenization, bool considerNormalization)`
  There is no `considerPreTokenizers` parameter (compile error CS1739 if you try).

## MEAI 10.8.3 interface facts

- `IEmbeddingGenerator<in TInput, out TEmbedding>` : `IDisposable`, and extends the non-generic `IEmbeddingGenerator` which declares `object? GetService(Type serviceType, object? serviceKey)` — implement explicitly (`=> null`) or the
  compiler demands it.
- `Embedding<float>`: ctor `(ReadOnlyMemory<float>)`; props `Vector`,
  `Dimensions`.
- `GeneratedEmbeddings<T>`: `IList<T>`-like — ctor `(IEnumerable<T>)`, `Add`, indexer, `Count`.
- `OpenAIEmbeddingGenerator` is **internal** in 10.8.3 — public path is the extension `client.AsIEmbeddingGenerator()` (optionally with int? dimensions).

## OpenAI-compatible provider (any baseUrl)

```csharp
using System.ClientModel; // ApiKeyCredential
var client = new EmbeddingClient(modelId, new ApiKeyCredential(apiKey),
    new OpenAIClientOptions { Endpoint = new Uri(baseUrl) }); // omit Endpoint → official OpenAI
return client.AsIEmbeddingGenerator();
```

The OpenAI client POSTs `{baseUrl}/embeddings` with
`{"input":[…],"model":…,"encoding_format":"base64"}` and an
`Authorization: Bearer <key>` header. Response shape the client accepts:
`{"object":"list","data":[{"object":"embedding","index":i,"embedding":[…]}],"model":…,"usage":{…}}`. A minimal-API fake must read the body **asynchronously** (Kestrel forbids sync reads: `JsonDocument.Parse(context.Request.Body)` → 500).

## Engine lifecycle / store wiring

- **Cache ownership**: the factory (`EmbeddingService`) caches generators by engine fingerprint and owns disposal. Callers must NOT `using`-dispose a cached generator — the second embed then dies with an NRE inside
  `InferenceSession.RunImpl`. Disposal on the caller side is only safe for one-shot generators or per-test service instances.
- **Fingerprint**: `local:bundled` / `local:<path>` / `openai:<model>@<baseUrl>`
  (default baseUrl `https://api.openai.com/v1`). Persist it in a settings table; a changed fingerprint means "re-embed".
- **Re-embed vs pending queue (scenario semantics)**: engine change re-embeds ONLY previously-embedded rows (select `embed_state='embedded'`, embed again — the vec trigger replaces the old vector). Pending rows stay pending —
  `embed_pending` owns them. Embedding the whole pending set inside configure would make `embed_pending` vacuous and break the "deferred queue processed after configuration" scenario.
- **API key never persisted**: hold it in-process (`_remoteApiKey` field set by configure), fall back to env (`AIRACCOON_OPENAI_API_KEY`) at embed time.

## vec0 sync (sqlite-vec 0.1.9)

- Blob = little-endian float32, 4 bytes/element, exactly `dim * 4` bytes; vec0 validates against the declared dimension (`float[384]`).
- vec0 has no triggers — keep `vec_entries` in sync from `entries` triggers:
    - `AFTER UPDATE OF embed_state WHEN NEW.embed_state='embedded' AND NEW.embedding IS NOT NULL`
      → `DELETE FROM vec_entries WHERE rowid=NEW.id; INSERT INTO vec_entries(rowid, embedding) VALUES (NEW.id, NEW.embedding);`
      (delete-then-insert makes re-embeds idempotent)
    - `AFTER DELETE ON entries` → `DELETE FROM vec_entries WHERE rowid=OLD.id;`
- Sample: writes embed synchronously once `embedding.provider` is set in settings; without it rows stay `pending` and `memory_stats` reports the count.
