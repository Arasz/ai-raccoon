# 0084. An arbitrary embedding model is whatever its manifest says it is

Date: 2026-08-21

## Status

Accepted. Extends `0036` (engine-aware chunk token budget) and `0076` (model set is an
outbox drained by an on-demand relay). Plan:
`docs/work/2026-08-21-arbitrary-embedding-models-plan.md` (rev 2, MoE-reviewed, G0
owner-approved) — lane records `docs/work/2026-08-21-embedding-moe-{architecture,engineer,ops}.md`.

## Context

The local engine was all-MiniLM-L6-v2 and nothing else: 384 dimensions in the vec0 DDL,
a WordPiece tokenizer reading the bundled `vocab.txt`, mean pooling, a 256-token window,
all compiled in. Every one of those was a constant somewhere in the code rather than a
property of the model, so "use a different model" meant "change the build".

ADR-0036 already established that the chunk budget must be counted with the tokenizer
that will do the embedding. That invariant only holds if the engine can *say* which
tokenizer that is.

## Decision

**A local model directory carries `ai-raccoon.manifest.json`, and that file is the
engine's contract.** Dimensions, context window, tokenizer family and files, pooling
mode, normalization, ONNX input/output names and per-file SHA-256 pins all come from it.
A directory without one is refused; a legacy `embedding.model=<path>.onnx` keeps the
pre-manifest defaults.

Consequences that follow from making the model self-describing:

1. **One schema, one read path.** `EmbeddingManifest` is the pinned v1 record;
   `IEmbeddingManifestSerializer` parses it and `IEmbeddingManifestValidator` validates
   it, on both the download (write) and activation (read) paths. Fields that are
   family-specific or informational — `tokenizer.options`, `mrl`, `pooling.outputNames`,
   `requiresTokenTypeIds` — are optional with legacy defaults, because requiring
   `mrl: {supported: false}` on a model that has never heard of MRL is ceremony. The
   rules that bite stay in the validator: sentencepiece needs a numeric special-token
   map, `model-output` pooling needs `onnx.embeddingOutput`.

2. **The tokenizer is an engine property.** `IEmbeddingTokenizer` with wordpiece and
   sentencepiece implementations, resolved through `EmbeddingService`. The repair and
   ingest families route through the same resolver, so ADR-0036's invariant — budget and
   counter agree with the active engine — survives a model swap.

3. **vec0 dimension is reconciled, not assumed.** sqlite-vec infers nothing, so the
   migration drain's *first* phase brings `vec_entries` and `vec_structure` to the
   engine's dimension in one `BEGIN IMMEDIATE` transaction: create-if-missing-or-mismatch,
   both tables, **no repopulate**. Ordering is the point — reconcile after the batch loop
   and the DROP discards what the drain just wrote, with no pending rows left to re-drive
   it, so the migration finishes green over empty tables.

4. **Remote engines declare their dimension before anything is written.**
   `model set openai --dims N` persists `embedding.dimensions`, and a pre-commit probe
   refuses a declared value the endpoint contradicts, refuses silence when the endpoint
   is not 384, and refuses an unreachable endpoint. Discovering the mismatch during the
   drain is too late: the bank is already pending behind a closed ToolGate (0076) with
   nothing able to finish it.

5. **The fingerprint is the manifest's content.** Re-downloading a model to the same path
   changes its file hashes, changes the manifest, changes the fingerprint, and re-embeds.
   Identity by path and dimension alone would silently keep stale vectors.

## Consequences

**A model swap costs a full re-embed, and that is measured, not assumed.** bge-m3 (1024-d,
fp32, 2.27 GB) re-embeds 23,520 entries at ~1.85 entries/s — about 3.4 hours with the
ToolGate closed. Dimension flips cost this in both directions. This is the number that
should decide whether a default change is worth it, and it is why the shipped default is
unchanged.

**Provenance beats guessing, and guessing is silent.** Two defects only surfaced when the
verb ran against real Hugging Face rather than a fake repo, and neither would have
crashed anything:

- `modules.json` names the Normalize module `"name": "2"` with `"path": "2_Normalize"`;
  matching on the name wrote `normalization: none` for a model that is L2-normalized.
- xlm-roberta's `tokenizer_config.json` declares neither `add_bos_token` nor
  `add_eos_token` — the behaviour lives in the tokenizer class — so defaulting to false
  dropped the `<s>` … `</s>` wrapper the model was trained with.

Both produced a working migration with quietly wrong embeddings, and both passed their
tests because the fixtures had been written to match the code rather than the repo. The
lesson is recorded here because it will recur for the next model family: **fixtures
derived from the implementation cannot falsify it.**

**Byte-identity is an architecture-local property.** The G3 golden-vector gate compares
the refactored engine against a pre-refactor capture. On the capture's own architecture
that is bit-for-bit; on another it cannot be, because the bundled model is qint8 and x64
and ARM requantise differently (measured: cosine 0.9895 between x64 and an Arm64 capture).
CI therefore enforces token-id equality — exact and arch-independent — plus a coarse
breakage floor, and the tight bound holds only on the capture arch. Restoring it on CI
needs a per-architecture capture.

**Deferred.** `tokenizer-json` (BPE) is validated and rejected pending an ML.Tokenizers
capability check. MRL truncation is recorded in the manifest and unused. The bge-m3
retrieval-quality comparison is measured separately; this ADR ships the mechanism, not a
default change.

## Amendment (2026-08-22) — activation re-verifies the pins, provenance files are pinned too

The original `IEmbeddingManifestLoader.Load()` only checked `File.Exists` for each pinned
file — a `model.onnx` swapped in place after download, with the manifest untouched, would
activate and embed silently on the wrong bytes. `Load()` now hashes every pinned file
(tokenizer, ONNX) and refuses, naming the file and both digests, on a mismatch (D1).

`ModelDownloadPlanner` already fetched and hashed the non-LFS provenance files
(`config.json`, `tokenizer_config.json`), but `WriteManifestAsync` never wrote them into
the manifest, so they were never covered by the activation check above. `EmbeddingManifest`
now carries an optional `ProvenanceFiles` list (absent on pre-D2 manifests), pinned with
the same trust-on-first-download SHA-256 mechanism as the tokenizer/ONNX files (D2).

## Amendment (2026-08-22, #470) — `pooling.mode` is reconciled to the graph, not trusted over it

"That file is the engine's contract" holds for every field except one. A model can pool
inside its own ONNX graph — its token-embeddings output is `[batch, dimensions]`, already a
vector — and no token-level `pooling.mode` can be applied to such an output. #466 made the
embed path branch on the output's rank rather than the declared mode, which fixed the
embedding but left the manifest saying something untrue and warning (417) on every load.

The manifest is now reconciled to the graph at the two moments the file is written or an
engine is activated, so the two never disagree for long:

- **Download.** The ORT smoke test already loads the graph, so it reports each output's
  declared rank; a rank-2 token-embeddings output writes `pooling.mode: model-output` plus
  `onnx.embeddingOutput` instead of the planner's file-derived inference.
- **Activation.** `IManifestPoolingRepair` rewrites an existing manifest with the same rule,
  once, logging event 424 (or 425 when the file cannot be written, leaving 417 to keep
  firing). It runs at activation and deliberately **not** at load: the local engine
  fingerprint is a hash of the manifest's bytes, and `CodeReindexJob` reconciles that
  fingerprint on every poll, so rewriting the file on a load would re-embed an entire corpus
  for a correction that changes no vector — while an activation invalidates everything
  anyway. Taking the fingerprint after the repair is what keeps the next poll quiet.

Pins are unaffected: a manifest does not pin itself, so the rewrite leaves every recorded
sha256 exactly as it was, and the activation pin check (the amendment above) still runs.
