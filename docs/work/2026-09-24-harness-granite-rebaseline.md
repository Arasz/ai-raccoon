# Research: harness re-baseline on granite (issue #707)

**Date:** 2026-09-24
**Question:** Now that the harness embeds with the same checkpoint family the
product ships (`granite-embedding-small-english-r2`, ADR-0108) instead of the
frozen gte-family checkpoint the old golden used, how much of the measured
harness-vs-bank gap was the embedding-runtime seam, and what does package D's
per-row leg-rank attribution show on the re-baselined golden?

## Findings

### F1: Harness-computed granite vectors match the bank's own stored vectors at median cosine 0.99999 (n=20), vs 0.5934 for the retired gte/ONNX seam [MEASURED]

Sampling 20 real project rows (40-2000 chars) from a disposable byte-copy of
the live bank, the cosine between the harness's HF/sentence-transformers
CLS+L2 vector and the bank's own stored `vec_entries` vector (its fp16 ONNX
graph's pooled+normalized output) is 0.999982-0.999992 per row (median
0.9999872, mean 0.9999869). The runtime seam Package A measured for the old
checkpoint (ONNX-vs-HF same-text cosine 0.5934) is gone: what remains is
consistent with fp16-vs-fp32 rounding, not a pooling, normalization, or
model-identity difference.

**Evidence:** `sanity_cosine.py` (this task's scratch script, not committed,
see Method below) against a `make_memory_copy.py`-style disposable copy of
`~/.ai-raccoon/memory.db` (never the live bank itself: the vector extension's
init needs a writable connection, which a strict `mode=ro` handle refuses).
This was worked around by copying first, matching the harness's own
sanctioned read-only-live/writable-copy split. Vectors read via the exact native binary
the product's pinned NuGet package ships
(`~/.nuget/packages/hiraokahypertools.sqlite-vec/0.1.9/runtimes/osx-arm64/native/vec0.dylib`,
`Directory.Packages.props:24`), unpacked as little-endian float32 blobs
keyed by `vec_entries.rowid = entries.id`
(`src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:159-160`). Harness vectors
via `ingest.create_embedding_model()` (this PR). Live bank confirmed granite
via `settings.embedding.engine = local:bundled#...` and
`BundledModel.DirectoryName = "granite-embedding-small-english-r2"`
(`src/AiRaccoon.Infrastructure/Embedding/BundledModel.cs:21`).

### F2: Pooling and normalization match the product by construction, not by tuning [READ]

`ibm-granite/granite-embedding-small-english-r2`'s own
`1_Pooling/config.json` declares `pooling_mode_cls_token: true` (every other
mode false); `sentence_transformers.SentenceTransformer` loads that module
automatically, so `llama_index`'s `HuggingFaceEmbedding` (which wraps
`SentenceTransformer` and refuses an explicit `pooling=` argument as
deprecated) CLS-pools without any harness-side choice. The product's manifest
(`src/AiRaccoon/Models/granite-embedding-small-english-r2/ai-raccoon.manifest.json`)
declares `pooling.mode: "model-output"` because its ONNX graph pools CLS
internally, and `OnnxEmbeddingGenerator.PoolAlreadyPooledOutput`
L2-normalizes that pooled output when `normalization: "l2"`, exactly what
`HuggingFaceEmbedding(normalize=True)` (the default) does on the harness
side. A verification run (`smoke_granite.py`, this task) confirmed the
loaded `SentenceTransformer`'s output matches a hand-computed CLS+L2 pool of
the same forward pass at cosine ~1.0 (0.9999999-1.0000001) and diverges from
a hand-computed mean+L2 pool (cosine ~0.933-0.936). CLS, not mean.

**Evidence:** `1_Pooling/config.json` (`hf_fs cat` on the model repo),
`llama_index/embeddings/huggingface/base.py` (installed package, `pip show`
resolves `llama-index-embeddings-huggingface`), the two cited product files,
`ai-raccoon.manifest.json`, `smoke_granite.py` (this task, not committed).

### F3: The harness's manifest-local token budget now mirrors the product's exactly [READ]

`EmbeddingService.ManifestContentBudget` returns
`descriptor.ChunkTokens ?? min(510, ctx-2)`
(`src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:434-446`), and the
bundled granite manifest sets `"chunkTokens": 254`
(`src/AiRaccoon/Models/granite-embedding-small-english-r2/ai-raccoon.manifest.json:75`),
so the explicit value wins and the `min(510, ctx-2)` fallback never applies.
That is 256 tokens total once the two special tokens are added. The harness
now passes `max_length=256` (`EMBED_MAX_SEQ_LENGTH` in `data/knobs.json`) to
`HuggingFaceEmbedding`/`SentenceTransformer`, which otherwise defaults to the
checkpoint's own 8192-token `max_seq_length` (`sentence_bert_config.json`).
An un-pinned harness would have silently diverged from the product's real
per-chunk truncation behavior on any longer-than-254-token row.

**Evidence:** the two cited C# lines, `data/knobs.json`, and
`ingest.create_embedding_model`'s docstring (this PR).

### F4: Package D ships: per-row leg ranks in results.json and the fusion_take / fusion_floor attribution in the report [MEASURED]

`evaluate.py` now persists each row's FTS rank, vector rank and fused rank alongside the old `fts_hit`/`vector_hit` booleans (additive schema), and `report.py` splits the `fusion` taxonomy cell into `fusion_take` (anchor fused below the Take(8) cut) and `fusion_floor` (dropped by the relative floor), rendering a "Fusion-drop attribution" section when any fusion row exists. The golden-conservation test activates as soon as a granite golden (`docs/work/results-granite.json`) is committed.

**Evidence:** `python3 -m pytest scripts/tests/test_llamaindex_harness_{report,evaluate,fusion,retrieve}.py scripts/tests/test_collect_ac_evidence.py -q` → 107 passed, 1 skipped (the golden-conservation test, pending the golden), on this branch, 2026-09-24.

### F5: The granite golden was not regenerated in this task [UNVERIFIED]

Regenerating the pair needs the full-volume path: re-embedding about 56k rows through the harness (about 13 h per the 2026-09-10 P4 lane report) on a fresh bank copy, and the committed `project-corpus-100.json` pins a snapshot older than today's bank (`refresh-retrieval-corpora.py` exits 2 on that mismatch by design). The owner dropped full-volume runs as a routine gate (Package G, 2026-09-10), and this machine is shared by several agent sessions, so the run was not started. The headline harness-vs-bank gap on granite is therefore not measured yet; F1 says the embedding seam that confounded the old golden is gone.

## Method

- **Sanity cosine (F1):** `sanity_cosine.py`/`smoke_granite.py`, scratch
  scripts under this session's scratchpad (not committed, throwaway
  diagnostics, not harness code). Never touched the live bank with a
  writable handle: read-only for settings/counts, a disposable
  `make_memory_copy.py` byte-copy for anything needing the vector extension.
- **Golden regeneration:** the exact P1/P4-documented command shape (see
  `docs/work/2026-09-10-p4-lane-report.md`), run against a fresh
  `make_memory_copy.py` copy of the live bank and the committed
  `project-corpus-100.json` (unchanged corpus; only the embedding engine
  moved), `--offline` once the granite weights were cached.

## Still open

- Regenerate the granite golden overnight: `make_memory_copy.py --live ~/.ai-raccoon/memory.db --target <copy>` → re-pin the corpus with `refresh-retrieval-corpora.py --copy <copy>` → `python3 -m llamaindex_harness.ingest --copy <copy> --store-dir <store> --corpus corpora/project-corpus-100.json --offline` (~13 h) → `evaluate … --out docs/work/results-granite.json --offline` → `report`. The conservation test in `test_llamaindex_harness_report.py` then checks the package D attribution against it.

- The live bank has grown substantially since the corpus was pinned (the
  corpus's `snapshotSha256` predates today's copy by design, and a fresh
  copy always warns on this, per `ingest.py`'s documented behavior). This is
  not itself part of this task's scope.
- F1's cosine sample (n=20) is a sanity check, not a full parity sweep; the
  residual ~1e-5 gap is consistent with fp16-vs-fp32 rounding but was not
  isolated from tokenization-edge-case effects.
