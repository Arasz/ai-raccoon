# Ops/Ecosystem — MoE section: arbitrary embedding model support (case study: bge-m3)

> **Rev-1 MoE snapshot.** This document is the ops lane's historical MoE record. Where it and the
> combined plan's §3 decisions disagree, the combined plan wins (notably D3 dimension reconcile
> and D6 chunk budget). Its factual measurements (bge-m3 files, GGUF repos, LM Studio API, HF
> oids) remain current.

**Date:** 2026-08-21
**Lane:** ops/ecosystem — operational facts and ecosystem integration for the "download any embedding model" plan
**Question:** What are the verified operational facts (files, sizes, validity), ecosystem paths (LM Studio, GGUF), download tooling options with a pinning policy, metadata sources, dims/context manifest data, and failure modes for supporting arbitrary embedding models, with bge-m3 as the case study?
**Sibling records:** `docs/work/2026-08-21-embedding-model-replacement.md` (research shortlist, dims table, download flow), `docs/work/2026-08-21-embedding-moe-*.md` (other MoE lanes).
**Grade mix:** 10 MEASURED, 9 READ, 2 INFERRED, 4 UNVERIFIED.

---

## 1. bge-m3 operational profile

### 1.1 Official ONNX export — valid but NOT self-contained (external-data layout) [MEASURED]

`BAAI/bge-m3` tree enumerated via the HF tree API (`/api/models/BAAI/bge-m3/tree/main?recursive=true&limit=1000` — default pagination truncates; with `limit=1000` the repo has **33 entries**):

| File | Size | Role |
|---|---|---|
| `onnx/model.onnx` | 724,923 B (708 KiB) | GraphDef only — **no weights inside** |
| `onnx/model.onnx_data` | 2,266,820,608 B (2.11 GiB) | fp32 weights, external data |
| `onnx/sentencepiece.bpe.model` | 5,069,051 B (4.8 MiB) | SentencePiece model (XLM-RoBERTa) |
| `onnx/tokenizer.json` | 17,082,821 B (16.3 MiB) | Fast tokenizer |
| `onnx/special_tokens_map.json` | 964 B | special tokens |
| `onnx/tokenizer_config.json` | 1,173 B | tokenizer config |
| `onnx/config.json` | 698 B | ONNX-side model config |
| `pytorch_model.bin` | 2,271,145,830 B (2.12 GiB) | fp32 torch weights (not needed for ONNX path) |
| `colbert_linear.pt` / `sparse_linear.pt` | 2.1 MB / 3.5 KB | ColBERT + sparse weights (dense-only path ignores) |

Validity **MEASURED** by Range-requesting the first 4 KiB and parsing the protobuf header: `onnx/model.onnx` starts with `ir_version=6, producer_name="pytorch"` — a valid ONNX protobuf. (Control: the current bundled `all-MiniLM-L6-v2` qint8 gives `ir_version=7, producer="onnx.quantize"`.)

**Operational consequence (the external-data trap):** the official export is a *pair* — `model.onnx` (725 KB) plus `model.onnx_data` (2.11 GiB) in the same directory. Downloading only `model.onnx` produces a model that fails to load. The current bundle is a single self-contained file; bge-m3's official export is the first candidate that breaks that assumption. OnnxRuntime resolves external data by relative path from the `.onnx` file, so the pair must land together.

**No quantized variant in the official repo** — the tree contains no `model_quantized.onnx` / qint8 files (verified by full recursive enumeration above).

**Tokenizer files:** the prompt's working assumption "sentencepiece.model" is wrong for this model — the actual name is **`sentencepiece.bpe.model`** (XLM-RoBERTa convention; present in both the repo root and `onnx/`). There is **no `vocab.txt`** anywhere in the repo — bge-m3 cannot use the current WordPiece `BertTokenizer` path at all.

**Evidence:** HF tree API `https://huggingface.co/api/models/BAAI/bge-m3/tree/main?recursive=true&limit=1000` (2026-08-21); Range GET on `https://huggingface.co/BAAI/bge-m3/resolve/main/onnx/model.onnx` and the MiniLM control, protobuf header parse.

### 1.2 Quantized ONNX variants — community only [MEASURED]

No `onnx-community/bge-m3` exists (onnx-community publishes only `bge-reranker-v2-m3-ONNX`, 34,140 downloads). The two most-downloaded community int8 exports (both self-contained single files, both with valid protobuf headers `ir_version=7, producer="onnx.quantize"`):

| Repo | model_quantized.onnx | tokenizer files | downloads |
|---|---|---|---|
| `gpahal/bge-m3-onnx-int8` | 569,958,496 B (543.5 MiB) | `sentencepiece.bpe.model` 5,069,051 B, `tokenizer.json` 17,082,799 B | 6,106 |
| `MahradHosseini/bge-m3-onnx-int8` | 570,117,086 B (543.7 MiB) | same pair | 21 |

So the ONNX download choice for bge-m3 is: **official fp32 pair (2.27 GB, 2 files, trustworthy provenance) vs community int8 (570 MB, 1 file, community provenance)**. The ops plan must not pick for the design — but must record that the int8 path exists and is the only single-file ONNX option, and that its provenance is a trust decision the SHA pinning policy (section 3.3) partially mitigates (tampering, not export quality).

**Evidence:** HF tree API for `gpahal/bge-m3-onnx-int8`, `MahradHosseini/bge-m3-onnx-int8`; HF model search `search=bge-m3 onnx` (2026-08-21).

### 1.3 GGUF repos for LM Studio / Ollama [MEASURED]

| Repo | Files (quant → bytes) | downloads |
|---|---|---|
| `gpustack/bge-m3-GGUF` | FP16 1,157,671,200 (1.08 GiB); Q8_0 634,553,760 (605 MiB); Q6_K 499,415,104; Q5_K_M 467,662,912; Q5_0 459,307,072; Q4_K_M 437,778,496 (417.5 MiB); Q4_0 421,558,336; Q3_K 402,290,752; Q2_K 366,114,880 | 42,757 (51 likes) |
| `ggml-org/bge-m3-Q8_0-GGUF` | `bge-m3-q8_0.gguf` 634,553,760 B | 11,111 |
| `vonjack/bge-m3-gguf` | f16 1,140,660,032; f16_bert_cpp 1,138,659,872; q8_0 617,542,656; q8_0_bert_cpp 615,542,464 | 438 |

No `lmstudio-community` bge-m3 repo exists (HF org search returned none). `gpustack/bge-m3-GGUF` is the de-facto standard for LM Studio users; the Q4_K_M (417.5 MiB) and Q8_0 (605 MiB) quantizations are the practical choices, matching LM Studio's "choose 4-bit or higher" guidance.

**Evidence:** HF tree API on all three repos; HF model search `search=bge-m3` + `author=lmstudio-community&search=bge-m3` (2026-08-21).

---

## 2. LM Studio integration

### 2.1 Loading bge-m3 in LM Studio — UI flow and URL-add [READ]

- **Discover tab** (`⌘2` / `ctrl+2`): search by keyword, by `user/model` string, **or paste a full Hugging Face URL into the search bar** — this is the documented "add by URL" flow. Search "bge-m3", pick `gpustack/bge-m3-GGUF`, choose a quant file (e.g. `bge-m3-Q4_K_M.gguf`), Download.
- **Programmatic URL-add:** `POST /api/v1/models/download` accepts `model` = model-catalog identifier **or an exact Hugging Face link** (e.g. `https://huggingface.co/lmstudio-community/gpt-oss-20b-GGUF`) plus an optional `quantization` string (e.g. `Q4_K_M`, only for HF links). Response is a download job (`status: downloading|paused|completed|failed|already_downloaded`, `total_size_bytes`).
- **Import:** GGUF files placed manually in the models directory are picked up (documented import flow).

**Evidence:** `https://lmstudio.ai/docs/app/basics/download-model` ("You can even insert full Hugging Face URLs into the search bar!"); `https://lmstudio.ai/docs/developer/rest/download` (request body: `model` — "Accepts model catalog identifiers (e.g., openai/gpt-oss-20b) and exact Hugging Face links"; `quantization` — "Only supported for Hugging Face links"); `https://lmstudio.ai/docs/app/advanced/import-model`.

### 2.2 OpenAI-compatible API surface [READ]

- **Base URL:** `http://localhost:1234/v1`; **API key convention:** `"lm-studio"` (docs use this literal everywhere; it is a placeholder — LM Studio does not validate a real key).
- **`GET /v1/models`** — "Returns the models visible to the server. The list may include all downloaded models when Just-In-Time loading is enabled." Identifiers are the full model paths (e.g. `gpustack/bge-m3-GGUF/bge-m3-Q4_K_M.gguf`). **No dims or context metadata in the response** — nothing in the docs suggests `/v1/models` exposes them.
- **`POST /v1/embeddings`** — OpenAI-format request/response (docs defer the schema to the OpenAI API reference): `data[].embedding` is the float vector, plus `usage.prompt_tokens`/`total_tokens`. The `model` parameter is the full identifier (`"model-identifier"` placeholder in docs; third-party usage confirms `{owner}/{repo}/{file.gguf}` shape, e.g. `nomic-ai/nomic-embed-text-v1.5-GGUF/nomic-embed-text-v1.5.Q4_K_M.gguf`).
- **Dims/ctx:** the API does **not** advertise them. Dims = length of the returned embedding vector (observable at runtime); ctx = the model's own limit (bge-m3: 8192 tokens). A client must know both a priori or probe them.

**Evidence:** `https://lmstudio.ai/docs/developer/openai-compat/embeddings` (Python example, base_url, api_key); `https://lmstudio.ai/docs/developer/openai-compat/models` ("List available models via the OpenAI-compatible endpoint" + cURL); identifier format from `https://mljourney.com/lm-studio-complete-setup-and-usage-guide/` (third-party, matches docs' `model-identifier` placeholder).

### 2.3 URL-add flow — verdict [READ + UNVERIFIED]

The URL-add flow **exists and is documented in two forms**: (a) UI — paste a HF URL into the Discover search bar; (b) REST — `POST /api/v1/models/download` with an exact HF link. For bge-m3 that means `https://huggingface.co/gpustack/bge-m3-GGUF` + `quantization: Q4_K_M`.

**UNVERIFIED:** whether the UI search bar accepts a *direct file* URL (…/blob/main/bge-m3-Q4_K_M.gguf) vs only repo URLs — docs say "full Hugging Face URLs" without distinguishing; the REST endpoint's `quantization` parameter implies repo-level links are the supported form. Treat direct-file URLs as unsupported until tested.

---

## 3. Download tooling for the local ONNX path

### 3.1 Current machinery [READ]

`scripts/src/bundle.py` pins 3 artifacts (name + URL + SHA-256: `MODEL_URL`/`MODEL_SHA256`, `VOCAB_URL`/`VOCAB_SHA256`, `GGUF_URL`/`GGUF_SHA256`); `scripts/download-embedding-model.py` fetches with `fetch_verified` (stdlib urllib only: download → `.part` → atomic replace → SHA-256 verify → **delete on mismatch**, `EmptyDownloadError` on 0 bytes) into either the bundled `src/AiRaccoon/Models` (onnx) or `AIRACCOON_DATA_ROOT/models` = `~/.ai-raccoon/models` (gguf). The server accepts a custom path via `ai-raccoon model set local /path/to/model.onnx` (writes the `embedding.model` settings row).

**Evidence:** `scripts/src/bundle.py:3-13`, `scripts/download-embedding-model.py:26-52`, `scripts/src/download.py:34-62`; research record F7.

### 3.2 Option A: huggingface_hub library — verified signature, but no user-supplied SHA pin [READ/MEASURED]

`hf_hub_download(repo_id, filename, subfolder, repo_type, revision, library_name, library_version, cache_dir, local_dir, user_agent, force_download, etag_timeout, token, local_files_only, headers, endpoint, tqdm_class, dry_run)` — the current 1.x signature, read from the official reference. Notable:

- **There is no `sha256` parameter in the 1.x signature** (verified against the docs' parameter list). Integrity is enforced against the server-provided etag/LFS sha — i.e. the channel's claim, not a pin you supply. A registry/TOFU pin stored by us cannot be enforced through this API.
- `local_dir=` exists, so files can land in `~/.ai-raccoon/models` instead of the default `~/.cache/huggingface` layout (but then it manages that dir's layout itself).
- Gains: revision resolution, etag-based resume, gated-repo token auth, subfolder handling.
- Cost: a pip dependency in a scripts tree that is currently **stdlib-only**, plus fighting its cache semantics for our layout.

**Evidence:** `https://huggingface.co/docs/huggingface_hub/en/package_reference/file_download` (signature block, 2026-08-21); `scripts/src/download.py:1-15` (stdlib-only imports).

### 3.3 Option B: plain URL + SHA via `fetch_verified` (current pattern) [READ/INFERRED]

Repo-id resolution done read-only through the HF API we already used in this lane: `GET /api/models/{repo}/tree/{rev}?recursive=true&limit=1000` (file list + sizes) and `GET /api/models/{repo}` (metadata), download via `resolve/main/{path}` URLs that `fetch_verified` already handles (LFS redirects proven working by the Range probes in section 1). Preserves the existing pin contract: the caller supplies the expected SHA.

### 3.4 Recommendation [INFERRED]

**Use Option B (URL + SHA, `fetch_verified` + HF tree API for resolution) for the CLI verb `download model <hf-url|repo-id>`.** Reasons:

1. **Only Option B supports the pinning policy** (section 3.5) — huggingface_hub 1.x has no `sha256` parameter, so registry/TOFU pins cannot be enforced through it.
2. Zero new dependencies in a stdlib-only scripts tree; matches the existing `bundle.py` + `fetch_verified` contract and the repo's "ask if a simpler shape would do" invariant.
3. Files land exactly where we want (`~/.ai-raccoon/models/<slug>/`) with no cache-layout fighting.
4. What we give up — etag resume and gated-repo auth — is acceptable for a one-shot CLI verb over public repos. Revisit Option A only if gated repos or resumable multi-GB downloads become requirements.

### 3.5 Pinning policy — two tiers [INFERRED]

- **Registry pins (trusted, committed):** models we bless ship as `bundle.py`-style entries — `{repo_id, revision, files: [{name, url, sha256}]}` committed in the repo. Applies to the current MiniLM bundle and to any future blessed default. For bge-m3 the registry must pin **both** files of the external-data pair (or the single-file int8 variant).
- **First-download (TOFU) pins:** arbitrary user-supplied repo-ids get their SHA-256 computed on first fetch and recorded in a per-model manifest; every later load re-verifies and refuses on mismatch. The CLI warns explicitly: *first pin trusts the channel once; registry pins are reviewed.* This is the same trust model `fetch_verified` already implements (verify-or-delete), extended with persistence.

### 3.6 File layout and vocab/tokenizer pairing rules [READ]

- Downloads land in **`~/.ai-raccoon/models/<repo-slug>/`** (`AIRACCOON_DATA_ROOT/models` — the existing gguf target dir convention), not in `src/AiRaccoon/Models` (that stays reserved for the bundled model).
- A **`manifest.json`** beside the files records: `repo_id`, `revision`, per-file `{path, sha256, size}`, `dims`, `ctx`, `tokenizer_class`, `pooling`, `normalized`, `instruction_prefix`.
- **Pairing rule:** tokenizer artifacts are chosen by `tokenizer_class`/`model_type` from `config.json` — WordPiece/BERT → `vocab.txt`; XLM-RoBERTa/SentencePiece → `sentencepiece.bpe.model` + `tokenizer.json`; BPE (Qwen2/Qwen3, nomic) → `tokenizer.json` + `vocab.json`/`merges.txt` as applicable. The manifest enforces presence; an install missing required tokenizer files is rejected.
- **Current-server caveat:** `EmbeddingService.cs:125` constructs the custom-path generator with `OnnxEmbeddingGenerator(modelPath, BundledModel.ResolveVocabPath())` — the bundled **vocab.txt is hardwired** even for custom models. Any non-WordPiece model therefore needs the tokenizer-abstraction work (research record F6) before the pairing rule above can take effect; the ops plan records the constraint.

---

## 4. Model metadata sources

### 4.1 Machine-readable, always present [MEASURED]

- **`config.json`** — `hidden_size` (1024 for bge-m3; equals output dims for CLS/mean-pooled encoder models), `model_type` (`xlm-roberta`), `architectures` (`XLMRobertaModel`), `max_position_embeddings` (8194), `vocab_size` (250002), `torch_dtype`. Verified raw from `https://huggingface.co/BAAI/bge-m3/raw/main/config.json`.
- **`tokenizer_config.json`** — `tokenizer_class` (`XLMRobertaTokenizer`), `model_max_length` (**8192** → the effective ctx for bge-m3). Verified raw.
- **`1_Pooling/config.json` + `modules.json`** (sentence-transformers repos only) — pooling mode (`pooling_mode_cls_token: true` for bge-m3), `word_embedding_dimension` (1024), and the normalization module (`2_Normalize`). Verified raw — this is the only place pooling/normalization is machine-readable.

### 4.2 Not machine-readable — must hardcode or ask the user [READ/MEASURED]

- **Instruction prefixes:** model-specific and NOT inferable from config. Verified from bge-m3's card: *"the BGE-M3 model no longer requires adding instructions to the queries"* — bge-m3 needs **none**; bge v1.5 needs the Chinese instruction; e5 needs `query:`/`passage:`. The manifest's `instruction_prefix` field is user-supplied or blessed-per-model, never auto-detected.
- **Pooling & normalization** when `1_Pooling`/`modules.json` are absent (raw checkpoints): ask the user (CLS vs mean; normalized yes/no). bge-m3 = CLS + normalize (measured).
- **MRL output dim** (Qwen3-0.6B): a choice (32–1024), not a fact — must be user-specified.
- **README model card:** human-readable only; use as fallback documentation, never as schema.

---

## 5. Dims and context — manifest table [MEASURED/READ]

All `hidden_size` / `max_position_embeddings` values below were measured from each repo's raw `config.json` (2026-08-21); ONNX sizes from research record F4 (measured 2026-08-21); ctx notes from model cards.

| Model | Dims | Ctx (config) | Tokenizer class | ONNX path & size | Notes |
|---|---|---|---|---|---|
| all-MiniLM-L6-v2 (current) | 384 | 256 (card `max_seq_length`) | WordPiece (`vocab.txt`) | `onnx/model_qint8_arm64.onnx` 23.0 MB | bundled; server hardwires 384 |
| BAAI/bge-small-en-v1.5 | 384 | 512 | WordPiece | `onnx/model.onnx` 133.1 MB | drop-in class |
| thenlper/gte-small | 384 | 512 | WordPiece | `onnx/model.onnx` 133.1 MB | drop-in class |
| intfloat/e5-small-v2 | 384 | 512 | WordPiece | `onnx/model_qint8_avx512_vnni.onnx` 34.1 MB | **arm64 note:** avx512 variant won't run on macOS arm64; fp32 file needed |
| nomic-ai/nomic-embed-text-v1.5 | 768 | 2048 config / 8192 claimed | BPE (nomic_bert) | `onnx/model.onnx` 547.3 MB | ctx discrepancy UNVERIFIED at runtime |
| mixedbread-ai/mxbai-embed-large-v1 | 1024 | 512 | WordPiece | `onnx/model_quantized.onnx` 337.0 MB | best quality/size of WordPiece family |
| Qwen/Qwen3-Embedding-0.6B | 1024 (MRL 32–1024) | 32768 | BPE (qwen3) | **no official ONNX**; safetensors 1191.6 MB | MRL-384 could dodge vec0 rebuild (F10, unmeasured) |
| **BAAI/bge-m3** | **1024** | **8192** (`tokenizer.model_max_length`; card: "up to 8192 tokens") | SentencePiece (XLM-R) | official fp32 pair 2.27 GB; int8 570 MB; GGUF Q4_K_M 417.5 MiB | case study; CLS+normalize; no instruction prefix |

Non-384 models (bge-m3, mxbai, nomic, Qwen3) require a vec0 `float[N]` rebuild + full re-embed (research record F5/F8); 384-dim models swap in place.

**Evidence:** raw `config.json`/`tokenizer_config.json` for all 8 repos (measured); research record F4 table (sizes); bge-m3 README (`https://huggingface.co/BAAI/bge-m3/raw/main/README.md`); all-MiniLM-L6-v2 card (`https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2`).

---

## 6. Failure modes and rejection rules

| # | Failure mode | Mechanism | What CLI/server must do |
|---|---|---|---|
| 1 | **Wrong dims (silent corruption)** | `RunBatch` slices `last_hidden_state` by the constant `EmbeddingMath.Dimension = 384` (`OnnxEmbeddingGenerator.cs:108-110`) with **no shape assert** — a 1024-dim model would produce garbage vectors that look valid | Reject at install: dims from `config.json`/manifest must equal the schema dims; non-384 only via the explicit vec0-rebuild path. Add a runtime shape assert on the ONNX output as defense-in-depth |
| 2 | **External-data trap** | Official bge-m3 export is `model.onnx` (725 KB) + `model.onnx_data` (2.11 GiB); loading the stub alone fails | Downloader must fetch the pair together or prefer the single-file int8 variant; manifest lists both files; reject if a referenced data file is missing |
| 3 | **Missing tokenizer files** | bge-m3 has **no `vocab.txt`**; the server hardwires the bundled vocab (`EmbeddingService.cs:125`) | Install rejected when required files per `tokenizer_class` are absent; the pairing rule (3.6) runs before the settings row is written |
| 4 | **Unverifiable downloads** | Arbitrary repos have no registry pin; LFS-pointer fetches (wrong URL) yield a ~130-byte text file; mid-flight corruption | TOFU pin on first download, verify on every load; pre-check HEAD size against the tree-API size; `fetch_verified` already deletes on SHA mismatch / 0 bytes |
| 5 | **Pooling/normalization mismatch** | Wrong pooling (mean vs bge-m3's CLS) or missing normalization degrades quality silently — valid-shaped garbage | Read `1_Pooling/config.json` + `modules.json` when present; otherwise require explicit user flags before enabling (no silent default) |
| 6 | **Missing instruction prefix** | Degraded retrieval for instruct models (bge v1.5, e5); bge-m3 needs none | Config field, not a hard failure; documented per model |
| 7 | **LM Studio remote provider** | Model not loaded → HTTP error from `/v1/embeddings`; dims not exposed by the API | Fail fast at connect (`GET /v1/models`); validate first embedding's vector length against the manifest dims; surface the loaded-model identifier |
| 8 | **GGUF vs ONNX runtime split** | GGUF runs under llama.cpp (LM Studio/Ollama), ONNX under OnnxRuntime — separate backends, same dims constraint | Dims/ctx checks are backend-agnostic; the vec0 schema is the single contract |

**Evidence:** `src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs:97-112`, `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:125`, `src/AiRaccoon.Core/Embedding/EmbeddingMath.cs:12` (read); sections 1–3 measurements.

---

## 7. Still open / UNVERIFIED

- **LM Studio input-length behavior:** whether the server truncates or errors on inputs > 8192 tokens for bge-m3 — undocumented.
- **Direct-file GGUF URLs in the LM Studio UI:** docs verify repo-level URLs; direct `…/blob/main/file.gguf` URLs unverified.
- **nomic 8K vs config 2048:** which the runtime honors — unverified.
- **Community int8 export quality (gpahal):** valid ONNX by header, but quality vs official fp32 is unmeasured; recommend the eval harness (research record F11) before blessing it as the default bge-m3 artifact.
- **ONNX Runtime opset compatibility for bge-m3's export (ir_version 6):** not exercised on this machine in this lane — a load smoke-test on a bank copy is prerequisite before any download verb ships.
