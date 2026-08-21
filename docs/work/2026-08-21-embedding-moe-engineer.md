# MoE — Arbitrary embedding model support (engineer lane)

**Date:** 2026-08-21
**Worktree:** task/support-for-other-embedding-models-u1
**Scope:** C# refactor plan for the local/remote embedding engine so that non-BERT models (case study: bge-m3) can be configured, embedded, and searched. Plan only — no code landed.
**Inputs:** `docs/work/2026-08-21-embedding-model-replacement.md` (research lane, findings F1–F11), `docs/adr/0036` (engine-aware chunk budget), `docs/adr/0076` (model-set outbox), `docs/adr/0063` (unset provider ⇒ bundled local).
**Status:** engineer lane draft; decisions here are proposals for the MoE owner, not accepted ADRs.

---

## 1. Verified baseline (what the code does today)

| Concern | Current shape | Evidence |
|---|---|---|
| Local engine | `OnnxEmbeddingGenerator(modelPath, vocabPath, logger)`; one batched `InferenceSession` run; mean-pool + L2-normalize | `src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs:16-115` |
| Window | `MaxSequenceLength = 256` const; `MaxContentTokens = 254` (256 − 2 for [CLS]/[SEP]) | `OnnxEmbeddingGenerator.cs:19-27` |
| Tokenizer | `BertTokenizer.Create(vocabPath, BertOptions{ LowerCaseBeforeTokenization, ApplyBasicTokenization, SplitOnSpecialTokens, IndividuallyTokenizeCjk, RemoveNonSpacingMarks })`; `EncodeToIds(text, true, true, true)`; hard truncate at 256; events 414/415 | `OnnxEmbeddingGenerator.cs:40-47,125-142` |
| Token counting | `LocalTokenizer : ILocalTokenizer` (lazy singleton `BertTokenizer` from bundled `vocab.txt`); DI singleton; consumers: `EmbeddingService`, `FileIngestor`, `ChunkIndexRepair`, `ChunkBackfill`, `ChunkPositionScanner`, `ReingestRepairJob`, `ChunkIndexRepairJob`, `SqliteRepairStore` | `LocalTokenizer.cs:20-36`, `ILocalTokenizer.cs:4-7`, `AppRegistrations.cs:158-271` |
| Chunker coupling | `MarkdownChunker(TokenCount)` verifies joined chunks against the real counter; `FileIngestor.ChunkSizeForAsync` supplies the real `BertTokenizer` for `local`, o200k for other providers; budget = `min(DefaultMaxTokens=256, SafeChunkBudgetFor)`; unset provider ⇒ `local` | `MarkdownChunker.cs:7-15`, `FileIngestor.cs:204-246`, `docs/adr/0036` |
| Pooling | `EmbeddingMath.MeanPoolAndNormalize(hidden, mask, seqLen, dim)`; `EmbeddingMath.Dimension = 384` const (used only by `OnnxEmbeddingGenerator`, verified by grep) | `EmbeddingMath.cs:12-49` |
| Blobs | `EmbeddingBlob.ToBytes/ToFloats` — dimension-agnostic (len = 4·N little-endian float32) | `EmbeddingBlob.cs:6-30` |
| Schema | `vec_entries`/`vec_structure` vec0 `(ctx TEXT, embedding float[384] distance_metric=cosine)`; `DefaultEmbeddingDimension=384`; `ReadVecDimensionAsync` (regex, fallback 384); **`RebuildVecTableAsync(connection, table, dimension, sourceColumn, wherePredicate)` already parameterized by dimension** (used by ladder v2/v9) | `MemorySchema.cs:66,137-141,419,1406-1445` |
| Re-embed | `model set` ⇒ `StartMigrationAsync` writes settings + `model_migration` outbox row + `MarkAllEmbeddedPending` in one tx; `ModelMigrationJob` drains (lease, 32-row batches, renewal) via `EntryEmbedder.DrainMigrationAsync`; `ToolGate` refuses every tool while open; startup pass + 15 s on-demand poll | `docs/adr/0076`, `EntryEmbedder.cs:54-170`, `MemorySchema.cs:364-382` |
| Search | `FROM vec_entries v … WHERE v.ctx = @ctx AND v.embedding MATCH @queryVector AND k = @limit` — a query blob of the wrong dimension errors at MATCH time | `MemorySql.cs:141-162` |
| Remote | `EmbeddingClient(model, key, baseUrl).AsIEmbeddingGenerator()`; `ContextTokensFor("openai") = 8191`; no dimension knowledge anywhere; fingerprint `openai:{model}@{baseUrl}` | `EmbeddingService.cs:26-27,54-63,102-109,128-147` |
| Packages | `Microsoft.ML.Tokenizers 2.0.0` (+ `Microsoft.ML.Tokenizers.Data.O200kBase 2.0.0`), `Microsoft.ML.OnnxRuntime 1.29.0`, `OpenAI 2.12.0` | `Directory.Packages.props:34-41` |
| Eval harness | `scripts/retrieval_tuning/{tune,report,scoring,build_eval_corpus}.py`; corpus `eval-set-100.json`; objective mean nDCG@5; defaults baseline 0.6105 (research F11) | `scripts/src/retrieval_tuning/*.py` |

### Verified Microsoft.ML.Tokenizers 2.0.0 API surface (Microsoft Learn, fetched 2026-08-21)

- **`SentencePieceTokenizer.Create(Stream, bool addBeginOfSentence = true, bool addEndOfSentence = false, IReadOnlyDictionary<string,int>? specialTokens)`** — model stream is a `sentencepiece_model.proto` file, which is exactly what xlm-roberta's `sentencepiece.bpe.model` is. Extra knobs relevant to HF parity: `AddDummyPrefix`, `EscapeWhiteSpaces`, `TreatWhitespaceAsSuffix`, `ByteFallback`, `UnknownToken/Id`, `BeginningOfSentenceToken/Id`, `EndOfSentenceToken/Id`, `SpecialTokens`; `CountTokens`/`EncodeToIds` have 4-boolean overloads. **No new dependency needed for bge-m3.**
- **`TiktokenTokenizer.Create(Stream, PreTokenizer, Normalizer, IReadOnlyDictionary<string,int>, Int32)`**, `Create(String, …)`, and **`CreateForModel(String modelName, Stream vocabFile, IReadOnlyDictionary<string,int> specialTokens, Int32 explicitVocabSize, Normalizer)`** — custom-vocab creation exists.
- `BertTokenizer` — unchanged (already in use).

---

## 2. Tokenizer abstraction (per-family tokenizers)

### 2.1 Design

Introduce one interface in `AiRaccoon.Infrastructure/Embedding` (Infrastructure, because it wraps `Microsoft.ML.Tokenizers` — Core stays dependency-free, same rule ADR-0036 already applied to chunking):

```csharp
public interface IEmbeddingTokenizer
{
    /// <summary>Content tokens without special tokens — the budget unit (ADR-0036 contract, unchanged).</summary>
    int CountTokens(string text);

    /// <summary>Full encode as the embedder will run it (special tokens included, truncation NOT applied here).</summary>
    IReadOnlyList<int> EncodeToIds(string text, bool addSpecialTokens);

    /// <summary>Tokens the engine adds at embed time (2 for BERT [CLS]/[SEP], 2 for xlm-roberta <s>/</s>, …).</summary>
    int SpecialTokenReservation { get; }
}
```

Per-family implementations (all stateless wrappers over `Microsoft.ML.Tokenizers` types):

| Family | Wraps | Construction inputs | Special tokens |
|---|---|---|---|
| `wordpiece` (bert) | `BertTokenizer` | `vocab.txt`; **the exact 5 `BertOptions` currently in `OnnxEmbeddingGenerator.CreateTokenizer` — copied verbatim, no drift** | [CLS]/[SEP] (2) |
| `sentencepiece` (xlm-roberta, **bge-m3**) | `SentencePieceTokenizer` | `sentencepiece.bpe.model` stream; `Create(stream, addBeginOfSentence: true, addEndOfSentence: true, specialTokens: { "<s>":0, "<pad>":1, "</s>":2, "<unk>":3 })` | `<s>`/`</s>` (2) |
| `bpe` (qwen2/qwen3) | `TiktokenTokenizer.CreateForModel(name, vocabStream, …)` (custom vocab — VERIFIED to exist) | `vocab.json` + merges | per descriptor |

**No new NuGet dependency.** SentencePieceTokenizer is in the already-pinned `Microsoft.ML.Tokenizers 2.0.0` (verified above); TiktokenTokenizer is the class `O200kTokenizer` already uses (`TiktokenTokenizer.CreateForEncoding("o200k_base")`, `O200kTokenizer.cs:8`).

### 2.2 What changes in the existing types

- **`OnnxEmbeddingGenerator`** stops building its own tokenizer: the `IEmbeddingTokenizer` becomes a constructor parameter (alongside the model path). `CreateTokenizer(vocabPath)` (static, public, used by tests + `LocalTokenizer`) stays as the *wordpiece factory method* but is no longer called by the generator itself. The generator keeps `MaxContentTokens` as a derived constant of its window (see §4).
- **`LocalTokenizer`/`ILocalTokenizer`**: the interface's contract is "counts tokens the way the *bundled* local engine will" and it is consumed by 9 call sites that mostly mean "the bundled-model tokenizer" (chunk-position maintenance, repair jobs, backfill — all of which run over already-chunked content budgeted for whatever engine was active). **Decision: keep `ILocalTokenizer` as the bundled-model tokenizer (rename its doc comment; no signature change), and route the *engine-relative* consumers through a new resolver.** The engine-relative consumers are exactly two: `EmbeddingService.TrimQueryToWindow` and `FileIngestor.ChunkSizeForAsync`'s counting override. Both already read engine settings (FileIngestor reads `embedding.provider`/`embedding.model` rows at `FileIngestor.cs:234-241`), so both can ask `EmbeddingService` for the configured engine's tokenizer.
- **`EmbeddingService`** becomes the tokenizer resolver hub (it already is the engine resolver hub and caches generators per fingerprint): add `IEmbeddingTokenizer GetTokenizer(EmbeddingSettings settings)` with a per-fingerprint cache in the same `ConcurrentDictionary` pattern as `_engines`. The tokenizer instance is then *shared* with the generator built for the same fingerprint (constructor-injected), so the embedder and the counter are the same object — the ADR-0036 invariant, preserved by construction.
- **`FileIngestor`/`ChunkPositionScanner`**: replace the `ILocalTokenizer` counting override with `IEmbeddingService.GetTokenizer(settings).CountTokens` when provider is `local`. (Both files already compute `provider`/`model`; they would inject `IEmbeddingService` instead of `ILocalTokenizer`.) `ChunkBackfill`/`ChunkIndexRepair`/`ChunkPositionScanner` internals that mean *bundled* keep `ILocalTokenizer` untouched.
- **`MarkdownChunker`** is untouched: it already consumes `TokenCount`, and the guarantee in ADR-0036 (verify joined chunk against the real tokenizer, binary-search split floor) is tokenizer-agnostic. The `TokenCount` delegate needs no change.

### 2.3 Chunker coupling (ADR-0036) implications

- The invariant "the chunker counts with the same tokenizer that will embed" must hold per *engine*, not per *provider*. Today it is `local ⇒ BertTokenizer`. After this change it is `local ⇒ IEmbeddingTokenizer(configured model path)`. For the bundled model the instance and options are byte-identical, so the corpus guarantee (`ChunkingCorpusGuaranteeTests`, 3,884 chunks ≤ 254 content tokens) must stay green **unchanged** — that is the no-behavior-change gate for this step.
- **Known gap generalizes**: ADR-0036's [UNK]-collapse detector (event 415) and its message ("BERT WordPiece tokens") are family-specific. SentencePiece has a different unknown path (`ByteFallback` exists as a knob; xlm-roberta's spm model has its own unk behavior). The message and the characterization test (`NewlineSeparatedHashList_StillCollapsesToUnknown_DocumentsKnownGap`) need a per-family audit; the *detector* (ids.Count ≤ 3 on long input ⇒ warn) stays, wording becomes family-neutral. This is a small, visible change — do not fold it silently into S1; it belongs in S8 with bge-m3.
- `DefaultMaxTokens = 256` in `FileIngestor.cs:24` and `ChunkPositionScanner.cs:29` caps the budget at 256 for *every* engine — see §4.

### 2.4 Assumptions / UNVERIFIED (tokenizer)

- [VERIFIED] SentencePieceTokenizer `Create(Stream, bool, bool, IReadOnlyDictionary<string,int>?)` — Microsoft Learn API page, fetched 2026-08-21.
- [UNVERIFIED] Raw `SentencePieceTokenizer` output for xlm-roberta matches HF's *fast* tokenizer on every fixture (the fast tokenizer layers a normalizer/pre-tokenizer on top of spm; `AddDummyPrefix`/`EscapeWhiteSpaces`/`ByteFallback` exact values must be derived from the model's `tokenizer.json` and proven by parity fixtures — §6.1). If parity cannot be reached, fallback: keep a tiny normalization wrapper in the `sentencepiece` family implementation.
- [UNVERIFIED] `TiktokenTokenizer.CreateForModel(name, vocabStream, …)` accepts Qwen2's `vocab.json`/`merges.txt` shape. The *API exists* (verified); *compatibility with Qwen2's BPE files* is not. Deferred to a spike in S9 (Qwen3 has no ONNX export anyway — research F9).
- [ASSUMPTION] Special-token ids for xlm-roberta are `<s>=0, <pad>=1, </s>=2, <unk>=3` (standard xlm-roberta vocab). Confirmed by parity fixture generation, not by this pass.

---

## 3. Pooling strategies

### 3.1 Where pooling lives

`OnnxEmbeddingGenerator.RunBatch` (`OnnxEmbeddingGenerator.cs:97-112`) is the single pooling site. Refactor it to delegate to a strategy:

```csharp
public enum PoolingStrategy { Mean, Cls }   // Mcls + MatryoshkaTruncate reserved for future families

public static class EmbeddingMath
{
    public static float[] MeanPoolAndNormalize(ReadOnlySpan<float> hidden, ReadOnlySpan<int> mask, int seqLen, int dim); // VERBATIM current implementation
    public static float[] ClsPoolAndNormalize(ReadOnlySpan<float> hidden, int dim);                                    // row 0, L2-normalize
}
```

- `mean` — current sentence-transformers semantics for MiniLM; the existing method moves **verbatim** (no numeric change).
- `cls` — bge-m3's default pooling: take hidden state of token 0 (`<s>`), L2-normalize.
- `mcls` — bge-m3's long-text variant (task context; precise definition per bge-m3 code: mean over non-special tokens) — **UNVERIFIED detail**, implement only if the model card/code is consulted in S8; otherwise CLS only.
- matryoshka (future, Qwen3) — not a pooling op but an output-dim truncation: `vector[..mrlDim]` after pooling, then normalize; MRL dims are trained for truncation. Deferred to S9.

### 3.2 Manifest selection

The engine descriptor (§5) carries `pooling: "mean" | "cls"` (whitelist; unknown ⇒ validation error at `model set`/load time, never a silent default). The bundled model's shipped descriptor says `mean`, so the default path is unchanged. `OnnxEmbeddingGenerator` switches on `descriptor.Pooling` in `RunBatch`.

### 3.3 Assumptions / UNVERIFIED (pooling)

- [ASSUMPTION] Normalizing CLS embeddings before storage is harmless and keeps current behavior uniform: vec0's `distance_metric=cosine` normalizes internally anyway, so ranking is unchanged by pre-normalization. bge-m3's own usage normalizes for cosine; if a reference golden (§6.4) disagrees, follow the golden.
- [UNVERIFIED] MCLS exact formula — see above.
- [ASSUMPTION] bge-m3's ONNX export (whenever the download lane provides it) exposes `last_hidden_state` + `input_ids`/`attention_mask` (+ optionally `token_type_ids`); if the export omits `token_type_ids`, the generator's third input must be dropped for that model — add a descriptor flag `requiresTokenTypeIds` rather than probing. **Check the actual bge-m3 onnx inputs in S8.**

---

## 4. Dimension plumbing

### 4.1 What is hardcoded today

- `EmbeddingMath.Dimension = 384` — consumed **only** by `OnnxEmbeddingGenerator` (grep-verified: `OnnxEmbeddingGenerator.cs:109-110`). The slice math already takes `dim` as a parameter everywhere else.
- vec0 DDL `float[384]` in `MemorySchema.Ddl` (fresh banks) + `DefaultEmbeddingDimension = 384` fallback in `ReadVecDimensionAsync`.
- Nothing validates blob length against the declared vec dimension before `MarkEmbedded` — a wrong-dim insert surfaces as sqlite-vec's "dimensions mismatch" at trigger time.

### 4.2 Target shape

1. **Dimension comes from the model, not a const.** `OnnxEmbeddingGenerator` derives `int Dimension` from the session output (`last_hidden_state` shape, last axis) at construction; `RunBatch` slices with it; `EmbeddingMath.Dimension` is deleted (the schema's `DefaultEmbeddingDimension` stays as a *schema* default). The generator exposes `public int Dimension` so callers can learn it without a tensor.
2. **Blob layout: unchanged.** `EmbeddingBlob` is already dimension-agnostic; vectors are implicitly versioned by (dim, engine fingerprint). Explicit blob versioning is **rejected**: the fingerprint + dim pair already determines what a blob means, an old-dim blob can only exist while `embed_state` is `pending` (old vectors leave vec0 via `vec_entries_pending` on the outbox commit), and a version header would tax every blob read to protect against a state the outbox already prevents (ADR-0076's whole point).
3. **The rebuild machinery already exists** — `RebuildVecTableAsync(connection, table, dimension, sourceColumn, wherePredicate)` and `ReadVecDimensionAsync` (used by ladder v2/v9; `MemorySchema.cs:1406-1445`). The gap is *when* the rebuild runs for an engine swap.

### 4.3 The critical ordering problem (and its fix)

`model set` commits the outbox on a **running server**; the schema ladder runs at **bank open**, so a ladder step alone cannot reconcile dimensions for a live swap. Without reconciliation, the drain embeds 1024-dim blobs into `float[384]` vec0 tables, every `MarkEmbedded` fails at the trigger, the migration never finishes, and (per ADR-0076) the server refuses everything — loud, but a total outage.

**Fix: dimension reconcile becomes the first phase of the migration drain** (S6):

```
DrainMigrationAsync:
  1. acquire lease (unchanged)
  2. resolve NEW engine dimension:
       local   → generator.Dimension (session output shape; descriptor cross-check)
       openai  → probe: one GenerateAsync(["probe"]) and measure vector length
  3. read declared dims: ReadVecDimensionAsync(vec_entries), ReadVecDimensionAsync(vec_structure)
  4. if any declared dim != new dim → RebuildVecTableAsync both tables at the NEW dim
       (safe: the outbox already marked every row pending, so the tables are empty;
        the ToolGate lock guarantees no query/write interleaves; trigger recreation
        follows the v2-step pattern, MemorySchema.cs:1159-1186)
  5. batch-embed loop (unchanged), finish, release lease (unchanged)
```

This covers both swap cases with one mechanism: a live 384→1024 swap **and** a fresh bank (created `float[384]`) whose first configured engine is 1024-dim. No `CurrentVersion` bump is needed — this is a code-path change, not a DDL change (the ladder stays v10).

Defense-in-depth: `MarkEmbedded` gains a blob-length guard against the declared vec dimension (cheap: one cached pragma read per connection) so a future ordering regression fails with a named error instead of sqlite-vec's mismatch. This guard MUST be proven to fire in a negative test (§6.2, prove-the-check-fails).

### 4.4 Fingerprint / re-embed flow

- `EngineFingerprint` today: `local:bundled` / `local:<path>`, `openai:<model>@<baseUrl>` (`EmbeddingService.cs:102-109`). **It must also include the descriptor identity** (dim, pooling, context, tokenizer family + files) — otherwise editing a manifest (or a re-downloaded model with a different dim) would not trigger the re-embed the vectors owe. S7.
- The `model_migration` outbox row (engine column) stores the fingerprint; the drain resolves the descriptor from settings + manifest at drain time (settings are already committed when the drain runs — no mid-flight read).

### 4.5 Assumptions / UNVERIFIED (dimension)

- [VERIFIED] `RebuildVecTableAsync`/`ReadVecDimensionAsync` exist and are dimension-parameterized (read at `MemorySchema.cs:1406-1445`).
- [ASSUMPTION] sqlite-vec (HiraokaHyperTools.sqlite-vec 0.1.9) rejects an insert/query whose blob length ≠ declared `float[N]` (this is the behavior the guard replaces). Confirm once in S6 with the negative test.
- [ASSUMPTION] All ONNX exports relevant to this lane expose a static output dim per file — true for BERT-family and bge-m3; MRL models (Qwen3) violate it by design and are handled in S9 via `descriptor.outputDimension ≤ modelDim`.

---

## 5. Per-model context (window + chunk budget)

### 5.1 What is hardcoded today

- `OnnxEmbeddingGenerator.MaxSequenceLength = 256`, `MaxContentTokens = 254`.
- `EmbeddingService.BundledModelContextTokens = 256`, `OpenAiEmbeddingContextTokens = 8191`; `ContextTokensFor("local") ⇒ 256` for *any* local model.
- `FileIngestor.DefaultMaxTokens = 256` and `ChunkPositionScanner.DefaultMaxTokens = 256` — **a hard 256 cap that would silently defeat bge-m3's 8192 window** if left in place.

### 5.2 Target shape

- The descriptor carries `contextTokens` (engine window). `OnnxEmbeddingGenerator.MaxSequenceLength` becomes the descriptor value (bundled default 256, no-behavior-change); `MaxContentTokens = contextTokens − SpecialTokenReservation` (254 for bundled, 8190 for bge-m3).
- `ContextTokensFor`/`SafeChunkBudgetFor`/`TrimQueryToWindow` resolve per engine: local ⇒ descriptor (fall back to bundled defaults when no manifest exists), openai ⇒ 8191 (or optional `embedding.contextTokens` settings override — see §7).
- The two `DefaultMaxTokens = 256` caps become **per-engine content budget** (descriptor content budget wins for engines with larger windows), with the bundled behavior unchanged (min(256, 254) = 254 today stays 254).

### 5.3 A real decision the owner must make (not mechanical)

bge-m3's 8192 window does **not** imply 8190-token chunks are good: `docs/adr/0081` measured retrieval cost of chunk-size choices, and research F1 flags needle-in-haystack degradation at 4K+ chars. **Engineer-lane requirement:** `maxTokens ≤ engine content budget`, counted with the engine tokenizer. **Owner/retrieval-lane decision:** what the *target* chunk budget for bge-m3 should be (keep 256 as a tunable default, or a new default). The plumbing supports either; the default must be a named constant, not an accident of the cap.

### 5.4 Assumptions

- [VERIFIED] bge-m3 context = 8192 (research F2/F3 source table).
- [ASSUMPTION] xlm-roberta reserves 2 special tokens at embed time (`<s>` + `</s>`), so 8190 content budget — confirmed during S8 parity work.

---

## 6. Remote path (OpenAI-compatible) implications

**What the OpenAI SDK client already handles (no server work):** auth/endpoint, request/response, batching, decoding. `EmbeddingClient.AsIEmbeddingGenerator()` returns opaque `float` vectors (`EmbeddingService.cs:128-147`).

**What the server must now know for a remote engine:**

1. **The dimension** — nowhere in settings today. Remote engines resolve it the same way the drain resolves local ones: **one probe embed at drain start** ("probe" ≈ 1 token; one API call per migration, acceptable). The probe result is also the natural place to validate the endpoint is alive before committing the bank to a drain that would otherwise fail mid-way.
2. **The context window** — keep `OpenAiEmbeddingContextTokens = 8191` as the default; optional `--context-tokens` override stored as a settings row (mirrors how `embedding.model`/`embedding.baseUrl` are written by `model set openai`). The chunk budget for openai is the window directly (ADR-0036: non-local providers keep the o200k counting proxy — no tokenizer work for remote).
3. **Truncation** — the API truncates over-limit input by documented default for text-embedding-3 models; server-side chunk budget already clamps to 8191, so API-side truncation should rarely fire. Whether the SDK exposes a `Truncate` option to make this explicit: **UNVERIFIED** (check OpenAI 2.12.0 `EmbeddingOptions` in S6; if present, pass `true` explicitly rather than relying on defaults).
4. **Fingerprint** — already includes model + baseUrl, so a remote model change re-embeds (no change needed beyond the shared §4.4 descriptor enrichment).

**Accepted limitation (record it):** a *same-model-id* dimension change by the API provider between migrations is not detectable (fingerprint unchanged). The probe at drain time covers the migration moment; afterwards the bank is consistent by construction because queries embed with the same engine that drained.

---

## 7. The engine descriptor (manifest)

A small JSON sidecar is the single source of truth per local model, written by the download machinery lane (research F7) and consumed by the engine:

```jsonc
{
  "family": "sentencepiece",            // wordpiece | sentencepiece | bpe
  "vocab": "sentencepiece.bpe.model",   // relative to the model file; wordpiece => vocab.txt
  "pooling": "cls",                     // mean | cls  (mcls reserved)
  "contextTokens": 8192,
  "dimension": 1024,                    // expected output dim; cross-checked against session
  "specialTokenReservation": 2,
  "requiresTokenTypeIds": false,        // bge-m3 export check in S8
  "queryPrefix": null                   // unused for bge-m3 (no query instructions); reserved for
                                        // e5-instruct-family models — UNVERIFIED scope for a later wave
}
```

- **Bundled model:** ships a default descriptor equivalent to today's constants (mean, 384, 256, wordpiece, 5 BertOptions). No manifest file required for the bundled path in S1–S4 (defaults), required for custom local models in S5+.
- **Validation:** unknown family/pooling, missing vocab file, `dimension` mismatch vs session output ⇒ hard config error at load/`model set` time (never a silent default). A `model set local <path>` without a sidecar works with conservative defaults (bundled-family) — flagged, not refused, because that is today's supported shape.
- **Remote:** no sidecar; descriptor is derived (dim from probe, context default/override).
- Where it lives in code: `EmbeddingModelDescriptor` record + `EmbeddingDescriptorLoader` (Infrastructure), resolved through `EmbeddingService` alongside the generator/tokenizer caches. The fingerprint (§4.4) hashes its identity.

---

## 8. Test strategy

### 8.1 Unit — tokenizer parity fixtures (the keystone)

Golden `{text → token ids}` files per family, generated by the **reference** tokenizer (HF `transformers`), committed under `tests/fixtures/tokenizer-golden/{wordpiece,xlm-roberta}/`. Cases: English prose, CJK paragraph, emoji, URL/path, long punctuation-free newline-joined run (the ADR-0036 [UNK] family), leading/trailing whitespace, text that ends mid-word. Assert `IEmbeddingTokenizer.EncodeToIds(text, addSpecialTokens:true)` == fixture ids and `CountTokens` == ids-minus-specials count.

- **Provenance, mirroring the `build_eval_corpus.py` precedent** (which has a "committed artifact differs from fresh generation" test): the generator script is pinned (transformers version recorded), and a test regenerates and compares — fixtures cannot silently drift from their generator.
- The wordpiece golden set pins *current* behavior first (S0) and becomes the regression net for the tokenizer seam (S1).

### 8.2 Unit — pooling and dimension

- `EmbeddingMathTests`: mean golden unchanged; CLS hand-computed golden; both normalize to unit norm.
- Generator-level: `OnnxEmbeddingPaddingTests` pattern extended — CLS pooling selection via injected strategy with a stub hidden tensor (pooling selection must be testable without a 1024-dim ONNX file).

### 8.3 Integration — swap on a scratch bank copy (never the live bank)

- Seed a scratch bank with the bundled engine; `model set local <1024-dim onnx>` (bge-m3 as the case study); drain; assert: vec tables declared `float[1024]` (sqlite_master), `entries` embedded count == total, `memory_search` returns rows (no dimensions-mismatch), FTS+vector fusion intact.
- **Negative (prove-the-check-fails):** with the dimension-reconcile phase disabled, the drain must fail with the MarkEmbedded guard error and the migration must stay open (server refuses) — the exact failure the fix exists to prevent.
- Fresh-bank variant: create bank, configure 1024 engine directly, first drain rebuilds from 384 → 1024.

### 8.4 Integration — tokenizer/budget under the new family

- ADR-0036 corpus guarantee, re-run with the xlm-roberta tokenizer and the bge-m3 window (8,190 content): `ChunkingCorpusGuaranteeTests`-style over `docs/**/*.md` + hostile fixtures (hex blob, minified JSON, CJK, unbalanced fence), zero violations.
- Event 414/415 semantics re-verified under the new family (415 wording change is deliberate and test-pinned).

### 8.5 Golden embedding vectors

For a fixed sentence, a golden `float[1024]` vector produced once by a reference run (sentence-transformers in Python, pinned environment, provenance recorded next to the file); integration test compares `OnnxEmbeddingGenerator` output within ε (1e-5). This pins tokenizer + pooling + model jointly — the thing parity fixtures alone cannot.

### 8.6 Eval harness (acceptance for S8)

Per research F11: on a scratch bank copy, swap to bge-m3, re-embed (~22.5k rows), run `scripts/retrieval_tuning` eval-set-100, commit the report; compare mean nDCG@5 against the 0.6105 defaults baseline. The report's per-query regression table is the deliverable. This is the *quality* gate; §8.3-8.5 are the *correctness* gates.

### 8.7 Existing tests that must change (and only as much as the invariant requires)

`EmbeddingMathTests` (const removal), `OnnxEmbeddingGeneratorLoggingTests` (event 415 wording), `EmbeddingContextTests`, `EmbeddingServiceLocalGuardTests`, `EmbeddingFeatureTests`, `EmbeddingServiceConfiguredPathTests`, ADR-0076 migration tests (fingerprint enrichment, S7), corpus-guarantee tests (tokenizer seam, S1 — green *unchanged* is itself the gate).

---

## 9. Ordered refactor sequence with gates

Every step ends with the **bundled-model no-behavior-change invariant**: the full suite green AND, where the step touches counting/embedding, the corpus-guarantee and golden tests green *unchanged* (they are the tripwire). TDD-mandatory applies per repo invariant: each step starts with its RED test.

| # | Step | Content | Gate (all must pass) |
|---|---|---|---|
| **S0** | Test scaffolding | Golden-fixture infra + wordpiece parity tests pinning current `BertTokenizer` behavior; fixture regeneration test. | Parity tests green; regeneration test green. |
| **S1** | Tokenizer seam | `IEmbeddingTokenizer` + `wordpiece` impl (options verbatim); generator takes tokenizer via ctor; `EmbeddingService.GetTokenizer` per-fingerprint cache shared with generator; FileIngestor/TrimQueryToWindow route through it; `ILocalTokenizer` kept for bundled-default consumers. | Full suite green; `ChunkingCorpusGuaranteeTests` **unchanged and green**; no production behavior change (diff review). |
| **S2** | Dynamic dimension | Generator derives dim from session output; `EmbeddingMath.Dimension` deleted; generator exposes `Dimension`. | Suite green (bundled still 384); `OnnxEmbeddingPaddingTests`/`EmbeddingFeatureTests` unchanged. |
| **S3** | Pooling strategy | `MeanPoolAndNormalize` moves verbatim behind a strategy; `ClsPoolAndNormalize` added; bundled descriptor default `mean`. | Mean path golden bit-identical; CLS unit tests green. |
| **S4** | Per-engine context | Window/content-budget/ContextTokensFor/SafeChunkBudgetFor/TrimQueryToWindow resolve from descriptor (bundled defaults identical); `DefaultMaxTokens` caps become per-engine content budget. | `EmbeddingContextTests` + corpus guarantee green; bundled budget still 254. |
| **S5** | Descriptor | `EmbeddingModelDescriptor` + loader + validation; bundled default descriptor; `model set local` without sidecar keeps today's shape (defaults). | Parse/validation unit tests; bundled descriptor equals today's constants; config-error paths tested (unknown family/pooling). |
| **S6** | Dimension reconcile in drain | Drain phase 1: resolve new dim (session / remote probe) → compare declared vec dims → `RebuildVecTableAsync` under lease → then batch loop; `MarkEmbedded` blob-length guard. | §8.3 integration green (384→1024 swap, fresh-bank variant) + negative test proves the guard fires; ADR-0076 crash-recovery tests unchanged and green. |
| **S7** | Fingerprint enrichment | `EngineFingerprint` includes descriptor identity; outbox stores it; descriptor-only changes re-embed. | Fingerprint-change triggers migration (updated ADR-0076 tests); identical descriptor ⇒ no migration. |
| **S8** | bge-m3 | `sentencepiece` family impl + xlm-roberta descriptor + parity fixtures + 415 wording + corpus guarantee under xlm-roberta + scratch-bank swap eval. | Parity green; golden vector ε-match; corpus guarantee zero violations; eval report (nDCG@5 vs 0.6105) committed; no dim-mismatch errors in any path. |
| **S9** | Qwen3 (future, blocked) | ONNX export (research F9 blocks), BPE spike (`TiktokenTokenizer.CreateForModel` vs Qwen2 files), MRL truncation, 32K context, MRL-384 option evaluation. | Not started this wave; spike findings first. |

**Cross-cutting:** an ADR must accompany S1+S5 decisions (tokenizer abstraction + descriptor format) — the research lane's "Still open" explicitly calls for it; and the MoE owner rules the §5.3 chunk-budget-for-bge-m3 question before S8.

---

## 10. Assumption & UNVERIFIED register (consolidated)

| # | Item | Status | Where it resolves |
|---|---|---|---|
| A1 | SentencePieceTokenizer.Create(Stream, bos, eos, specialTokens) in ML.Tokenizers 2.0.0 | VERIFIED (Microsoft Learn, 2026-08-21) | — |
| A2 | Raw SentencePieceTokenizer == HF xlm-roberta fast tokenizer on all fixtures | UNVERIFIED | S8 parity fixtures |
| A3 | TiktokenTokenizer custom-vocab creation exists | VERIFIED (Create/CreateForModel overloads) | — |
| A4 | TiktokenTokenizer accepts Qwen2 vocab.json/merges.txt | UNVERIFIED | S9 spike |
| A5 | bge-m3 CLS pooling; MCLS exact formula | CLS verified by task context; MCLS UNVERIFIED | S8 (model card/code) |
| A6 | bge-m3 ONNX export inputs (token_type_ids?) | UNVERIFIED | S8 (`requiresTokenTypeIds` flag) |
| A7 | sqlite-vec errors on wrong-dim blob insert/query | ASSUMED | S6 negative test |
| A8 | OpenAI 2.12.0 SDK exposes explicit truncate option | UNVERIFIED | S6 (check EmbeddingOptions) |
| A9 | xlm-roberta special ids `<s>=0,<pad>=1,</s>=2,<unk>=3` | ASSUMED | S8 fixtures |
| A10 | Chunk budget for bge-m3 (target, not ceiling) | OWNER DECISION | §5.3 (ADR-0081 evidence) |
| A11 | bge-m3 ONNX artifact availability/size | Research/download lane | F4-style measurement |
| A12 | Normalization-before-storage is rank-neutral under vec0 cosine | ASSUMED | §3.3, golden vectors |

## 11. Hand-off notes for sibling lanes

- **Download machinery lane (F7):** write the §7 manifest next to the model + vocab it downloads; SHA-pin the manifest too (it changes embedding semantics — a tampered manifest is a tampered engine).
- **Retrieval lane:** rule §5.3; re-run ADR-0081-style chunk-size measurement for bge-m3 before choosing the default budget.
- **Test lane:** §8.6 eval report is the S8 acceptance artifact.
- **Docs lane:** ADR for tokenizer abstraction + descriptor (S1/S5); amend ADR-0036's known-gap wording on 415 generalization.
