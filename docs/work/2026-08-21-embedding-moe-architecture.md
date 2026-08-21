# Architecture — arbitrary embedding models in AiRaccoon (MoE plan, architect lane)

**Date:** 2026-08-21
**Task:** `support-for-other-embedding-models` (worktree branch `task/support-for-other-embedding-models-u1`)
**Lane:** architect — abstraction/manifest, provider taxonomy, dimension strategy, download surface, backward compatibility, phased work packages
**Status:** plan (ready for owner/MoE review; convention per `docs/work/README.md`)
**Sibling lanes:** engineer = C# refactor details; ops = ecosystem facts (LM Studio/GGUF/quantization). Cross-lane decisions are marked **D#** and collected in §12.

This document is executable by an implementation lane that knows nothing beyond this file plus the
cited sources. Every READ fact carries a `file:line` or URL citation, verified 2026-08-21. Anything
not directly verified is labeled **UNVERIFIED** (do not implement against an UNVERIFIED fact without
measuring it first).

---

## 0. Goal and non-negotiables

### 0.1 Goal

Make AiRaccoon's embedding engine able to run **arbitrary user-provided embedding models**, with
`BAAI/bge-m3` as the first case study, while:

1. existing banks keep working with zero migration,
2. the bundled all-MiniLM-L6-v2 default stays the default until a quality gate passes on our own corpus,
3. every model swap happens through the existing model-migration machinery (ADR-0076) so the bank is never half-embedded,
4. downloads are SHA-256-pinned and never trusted unverified.

### 0.2 Non-negotiable constraints (inherited from the tuning plan, §0.2)

| # | Constraint | Enforcement |
|---|---|---|
| C1 | Never write the live bank `~/.ai-raccoon/memory.db`; never touch the live server (port 7721, PID 55514). Only read-only access is permitted (`?mode=ro`). | harness safety asserts; no code path opens the live path for write |
| C2 | Scratch servers use `--data-root <scratch>` + `--port 0`; port 7721 must never be bound. | server launch asserts |
| C4 | TDD is mandatory for every code change. | per-WP gates |
| C6 | Runtime scratch (bank copies, model downloads for testing) lives OUTSIDE the repo, under `/tmp/embedding-moe/`. Only deliverables (code, docs, small fixtures) are committed. | §10 |

### 0.3 Assumptions (state every assumption; UNVERIFIED = measure before relying)

- **A1** — The case study is `BAAI/bge-m3` (1024-dim, xlm-roberta/sentencepiece, CLS pooling, 8192 ctx,
  MIT, no query instructions). Fact block from the parent task (verified 2026-08-21); my own
  verification of repo contents and ONNX graph is in §2.
- **A2** — "Arbitrary model" means: any ONNX model whose tokenizer family is supported by
  `Microsoft.ML.Tokenizers 2.0.0`, plus any OpenAI-compatible remote endpoint. **UNVERIFIED** which
  tokenizer families ML.Tokenizers 2.0.0 supports beyond Bert and SentencePiece (see §5.2; ops lane
  verifies; bge-m3 needs only SentencePiece, so the case study does not depend on the answer).
- **A3** — The vec0 virtual table **cannot change dimension in place**: sqlite-vec's `vec0` column
  parser requires a literal `float[N]` declaration and rejects vectors of any other dimension
  (verified in source, §3 F9). A dimension change therefore always means DROP + CREATE of the vec0
  tables. This is the load-bearing fact of §7.
- **A4** — A model swap already costs a full re-embed (fingerprint change → `MarkAllEmbeddedPending`,
  §3 F8); the vec0 rebuild adds index drop/create time, not per-row work. Wall time of a bge-m3
  re-embed of the 22.5k-entry bank is **UNVERIFIED** (measured in WP5 on a copy, §10).
- **A5** — OpenAI-compatible endpoints (LM Studio, Ollama, vLLM) return embeddings with dimensions
  the server decides; AiRaccoon must know the dimension **before** the migration starts, because the
  vec0 schema must be right when the re-embed drain inserts rows (F9, F8).
- **A6** — `embedding.model` stays the single pointer for the local engine: a path to a **directory**
  containing `manifest.json` (new) or a path to a **`.onnx` file** (legacy, unchanged semantics).
  `EmbeddingService.CreateLocal` currently requires `File.Exists` on the model path
  (`EmbeddingService.cs:111-126`) — accepting a directory is a small, deliberate change (WP3).
- **A7** — The `scripts/download-embedding-model.py` bundled-asset bootstrap stays as-is; the new
  download surface is a separate CLI verb (§8). Backward compat with the script's output layout is
  preserved (both write under `<data-root>/models/`, distinct subpaths).
- **A8** — Bank copy size 22,514 entries (research record F8; the tuning plan §1 measured 22,509 the
  same day — ±5 entries drift between snapshots, immaterial to the plan).

---

## 1. Verified current state (2026-08-21)

| # | Fact | Evidence |
|---|---|---|
| F1 | Provider dispatch exists: `local` (in-process ONNX) vs `openai` (OpenAI SDK, any OpenAI-compatible endpoint). Engines cached per fingerprint; unknown provider throws. | `EmbeddingService.cs:36-47` |
| F2 | Engine fingerprint = `local:<model-path-or-bundled>` / `openai:<model>@<baseUrl>`; a change triggers the ADR-0076 migration (outbox record + `MarkAllEmbeddedPending`), which re-embeds the whole bank. | `EmbeddingService.cs:102-109`; `IModelMigrationStore.cs:10-25`; `SqliteMemoryStore.ModelMigration.cs:24-38`; `MemorySql.cs:357-359` |
| F3 | Settings rows: `embedding.provider`, `embedding.model`, `embedding.baseUrl`, `embedding.apiKey`, `embedding.engine`. | `EmbeddingSettingsKeys.cs:9-17`; `EmbeddingSettings.cs:4-8` |
| F4 | Bundled local model: int8 all-MiniLM-L6-v2 (`model_qint8_arm64.onnx`, 23.0 MB), BERT vocab.txt, SHA-pinned, downloaded on demand into `src/AiRaccoon/Models`. Custom path via `embedding.model` overrides it. | `BundledModel.cs:17-29,72-81`; research F4 |
| F5 | Local generator: WordPiece BertTokenizer from the **bundled** vocab (options: lower-case, basic tokenization, CJK, special-token split), 256-token window (`MaxContentTokens = 254`), session inputs `input_ids`/`attention_mask`/`token_type_ids`, output `last_hidden_state` → mean-pool + L2-normalize. | `OnnxEmbeddingGenerator.cs:19-47,91-110`; `LocalTokenizer.cs:20-36`; `EmbeddingMath.cs:12-49` |
| F6 | Token budget plumbing is hard-wired to the bundled engine: `ContextTokensFor` returns 256 (local) / 8191 (openai); `SafeChunkBudgetFor` returns 254 for local; `TrimQueryToWindow` counts with the **bundled** WordPiece tokenizer. | `EmbeddingService.cs:54-99` |
| F7 | vec0 schema hardcodes `embedding float[384]` on BOTH `vec_entries` and `vec_structure`, cosine. | `MemorySchema.cs:137,141` |
| F8 | Re-embed machinery exists: migration relay drains `embed_state='pending'` rows bank-wide; old vectors leave the index via `vec_entries_pending`/`vec_structure_pending` triggers the instant the outbox commits. | `MemorySql.cs:357-359`; `MemorySchema.cs:160-164,193-197`; research F8 |
| F9 | **vec0 requires a fixed declared dimension.** `vec0_parse_vector_column` demands `float[N]` (the `[` and digit are mandatory; `dimensions <= 0` is an error) and inserts of mismatched dimension fail ("Vector dimension mistmatch"). No ALTER path for virtual tables. | `sqlite-vec.c` `vec0_parse_vector_column` + dimension checks (https://github.com/asg017/sqlite-vec/blob/main/sqlite-vec.c); project pins `HiraokaHyperTools.sqlite-vec 0.1.9` (`Directory.Packages.props`) |
| F10 | Schema is versioned by a one-time ladder v1…v10 (`MigrateToV1Async`…`MigrateToV10Async`); unconditional, re-runnable DDL (`CREATE … IF NOT EXISTS`) runs on every open and heals missing objects. | `MemorySchema.cs:38-47` and Ddl |
| F11 | CLI surface today: `model set local [path]`, `model set openai <model> [<base-url>] [--api-key <key>]`, `settings model reset|show`. `model set` commits an outbox record and returns; the server relay re-embeds; tool calls are refused while the migration is open (ADR-0076). | `CliCommandTree.cs:151-176`; `SettingsCommands.cs:95-148` |
| F12 | OpenAI-compatible remote path requires a non-empty API key (any string works against LM Studio) and a model id; endpoint defaults to `https://api.openai.com/v1`. | `EmbeddingService.cs:128-147` |
| F13 | Download machinery is half-built: `scripts/src/bundle.py` pins name+URL+SHA-256 for the ONNX model, vocab and a GGUF variant; `scripts/download-embedding-model.py` fetches with SHA verification (`fetch_verified`) into the bundle dir (onnx) or `$AIRACCOON_DATA_ROOT/models` (gguf). No arbitrary-repo download verb exists. | `scripts/src/bundle.py:3-13`; `scripts/download-embedding-model.py:26-52`; research F7 |
| F14 | Eval harness exists (tuning task deliverables, present in this worktree): `scripts/retrieval_tuning/` with `corpora/eval-set-100.json`, `corpora/test-set-10.json`, scratch-server safety asserts; defaults baseline **mean nDCG@5 = 0.6105** (tuned 0.6655). | `docs/work/2026-08-21-parameter-tuning-report.md:12`; `scripts/retrieval_tuning/` |
| F15 | Dependencies: `Microsoft.ML.OnnxRuntime 1.29.0`, `Microsoft.ML.Tokenizers 2.0.0`, `OpenAI 2.12.0`, `HiraokaHyperTools.sqlite-vec 0.1.9`. | `Directory.Packages.props` |
| F16 | LM Studio (localhost OpenAI-compatible `/v1/embeddings`) is structurally reachable TODAY via `ai-raccoon model set openai <model> --base-url http://localhost:1234/v1 --api-key any` (F12). What is missing: dimension knowledge (A5) and any non-384 model support (F9). | parent-task verified fact block |

---

## 2. Case-study facts: BAAI/bge-m3 (verified 2026-08-21, not inherited)

| Fact | Value | Evidence |
|---|---|---|
| Official repo HAS an `onnx/` export | `onnx/model.onnx` (724,923 B) + **external weights** `onnx/model.onnx_data` (**2,266,820,608 B ≈ 2.27 GB**), fp32 | HF tree API `BAAI/bge-m3/tree/main?recursive=true&expand=true` (fetched 2026-08-21) |
| Tokenizer files in `onnx/` | `sentencepiece.bpe.model` (5,069,051 B), `tokenizer.json` (17,082,821 B), `tokenizer_config.json`, `special_tokens_map.json` | same tree API |
| LFS objects carry SHA-256 `oid` (pinnable **before** download) | `model.onnx` oid `f8425123…4435b`; `model.onnx_data` oid `1eebfb28…8416b4`; `sentencepiece.bpe.model` oid `cfc8146a…62865`; `tokenizer.json` oid `6710678b…2faf790` | same tree API, `lfs.oid` field |
| Config | `hidden_size: 1024`, `max_position_embeddings: 8194`, `model_type: xlm-roberta`, `vocab_size: 250002`, 24 layers | `BAAI/bge-m3/resolve/main/onnx/config.json` (fetched 2026-08-21) |
| ONNX graph I/O (parsed from `model.onnx` protobuf, 2026-08-21) | inputs: **`input_ids`, `attention_mask`** (NO `token_type_ids`); outputs: **`token_embeddings`** `[batch, seq, 1024]` AND **`sentence_embedding`** `[batch, dim]` (the graph computes a pooled output internally — there is a `Div` node in the dim param name, semantics UNVERIFIED; must be compared against HF reference embeddings before trusting) | my own protobuf parse of the downloaded `model.onnx` |
| Behavior | 1024-dim fixed output; **NO MRL** (matryoshka truncation not supported by bge-m3 — MRL is a Qwen3/Jina/Voyage feature, research F10); CLS pooling (MCLS for long text), no query instructions, 8192 ctx, MIT license; community GGUF repos exist for LM Studio/Ollama | parent-task fact block; research F2/F10 |

Implications for the architecture:

- **I1** — bge-m3 requires a vec0 rebuild to `float[1024]` (F9): the keep-384 gate alone cannot host it, and MRL truncation does not exist for it. The dimension strategy MUST support dynamic vec0 schema (§7).
- **I2** — The current generator would break on bge-m3 in three places: it always feeds `token_type_ids` (bge-m3 graph has no such input — F5 vs I/O above), it reads `last_hidden_state` (bge-m3 names it `token_embeddings`), and it mean-pools (bge-m3 wants CLS, or the graph's own `sentence_embedding` output). The abstraction in §5 must cover input-name and output-name variability.
- **I3** — The download surface must handle **multi-file ONNX with external data** (`model.onnx_data` must sit next to `model.onnx`; ONNX Runtime loads it automatically when co-located — standard ONNX external-data behavior) and multi-GB files with a size warning.
- **I4** — SentencePiece tokenization is available: `Microsoft.ML.Tokenizers` has `SentencePieceTokenizer.Create(modelStream, addBeginOfSentence, addEndOfSentence, specialTokens)` (parent-task fact block). bge-m3 needs `add_bos`/`add_eos` per `tokenizer_config.json` (UNVERIFIED exact flag values — read from `onnx/tokenizer_config.json` at download time and record in the manifest).

---

## 3. Target architecture (overview)

```mermaid
flowchart TD
    subgraph CLI
        MD["model download &lt;repo-id&gt;<br/>HF resolution + SHA pinning<br/>writes manifest.json + files"]
        MS["model set local &lt;dir|file&gt;<br/>model set openai &lt;model&gt; [--dims N]"]
    end
    subgraph Bank
        SET[(settings<br/>embedding.*)]
        VEC[(vec_entries / vec_structure<br/>float[N] — dimension reconciled<br/>at migration time)]
        MIG[(model_migration outbox<br/>ADR-0076)]
    end
    MD --> DIR["&lt;data-root&gt;/models/&lt;slug&gt;/<br/>manifest.json + model + tokenizer files"]
    MS --> SET
    MS --> MIG
    MIG --> REL[Relay: dimension reconcile<br/>DROP+CREATE vec0 float[N] if needed<br/>→ drain pending re-embed]
    SET --> EMB[EmbeddingService<br/>manifest-aware dispatch]
    DIR --> EMB
    EMB --> VEC
    REL --> VEC
```

Pipeline today (unchanged by this design except where noted): chunker → `SafeChunkBudgetFor`/`TrimQueryToWindow` (F6) → `CreateGenerator` (F1) → embed → vec0 insert via trigger (F8). The manifest (§5) replaces the hard-wired constants in that chain; the dimension reconcile (§7) replaces the hard-wired `float[384]` when a model demands it.

---

## 4. Design decisions at a glance

| # | Decision | Section |
|---|---|---|
| D1 | **Manifest contract**: sidecar `manifest.json` next to local model files; bundled model uses a compiled-in manifest; remote providers use settings rows (no manifest file). | §5, §12 |
| D2 | New optional settings row `embedding.dimensions` (remote dims override); `embedding.model` semantics extended to accept a directory (local). | §5.4, §12 |
| D3 | **Dimension strategy**: dynamic vec0 schema — DROP+CREATE `vec_entries`/`vec_structure` with `float[N]` inside the model-migration relay; keep-384 is an automatic fast path (no rebuild); MRL truncation is a rejected general strategy (only a possible future per-model optimization for MRL-trained models). | §7, §12 |
| D4 | New CLI verb `ai-raccoon model download <repo-id>` (SHA-256 pinned from HF LFS oids); `scripts/download-embedding-model.py` untouched. | §8, §12 |
| D5 | Tokenizer support scope: `bert-wordpiece` + `sentencepiece` required; `tokenizer.json`-BPE models gated on an ML.Tokenizers 2.0.0 capability check (ops lane verifies; likely deferred). | §5.2, §12 |
| D6 | Chunk budget for manifest-local models = `contextWindowTokens − 2`, capped at **510** pending the MCLS question (bge-m3 MCLS pooling for long text is a later enhancement; initial clamp keeps pooling semantics simple). | §5.3, §12 |
| D7 | Engine fingerprint gains the manifest identity + dimension, so replacing a model file under the same path still triggers a re-embed. | §5.5, §12 |

---

## 5. The abstraction: model manifest contract (D1, D2, D5)

### 5.1 What the engine needs to know about an arbitrary model

| Property | Today (hard-wired) | Manifest field |
|---|---|---|
| Output dimension | `EmbeddingMath.Dimension = 384` (`EmbeddingMath.cs:12`) | `dimensions` (int) |
| Tokenizer family | WordPiece from bundled `vocab.txt` (`LocalTokenizer.cs:20-36`) | `tokenizer.family` = `bert-wordpiece` \| `sentencepiece` \| `tokenizer-json` (gated, D5) |
| Tokenizer files + pins | bundled vocab, SHA-pinned (`BundledModel.cs:18-23`) | `tokenizer.files[]` (path + sha256) |
| Tokenizer options | `BertOptions` in `OnnxEmbeddingGenerator.cs:40-47` | `tokenizer.options` (family-specific: lower-case flags for bert; addBegin/EndOfSentence + specialTokens for sentencepiece) |
| Pooling | mean-pool + L2 (`OnnxEmbeddingGenerator.cs:108-110`) | `pooling.mode` = `model-output` \| `mean` \| `cls` \| `last-token`; `pooling.output` names |
| Normalization | always L2 (`EmbeddingMath.cs:44-48`) | `normalization` = `l2` \| `none` |
| Context window | 256 local / 8191 openai (`EmbeddingService.cs:54-63`) | `contextWindowTokens` (int) |
| Model inputs/outputs | fixed names (`OnnxEmbeddingGenerator.cs:91-97`) | `onnx.inputs[]`, `onnx.embeddingOutput`, `onnx.tokenEmbeddingsOutput` (auto-detect fallback) |
| Query instruction prefix | none | `queryInstruction` (string \| null) — e.g. `"query: "` for e5 models; bge-m3 = null |
| MRL | none | `mrl.supported` (bool, default false) — informational; only MRL-trained models may declare it |
| Provider kind | settings row (`embedding.provider`) | `provider` = `local` \| `openai` (must match the settings row; validation) |
| Identity for fingerprint | model path/id + baseUrl (F2) | `source.repo`, `source.revision`, `model` |

### 5.2 Tokenizer family support (D5)

- `bert-wordpiece` — already implemented (`BertTokenizer`, `OnnxEmbeddingGenerator.cs:40-47`).
- `sentencepiece` — `Microsoft.ML.Tokenizers` `SentencePieceTokenizer.Create(modelStream, addBeginOfSentence, addEndOfSentence, specialTokens)` (parent-task fact block). Required for bge-m3 (xlm-roberta).
- `tokenizer-json` (HF `tokenizer.json` BPE, e.g. Qwen2/GPT-2 family) — **UNVERIFIED** whether ML.Tokenizers 2.0.0 can consume HF `tokenizer.json` directly. Ops lane verifies; if unsupported, manifest validation rejects `tokenizer-json` with an actionable error and the Qwen3-style models stay out of scope (they also lack official ONNX exports — research F9).

**Manifest validation rules (WP1):** unknown family → reject; `dimensions <= 0` → reject; declared sha256 missing or malformed → reject; file list empty for provider `local` → reject; `pooling.mode=model-output` requires `onnx.embeddingOutput`; `mrl.supported=true` requires `mrl.minDimensions` and is restricted to a vetted allowlist (MRL is a quality-critical claim — no generic trust).

### 5.3 Chunk budget and query trimming (D6)

`SafeChunkBudgetFor` and `TrimQueryToWindow` (`EmbeddingService.cs:74-99`) become manifest-aware:

- manifest-local: budget = `contextWindowTokens − specialTokensReserved` (2 for both bert and sentencepiece BOS/EOS patterns), **capped at 510** in v1 (D6). Rationale: bge-m3 uses MCLS pooling above 512 tokens (parent fact block); until MCLS is implemented, clamping keeps the CLS-pooling semantics the model was validated with. Quality impact of 510-token chunks (vs today's 254) is an eval question for WP5, not a silent change.
- remote: unchanged 8191 default (F6), overridable later via settings.
- token counting uses the **engine's own tokenizer**, not the bundled `LocalTokenizer` — the `ILocalTokenizer` seam (`LocalTokenizer.cs:20-36`) generalizes to an `IEmbeddingTokenizer` resolved per engine (engineer lane detail).

### 5.4 Where the manifest lives (D1, D2)

| Case | Location | Notes |
|---|---|---|
| Downloaded local model | `<data-root>/models/<slug>/manifest.json` + files | `<slug>` = sanitized repo id (e.g. `BAAI__bge-m3`); the directory is the atomic unit (download → verify → activate) |
| Hand-placed local model | any directory the user points `model set local <dir>` at | loader requires `manifest.json` in that directory |
| Bundled model | **compiled-in** constants (extend `BundledModel.cs:17-29` pattern) | no file, no behavior change; the null-manifest case for fresh banks |
| Legacy custom path | `embedding.model` = path to a `.onnx` file, no manifest | unchanged semantics (F5) — see §9 |
| Remote (openai) | settings rows: `embedding.model`/`baseUrl`/`apiKey` + optional **`embedding.dimensions`** | no files exist for a remote model; a full manifest file is rejected as over-engineering (A: settings rows are one integer; B: manifest files were considered for remote and rejected — two sources of truth for the same provider, no files to pin) |

**Why sidecar file and not settings rows for local:** the manifest carries hashes, tokenizer pairing and pooling semantics — structured, versioned data a flat key-value settings table handles badly; and the directory+manifest pair is atomic with the files it describes (a settings-row approach can point at a half-downloaded directory). **Why not a bundled registry:** a checked-in catalog cannot cover arbitrary user models; the manifest IS the registry entry, user-extensible.

### 5.5 Engine identity (D7)

`EngineFingerprint` (`EmbeddingService.cs:102-109`) gains the manifest identity:

```
local:<path>#<model>@<dims>            (manifest case)
local:<path>                           (legacy file case — unchanged)
openai:<model>@<baseUrl>[:<dims>]      (dims present only when embedding.dimensions set)
```

This keeps F2's invariant — fingerprint change ⇒ full re-embed — true when a manifest file is replaced under the same path. Backward compat: a legacy `embedding.engine` value already stored in a bank is untouched until the next `model set`.

### 5.6 Example manifest (bge-m3; exact field names are D1, engineer lane owns the C# record)

```json
{
  "manifestVersion": 1,
  "model": "BAAI/bge-m3",
  "source": { "repo": "BAAI/bge-m3", "revision": "main", "provider": "huggingface" },
  "provider": "local",
  "dimensions": 1024,
  "contextWindowTokens": 8192,
  "queryInstruction": null,
  "normalization": "l2",
  "mrl": { "supported": false },
  "tokenizer": {
    "family": "sentencepiece",
    "files": [ { "path": "sentencepiece.bpe.model", "sha256": "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865" } ],
    "options": { "addBeginOfSentence": true, "addEndOfSentence": true, "specialTokens": "<s>,</s>,<unk>,<pad>,<mask>" }
  },
  "onnx": {
    "files": [
      { "path": "model.onnx", "sha256": "f84251230831afb359ab26d9fd37d5936d4d9bb5d1d5410e66442f630f24435b" },
      { "path": "model.onnx_data", "sha256": "1eebfb28493f67bba03ce0ef64bfdc7fc5a3bd9d7493f818bb1d78cd798416b4" }
    ],
    "inputs": ["input_ids", "attention_mask"],
    "tokenEmbeddingsOutput": "token_embeddings",
    "embeddingOutput": "sentence_embedding"
  },
  "pooling": { "mode": "model-output" }
}
```

`pooling.mode` is initially `model-output` (use `sentence_embedding`) **only after** WP5's parity check against HF reference embeddings (§10 WP5 gate); until then the implementation must support both modes and the manifest default for bge-m3 is decided by the measured parity (open question Q3).

---

## 6. Provider taxonomy: local ONNX vs remote OpenAI-compatible

| Capability | local ONNX (in-process) | remote OpenAI-compatible (LM Studio / Ollama / vLLM / OpenAI) |
|---|---|---|
| Offline operation | ✅ full | ❌ endpoint must be up for embed AND search |
| Model choice | any ONNX export with a supported tokenizer | only models the server has loaded (GGUF etc.); community GGUF repos exist for bge-m3 (parent fact block) |
| Dimension control | manifest declares it; vec0 reconciled (§7) | server decides; must be declared up front via `--dims` (A5) or assumed 384 (legacy, F16) |
| Tokenizer control | ours (ML.Tokenizers) — parity must be proven per model | server-side; no control |
| Pooling/normalization | ours or graph-provided (`model-output`) | server-side (e.g. llama.cpp embed pipeline — normalization behavior **UNVERIFIED**; ops lane checks LM Studio) |
| Input names / special tokens | manifest-driven, validated at load | opaque |
| Per-embed cost | local CPU (bge-m3 fp32 24 layers: slow; **UNVERIFIED** how slow) | server latency + quantization (GGUF Q5_K_M etc.; quality delta vs fp32 ONNX **UNVERIFIED** — ops lane) |
| Setup cost | download ~2.27 GB for bge-m3 | LM Studio download + load model in GUI |
| Failure mode | local file errors, fail-fast at load | endpoint down / wrong model id / dims mismatch — fails during re-embed drain (risk R4) |

**What each can/cannot do, stated plainly:**

- **Local ONNX is the quality-controlled path**: AiRaccoon owns tokenization, pooling, normalization and can verify vectors against HF references; it is the only path that can host bge-m3 with proven semantics. It cannot host models without ONNX exports (Qwen3-Embedding-0.6B — research F9) and is bound by ML.Tokenizers support (D5).
- **Remote is the zero-download path**: LM Studio with bge-m3 GGUF works structurally TODAY (F16) for a 384-dim-world… except it does not: bge-m3 is 1024-dim, so even the remote path needs §7 before it can serve bge-m3. For **384-dim remote models** (e.g. a MiniLM GGUF in LM Studio) the remote path works today with zero changes — worth stating as the first achievable remote win, testable in WP5 with a scratch server.
- Remote cannot do client-side pooling choices, cannot be pinned (server-side model swap changes vectors silently — a fingerprint cannot see it), and its dims must be declared. These are accepted limitations; the manifest contract does not attempt to paper over them.

---

## 7. Dimension strategy (D3)

### 7.1 Options and trade-offs

| Option | Works for bge-m3 (1024)? | Schema impact | Re-embed cost on the 22.5k bank (F8, A8) | Verdict |
|---|---|---|---|---|
| **A. Dynamic vec0 schema per model** — on engine switch, the migration relay DROPs and re-CREATEs `vec_entries`/`vec_structure` with `float[N]` (triggers recreated by the unconditional DDL, F10), then the existing pending-drain re-embeds | ✅ | DROP+CREATE (virtual tables; seconds — index structures only, no data copy) | re-embed of 22,514 entries is already mandatory on any engine switch (F2); the rebuild adds no per-row cost. bge-m3 fp32 24-layer CPU re-embed wall time **UNVERIFIED** — measured in WP5 | **RECOMMENDED** |
| **B. Keep-384 gate** — refuse any model whose manifest dims ≠ 384 | ❌ (rejects the case study) | none | none (nothing changes) | Rejected as the *strategy*; adopted as an *automatic fast path* inside A: dims == 384 ⇒ no rebuild, exactly today's behavior. Also serves as a pre-WP4 validation gate so early adopters cannot break their bank mid-phase |
| **C. Client-side MRL truncation** — embed at native dims, store first 384 | ❌ (bge-m3 has no MRL — I1) | none | none | Rejected as the *general* strategy (it silently discards signal for non-MRL models and produces garbage vectors); noted as a possible *future per-model optimization* for MRL-trained models (Qwen3/Jina/Voyage — research F10) declared via `mrl` in the manifest. Not in scope for bge-m3 |

### 7.2 The recommended mechanism (Option A) — how it works

1. `model set` commits the new engine + manifest identity (F11; fingerprint per §5.5) and marks all embedded rows pending (F8) — unchanged.
2. The relay's migration job reads the target dimension: manifest `dimensions` (local) or `embedding.dimensions` (remote), defaulting to 384 when absent.
3. **Dimension reconcile** (new, idempotent, runs inside the migration lock so no tool call can interleave — ADR-0076 ToolGate): read the actual vec0 dimension from `sqlite_master` (`SELECT sql FROM sqlite_master WHERE name IN ('vec_entries','vec_structure')`); if it differs from the target → `DROP TABLE vec_entries; DROP TABLE vec_structure;` — the unconditional DDL (`CREATE … IF NOT EXISTS` with `float[N]`, F10) recreates them (the DDL's dimension becomes a parameter of the bank's active engine rather than a constant).
4. The pending drain re-embeds every row; the existing triggers populate the new vec0 tables (F8). No query-side change: vector search SQL is dimension-agnostic (MATCH + k).
5. Failure safety: a crash between DROP and CREATE is healed by the every-open DDL (F10); a crash mid-drain leaves the outbox open and the relay retries (F2/F11 machinery).

**Why this beats the alternatives for this codebase:** it reuses the two mechanisms that already exist and are proven (the outbox migration + the pending-drain triggers, F8/F11), it is reversible (switch back to MiniLM ⇒ rebuild to 384 again), and the schema ladder (F10) stays untouched because the dimension is a *runtime property of the engine*, not a one-time schema version — the same bank may legitimately flip 384 → 1024 → 384 over its life.

**Cost statement for the owner:** the re-embed of 22,514 entries is paid once per switch, in both directions, for ANY engine change (already true today — F2). The vec0 rebuild itself is index drop/create (seconds). The new, case-study-specific cost is bge-m3's fp32 CPU inference during the drain — **UNVERIFIED**, to be measured in WP5 on a bank copy before any recommendation.

---

## 8. Download-model surface (D4)

### 8.1 CLI verb

```
ai-raccoon model download <repo-id>
    [--revision <rev>]        # default: main
    [--file <path-in-repo>]   # repeatable; default: auto-select (below)
    [--dir <target>]          # default: $AIRACCOON_DATA_ROOT/models/<slug>
    [--dry-run]               # resolve + list files/sizes/SHA-256, download nothing
    [--yes]                   # skip the size confirmation for downloads > 500 MB
    [--set]                   # chain: model set local <dir> after verified download
```

This is a new top-level operation under the existing `model` family (`CliCommandTree.cs:151-164` pattern); `scripts/download-embedding-model.py` and its `scripts/src/bundle.py` pins stay untouched (A7).

### 8.2 Resolution and file selection

1. `GET https://huggingface.co/api/models/<repo-id>/tree/<revision>?recursive=true&expand=true` — the `expand=true` form returns `lfs.oid` (SHA-256) per LFS file, verified working for bge-m3 (§2). Fail if the repo or revision is missing.
2. Auto-selection (overridable by `--file`): model file = `onnx/model.onnx` (preferred) else `model.onnx` at root; **plus every sibling it externalizes** — any `*.onnx_data*` file in the same directory (bge-m3 case, I3) — detected from the file list, not guessed.
3. Tokenizer pairing (from `config.json` `model_type`, priority `onnx/<file>` over root): `bert`/`bert-*` → `vocab.txt`; `xlm-roberta`/`roberta` → `sentencepiece.bpe.model`; `t5` → `spiece.model`; `gpt2`/`llama`/`qwen2` → `tokenizer.json` (subject to D5). Also fetch `config.json` (dims/ctx source) and `tokenizer_config.json` (special-token flags).
4. Write `manifest.json` (§5.6) with `dimensions` from `hidden_size` and `contextWindowTokens` from `max_position_embeddings − 2` (config.json values; for bge-m3: 1024 / 8192 — §2).

### 8.3 SHA-256 pinning (never trust unverified)

- **Primary pin source: HF LFS `oid`** (sha256 hex) — captured from the tree API **before** download, so the pin is not self-referential (no trust-on-first-use).
- Non-LFS files (e.g. small `config.json`): pinned from the bytes actually downloaded, recorded in the manifest (TOFU for non-model metadata only — acceptable; the model and tokenizer files are LFS-pinned).
- Download verification: reuse the `fetch_verified`/`BundledResource.IsVerified` pattern (`scripts/download-embedding-model.py:26-52`; `BundledModel.cs:218-229`) — mismatch ⇒ delete the artifact, exit non-zero, no half-installed model (research F7 flow).
- Every subsequent load verifies all pinned files against the manifest before the engine is built (same `IsVerified` pattern); a tampered or drifted file fails loudly at `model set` / engine-build time, never silently degrading retrieval.
- Multi-GB guard: print total size and require `--yes` above 500 MB (bge-m3: 2.27 GB).

### 8.4 Where files land

```
<data-root>/models/<slug>/            # slug = repo id sanitized, e.g. BAAI__bge-m3
  manifest.json
  model.onnx            (724,923 B)
  model.onnx_data       (2,266,820,608 B)
  sentencepiece.bpe.model
  config.json, tokenizer_config.json   # provenance, not loaded by the engine
```

Consistent with the gguf precedent (`download-embedding-model.py:32-35` writes `$AIRACCOON_DATA_ROOT/models`); subdirectory per model so multi-file models are atomic (A7). Activation is a separate, explicit step (`model set local <dir>`, or `--set`), so downloading never changes the running engine.

---

## 9. Backward compatibility and migration path

| Existing configuration | Behavior after this plan | Why it keeps working |
|---|---|---|
| Fresh bank, bundled MiniLM (default) | identical | no manifest ⇒ compiled-in manifest; dims 384 ⇒ no rebuild; fingerprint `local:bundled` unchanged (F2) |
| `embedding.model` = custom `.onnx` path, no manifest (legacy) | identical (WordPiece + bundled vocab + 384 + mean-pool) | no manifest ⇒ legacy semantics (A6); documented limitation: such paths cannot host non-WordPiece/non-384 models — the manifest is the upgrade path |
| `openai` row without `embedding.dimensions` | identical (384 assumption; 8191 ctx) | absent row ⇒ legacy defaults (F16) |
| Banks at schema v10 | no ladder step runs | the dimension reconcile is a runtime property (F10, §7.2), not a schema version |
| `scripts/download-embedding-model.py`, bundle pins | untouched | A7 |
| Existing `embedding.engine` values | untouched until next `model set` | §5.5 |

**Migration path for an existing bank to a new model:** `model download` (downloads + verifies) → `model set local <dir>` (outbox commits; ToolGate blocks tools; relay reconciles vec0 dims + re-embeds; bank is searchable again with the new engine). Rollback: `model set local` back to the bundled model — the same machinery rebuilds to 384 and re-embeds. Nothing in this path writes outside the bank + `<data-root>/models/` (C1/C2 apply to all testing).

**Phased rollout guard:** until WP4 lands, `model set local <dir>` validates the manifest and **refuses dims ≠ 384** with an actionable message (Option B as a gate, §7.1) — mid-phase users cannot create a half-broken bank.

---

## 10. Phased work packages with acceptance gates

All phases: TDD mandatory (repo invariant), scratch under `/tmp/embedding-moe/` (C6), no live-bank or port-7721 access (C1/C2). **No behavior change for any existing configuration before WP5** — WPs 1–4 are additive or behavior-preserving (golden tests prove it).

| WP | Lane | Deliverables | TDD / gate |
|---|---|---|---|
| WP0 | all | This plan reviewed; scratch root created | owner/MoE review (G0) |
| WP1 | arch+eng | Manifest contract: C# record + JSON (de)serializer + validation; golden fixtures (`null-manifest` legacy, full bge-m3 manifest, malformed set); schema doc | G1: unit tests green; validation rejects bad dims/sha/family; fixtures round-trip. **No runtime use yet** |
| WP2 | eng+ops | `model download` verb: HF tree resolution, LFS-oid pinning, multi-file/external-data handling, manifest writer, `--dry-run`, size guard | G2: fixture-repo tests with a mocked HF API; SHA-mismatch test watched RED→GREEN; `--dry-run` on real `BAAI/bge-m3` prints the §2 file list + oids WITHOUT downloading; real 2.27 GB download happens only in WP5 scratch |
| WP3 | eng | Behavior-preserving engine generalization: manifest-driven tokenizer family (bert + sentencepiece), inputs/outputs, pooling (`mean` \| `model-output` \| `cls` \| `last-token`), normalization; `IEmbeddingTokenizer` per engine (replaces hard-wired `LocalTokenizer` usage in F6); `EmbeddingMath.Dimension` → runtime; `CreateLocal` accepts a directory (A6); fingerprint per §5.5 | G3: full `dotnet test` suite green; **golden vector equality** — bundled MiniLM embeddings byte-identical before/after refactor over the eval-set-100 query texts; legacy custom-path test unchanged; log-event ids 414/415/416 preserved |
| WP4 | eng | Dimension reconcile (D3): vec0 DROP+CREATE with `float[N]` inside the migration relay; `embedding.dimensions` settings row (remote); `--dims` on `model set openai`; pre-WP5 gate: `model set local` refuses manifest dims ≠ 384 | G4: fixture-bank tests on a scratch server — (a) 1024-dim manifest + tiny committed fixture ONNX (test-only, ~MBs) migrates: counts parity (entries == vec rows), `sqlite_master` DDL now `float[1024]`; (b) crash mid-migration (kill -9 the relay) recovers on restart (outbox still open → retry); (c) legacy 384 bank untouched (DDL unchanged); (d) `model set openai --dims 1024` writes the row and the fingerprint includes dims |
| WP5 | ops+eng+eval | bge-m3 case study (the only behavior-changing phase): real download (2.27 GB, scratch), manifest + pin verification, parity check of `sentence_embedding` vs HF reference embeddings (sentence-transformers) — RED before trusting (Q3), swap on a bank **COPY** (scratch server, `--data-root`, port ≠ 7721), timed full re-embed, eval harness run (F14) | G5: parity test passes (cosine ≥ 0.999 on ≥ 20 spot-check texts vs HF reference — threshold to be set from measurement); eval report: defaults (0.6105) vs bge-m3 mean nDCG@5 (+ tuned 0.6655 comparison), test-set-10 grades, per-query regression table (tuning-plan discipline); re-embed wall time recorded; **default model unchanged** — owner decides from the report |
| WP6 | arch+eng | ADR (supersedes/extends docs/adr/0036 tokenizer pinning), docs drift audit, PR | G6: ADR reviewed; one squash-merge PR from this worktree |

Also in WP5 (cheap, high value): a **384-dim remote smoke test** — point `model set openai` at a MiniLM-class GGUF in LM Studio (F16) on a scratch server and record that the remote path works end-to-end with zero code changes (the first achievable remote win, §6).

---

## 11. Risks

| # | Risk | Mitigation |
|---|---|---|
| R1 | bge-m3 download is 2.27 GB fp32; no official int8 ONNX (**UNVERIFIED** whether onnx-community has a quantized export — ops lane checks); slow CPU re-embed | size guard + `--yes`; measurement in WP5 before any recommendation; quantization is a follow-up, not a blocker |
| R2 | `sentence_embedding` graph output semantics unverified (the `Div` node, §2) — wrong pooling silently degrades retrieval | WP5 parity gate vs HF reference; fallback `cls` from `token_embeddings` is supported by the manifest (`pooling.mode`) |
| R3 | SentencePiece tokenizer parity (add_bos/add_eos/special tokens) — mismatches produce garbage vectors | tokenizer flags recorded from `tokenizer_config.json` at download; parity test in G5 covers it |
| R4 | Remote endpoint down or dims wrong at migration time ⇒ re-embed drain fails while the ToolGate blocks tools (ADR-0076) | pre-flight probe embed (one call, check dims match declared) before the migration commits, when `embedding.dimensions` is set; failure path documented (outbox stays open; retry after fixing endpoint) — design question Q5 |
| R5 | Replacing a model file under the same path without a manifest change silently keeps old vectors | fingerprint includes manifest identity + dims (§5.5) |
| R6 | Ranking regressions from a better model (RRF rank-fragility — tuning plan §2) | eval harness + per-query regression table gate (G5); default unchanged until owner decision |
| R7 | vec0 rebuild window (DROP→CREATE) exposed to a crash | every-open DDL heals (F10); migration outbox retries (F11); covered by G4(b) crash test |
| R8 | `tokenizer.json`-BPE models unsupported in ML.Tokenizers 2.0.0 (D5) | manifest validation rejects them with an actionable error; Qwen3-class models already lack ONNX exports (research F9) — documented scope |
| R9 | Chunk budget change 254 → 510 (D6) alters embedding granularity even for the bundled model if applied too broadly | D6 applies ONLY to manifest-local models; bundled/legacy paths keep 254; eval measures the bge-m3 chunking in WP5 |

---

## 12. Cross-lane decisions and handoffs (explicit)

| D# | Decision | Owner | Handoff to |
|---|---|---|---|
| D1 | Manifest: sidecar `manifest.json` (local), compiled-in (bundled), settings rows (remote). JSON schema v1 field names per §5.6 — final field names are the engineer lane's C# record; ops lane must not ship a download tool emitting a different shape | architect (this doc) + engineer | ops (download tool output), engineer (C# record) |
| D2 | Settings additions: `embedding.dimensions` (optional, remote); `embedding.model` accepts a directory (local). No settings removals | engineer | ops (docs), test lanes |
| D3 | Dimension strategy = dynamic vec0 schema (DROP+CREATE inside migration relay); keep-384 automatic fast path; MRL rejected as general strategy | architect (this doc) | engineer (reconcile step), eval (quality measurement) |
| D4 | CLI verb `model download <repo-id>` per §8; bundled script/pins untouched | engineer | ops (bge-m3 download verification), docs |
| D5 | Tokenizer scope: bert-wordpiece + sentencepiece in v1; tokenizer-json gated on ML.Tokenizers capability check | ops verifies capability; engineer implements | — |
| D6 | Manifest-local chunk budget = ctx − 2, capped at 510; MCLS deferred | architect + eval | engineer (budget plumbing) |
| D7 | Fingerprint = `local:<path>#<model>@<dims>` / `openai:<model>@<baseUrl>[:<dims>]` | engineer | ops (docs) |
| D8 | bge-m3 pooling default (`model-output` vs `cls`) decided by WP5 parity measurement, not by this plan | eval (WP5) | engineer (manifest default) |

---

## 13. Open questions for the reviewer

1. **Q1** — Is 2.27 GB fp32 acceptable as the case-study artifact, or should the ops lane first confirm a quantized community export (R1)? The architecture is indifferent; the case study's cost is not.
2. **Q2** — Should `--set` chaining on `model download` be allowed to trigger a migration on the LIVE bank, or stay strictly download-only (my recommendation: download-only; activation is an explicit `model set`)? 
3. **Q3** — `sentence_embedding` output parity (R2): if the graph's pooled output does not match HF reference behavior, fall back to CLS-from-`token_embeddings` — no architecture change needed (D8).
4. **Q4** — Remote dims via settings row (D2) vs a remote manifest file: this plan picks the settings row (A: no files to pin; B: single source of truth). Confirmed?
5. **Q5** — Migration failure semantics for remote endpoints (R4): retry-forever (bank blocked) vs fail-closed with explicit `model set` re-issue. The current ADR-0076 machinery implies retry-forever; a decision is needed before WP4.

---

## Sources

- Code: `src/AiRaccoon.Infrastructure/Embedding/{EmbeddingService,EmbeddingSettings,EmbeddingSettingsKeys,OnnxEmbeddingGenerator,LocalTokenizer,EmbeddingBlob,BundledModel}.cs`, `src/AiRaccoon.Core/Embedding/EmbeddingMath.cs`, `src/AiRaccoon.Core/Memory/{EmbeddingConfig,IModelMigrationStore}.cs`, `src/AiRaccoon.Infrastructure/Sqlite/{MemorySchema,MemorySql}.cs`, `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.ModelMigration.cs`, `src/AiRaccoon/Setup/Cli/{CliCommandTree.cs,Commands/SettingsCommands.cs,Commands/ConfigCommands.cs}`, `src/AiRaccoon/Settings/*`, `Directory.Packages.props` (all read 2026-08-21).
- Scripts: `scripts/src/bundle.py`, `scripts/download-embedding-model.py`.
- Records: `docs/work/2026-08-21-embedding-model-replacement.md` (research F4–F11), `docs/work/2026-08-21-parameter-tuning-plan.md` (§0.2 constraints, §2 rank-fragility), `docs/work/2026-08-21-parameter-tuning-report.md` (baseline 0.6105), worktree `scripts/retrieval_tuning/`.
- External: HF tree API + `onnx/config.json` + `model.onnx` protobuf parse for `BAAI/bge-m3` (2026-08-21); `sqlite-vec.c` `vec0_parse_vector_column` (https://github.com/asg017/sqlite-vec/blob/main/sqlite-vec.c).
- Parent-task verified-fact block (2026-08-21): provider dispatch, bge-m3 behavior (CLS/MCLS, no instructions, 8192 ctx, MIT), `SentencePieceTokenizer.Create` availability, LM Studio reachability via the openai provider.
