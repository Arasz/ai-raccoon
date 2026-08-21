# Embedding manifest contract — pinned v1 schema (WP1)

**Date:** 2026-08-21
**Plan:** `docs/work/2026-08-21-arbitrary-embedding-models-plan.md` (D1, D5, D7, D8, D11)
**Owner of the C# record:** lane A (this worktree) — `src/AiRaccoon.Infrastructure/Embedding/Manifest/`
**Pinned by:** golden fixtures in `tests/AiRaccoon.Tests/Resources/ManifestFixtures/` (parse + round-trip tests) — the fixtures ARE the schema; change them only with a schema-version bump.

## 1. Shape

`manifest.json` sits next to a local model's files (plan §5.4). Serialization: camelCase field
names, kebab-case enum values, explicit `null` for nullable fields. Unknown enum values and
malformed JSON fail deserialization with an actionable message naming the field path;
`[JsonRequired]` fields missing from the document fail the same way.

```jsonc
{
  "manifestVersion": 1,              // int — only 1 is pinned
  "model": "BAAI/bge-m3",            // string — the HF repo id
  "source": { "repo": "BAAI/bge-m3", "revision": "main" },
  "provider": "local",               // local | openai (must match the settings row at runtime)
  "dimensions": 1024,                // int > 0 — output dims; drives the vec0 schema (WP4)
  "contextWindowTokens": 8192,       // int > 0 — tokenizer window (D6 budget derives from it)
  "normalization": "l2",             // l2 | none
  "queryInstruction": null,          // string | null — never auto-detected (ops §4.2)
  "requiresTokenTypeIds": false,     // bool — graph has a token_type_ids input
  "mrl": { "supported": false, "minDimensions": null },   // MRL truncation, informational
  "pooling": {
    "mode": "model-output",          // mean | cls | model-output | last-token
    "outputNames": { "embedding": "sentence_embedding", "tokenEmbeddings": "token_embeddings" }
  },
  "tokenizer": {
    "family": "sentencepiece",       // bert-wordpiece | sentencepiece | tokenizer-json (gated, D5)
    "files": [ { "path": "sentencepiece.bpe.model", "sha256": "<64 hex>" } ],
    "options": {
      "addBeginOfSentence": true,
      "addEndOfSentence": true,
      "specialTokens": { "<s>": 0, "<pad>": 1, "</s>": 2, "<unk>": 3 }
      // numeric ids taken from tokenizer_config.json added_tokens_decoder at download time —
      // never a guessed <mask> mapping (xlm-roberta's mask id is 250001, model-specific)
    }
  },
  "onnx": {
    "inputs": [ "input_ids", "attention_mask" ],
    "embeddingOutput": "sentence_embedding",      // string|null — pooled output (model-output mode)
    "tokenEmbeddingsOutput": "token_embeddings",  // string|null — token-level output
    "files": [ { "path": "model.onnx", "sha256": "<64 hex>" },
               { "path": "model.onnx_data", "sha256": "<64 hex>" } ]
  }
}
```

Notes:

- **File pins (D7/D8):** every file that must be re-verified on load is listed with its SHA-256.
  For LFS files the sha256 is the HF LFS oid captured from the tree API **before** download; for
  non-LFS files it is the TOFU pin of the downloaded bytes. `config.json`/`tokenizer_config.json`
  are provenance files on disk, not pinned entries — their content is captured in the derived
  fields (dims, ctx, special tokens).
- **Pooling placeholder (D11):** when `1_Pooling/config.json` + `modules.json` are absent the
  downloader writes `model-output` (or `cls` when the graph has no pooled output) as a
  placeholder that WP5's parity measurement rewrites — never a silent user-facing default.
- **`provider` must match the settings row** at activation time (WP3); the manifest file itself
  only pins the two legal values.

## 2. Validation rules (all actionable — field path + accepted values)

| Rule | Message shape |
|---|---|
| unknown tokenizer family | `tokenizer.family: unknown family '<v>' (expected bert-wordpiece, sentencepiece or tokenizer-json)` |
| `tokenizer-json` | `tokenizer.family: 'tokenizer-json' is not yet supported (D5 capability gate — deferred ...)` |
| unknown pooling mode / normalization / provider | names the field, the value, the accepted values |
| `dimensions <= 0` / `contextWindowTokens <= 0` | `dimensions: must be a positive integer, got <v>` |
| missing/blank `model`, `source.repo`, `source.revision` | names the field |
| sha256 missing or not 64 hex chars | `<path>[i].sha256: must be a 64-character hex SHA-256, got '<v>'` |
| empty file list for provider `local` | `tokenizer.files: provider 'local' requires at least one pinned file` (same for `onnx.files`) |
| `pooling.mode: model-output` without `onnx.embeddingOutput` | `pooling.mode: 'model-output' requires onnx.embeddingOutput to name the graph's pooled output (e.g. 'sentence_embedding')` |
| `mean`/`cls`/`last-token` without `onnx.tokenEmbeddingsOutput` | symmetric rule |
| `mrl.supported` without `mrl.minDimensions` | `mrl.supported: 'true' requires mrl.minDimensions — ... (never assumed)` |
| `manifestVersion != 1` | `manifestVersion: unsupported manifest version <v> (only version 1 is pinned)` |
| empty `specialTokens` (local) / negative token ids | names the field |

Provider `openai` (settings-row engine, no files) is exempt from the file-list rules.

## 3. Null-manifest legacy semantics (golden case (a))

A model directory **without** `manifest.json` keeps the pre-manifest custom-path contract
(plan §9, `LegacyManifestSemantics` in `EmbeddingManifest.cs`):

- tokenizer: `bert-wordpiece` from the **bundled** vocab.txt (a custom `.onnx` path never
  brought its own vocab — ops §3.6)
- `dimensions = 384`, `contextWindowTokens = 256`, `pooling = mean`, `normalization = l2`,
  `requiresTokenTypeIds = true`

A manifest is the only upgrade path to other families/dims; `model set local <dir>` (WP3)
requires one and rejects a manifest-less directory.

## 4. Openai manifests

Remote engines are settings rows (`embedding.model`/`baseUrl`/`apiKey`/`embedding.dimensions`),
never a manifest file (architecture §5.4). The record type exists so a settings-row engine can
be *represented* as a manifest in memory (files empty, provider `openai`), but no file is
written for remote engines.
