# Configure embedding engines

Select, configure, and switch embedding models for vector search.

> **A fresh bank has no memory engine.** Memory search runs keyword-only until you run
> `ai-raccoon model embedding set local` (Recipe 1, the recommended engine). The `warning`
> on a `memory_search` result says so, and `doctor` reports the same state.

---

## Supported embedding engines

AiRaccoon embeds with one of three engine kinds. Memory and the code corpus each pick their own
independently (Recipe 5); a fresh bank starts with neither configured.

```mermaid
graph LR
    subgraph Local ["Local ONNX Engine (recommended)"]
        ONNX["Bundled granite-embedding-small-english-r2\n(fp16, 384-dim, ~97MB)"]
        L_Prop["• 100% Offline\n• GPU first (WebGPU on macOS)\n• Zero API cost"]
    end

    subgraph LocalOther ["Any other local ONNX model"]
        Other["Downloaded via 'model download'\n(WordPiece, SentencePiece, or any tokenizer.json repo)"]
        O_Prop["• 100% Offline\n• CPU by default; 'gpu' opts in\n• Any dimension"]
    end

    subgraph Remote ["Remote OpenAI-Compatible"]
        OpenAI["OpenAI / Ollama / LM Studio\n(/v1/embeddings)"]
        R_Prop["• Custom domain models\n• Requires API Key or Local Endpoint"]
    end

    ONNX --- L_Prop
    Other --- O_Prop
    OpenAI --- R_Prop
```

### Supported models

| Engine kind | Model | Dimensions | Context window | Used for | Activate with |
|---|---|---:|---:|---|---|
| Local, bundled (default) | `granite-embedding-small-english-r2` (fp16, Apache-2.0) | 384 | 8,190 tokens (chunked to 254 for memory, 510 for code) | Memory + code | `model embedding set local` / `model code set default` |
| Local, downloaded | Any Hugging Face repo whose `config.json` reports a `bert*`/`new`/`gte*` model type with a `vocab.txt` (WordPiece), an `xlm-roberta`/`roberta`/`t5` type (SentencePiece), or that simply ships a `tokenizer.json` (BPE/byte-level, including decoder-style models pooled last-token) | Whatever the manifest declares | Whatever the manifest declares | Memory or code | `model download <repo-id>`, then `model embedding set local <dir>` / `model code set local <dir>` |
| Remote, OpenAI-compatible | Any `/v1/embeddings` endpoint: OpenAI (`text-embedding-3-small`/`-large`), Ollama, LM Studio, etc. | Endpoint-reported, or `--dims` | Provider's own (8,191 assumed) | Memory only — the code corpus is local-only | `model embedding set openai <model> [base-url] --api-key --dims` |

Named examples this project's own research has downloaded or scored: `BAAI/bge-m3`,
`ibm-granite/granite-embedding-english-r2`, `Alibaba-NLP/gte-modernbert-base`,
`jinaai/jina-embeddings-v2-base-code`, the `Qwen3-Embedding` family, and `google/embeddinggemma-300m`
(gated under the [Gemma Terms](https://ai.google.dev/gemma/terms) — see Recipe 4). Any other repo
that satisfies one of the three tokenizer families above is supported the same way;
`model download <repo-id> --dry-run` says so before anything is fetched.

**Before ADR-0108 (pre-1.47.0):** memory used a bundled `all-MiniLM-L6-v2` (int8, 23 MB) and the
code corpus downloaded `faxenoff/code-daemon-embed-v1` (179 MB) by default. Both lost to
granite-small on every retrieval eval run against them — see
[Embedding benchmark](../reference/embedding-benchmark.md) for the numbers and
[ADR-0108](../adr/0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md) for the decision.

### Performance & latency comparison

The bundled granite-embedding-small-english-r2 (fp16) beats the engines it replaced on every
retrieval eval measured, and runs faster on the GPU than they ran on the CPU
([ADR-0108](../adr/0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md)):

| Engine | Model | Where it runs | Memory nDCG@10 |
|---|---|---|---:|
| Local, bundled (current default) | `granite-embedding-small-english-r2` (fp16) | GPU (WebGPU on macOS), else CPU | 0.632 |
| Local, bundled (default before 1.47.0) | `all-MiniLM-L6-v2` (int8) | CPU only | 0.605 |

Full quality and latency numbers for every engine measured, including the remote OpenAI/Ollama
comparison, live in [Embedding benchmark](../reference/embedding-benchmark.md).

---

## Engine configuration recipes

### Recipe 1: Use local bundled ONNX model (recommended)

Switch to or restore the bundled ONNX model:

```bash
ai-raccoon model embedding set local
```

*The local model needs no network access and runs in-process via ONNX Runtime.*

The bundled model is granite-embedding-small-english-r2 (fp16, 384 dimensions, Apache-2.0), and
the code corpus uses the same one (Recipe 5). It replaced all-MiniLM-L6-v2 in 1.47.0, and a bank
embedded with the old model re-embeds once on its own after the upgrade
([ADR-0108](../adr/0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md)).

On macOS the session runs on the GPU through ONNX Runtime's WebGPU provider, which takes far less
CPU per embed. Other platforms run on the CPU. `settings model device` changes where local sessions
run, taking effect on the next server restart:

```bash
ai-raccoon settings model device auto   # default: the bundled model on the GPU, other models on the CPU
ai-raccoon settings model device gpu    # every local model on the GPU where the platform has one
ai-raccoon settings model device cpu    # never the GPU
```

`auto` keeps downloaded models on the CPU because a quantized (int8) model produces slightly
different vectors on the GPU than the ones already stored from the CPU. Switch to `gpu` only for
a model whose bank you are happy to have embedded on the GPU from the start.

### Recipe 2: Configure OpenAI embeddings

Use official OpenAI text embeddings:

```bash
ai-raccoon model embedding set openai text-embedding-3-small --api-key "sk-..."
```

### Recipe 3: Configure Ollama or local LLM server

Point to a local Ollama or LM Studio OpenAI-compatible endpoint:

```bash
ai-raccoon model embedding set openai bge-m3 http://localhost:11434/v1 --api-key "ollama"
```

Declare the output dimension whenever it is not 384 — sqlite-vec cannot infer it, and
the vector index has to be rebuilt to match:

```bash
ai-raccoon model embedding set openai text-embedding-3-large --api-key "sk-..." --dims 3072
```

`model embedding set` probes the endpoint before it commits. A `--dims` the endpoint contradicts,
an endpoint that returns something other than 384 with no `--dims`, or an endpoint that
cannot be reached are all refused with nothing written.

### Recipe 4: Run an arbitrary Hugging Face model locally

Download a model into `<data-root>/models/<slug>`, verified against the SHA-256 pins
Hugging Face publishes as LFS oids, then activate it as a second step:

```bash
ai-raccoon model download BAAI/bge-m3 --dry-run   # resolve, print files, sizes and pins
ai-raccoon model download BAAI/bge-m3 --yes       # >500 MB needs --yes
ai-raccoon model embedding set local <data-root>/models/bge-m3
```

The download writes `ai-raccoon.manifest.json` beside the model files, describing its
dimensions, context window, tokenizer family, pooling and normalization — read from the
repo's own `config.json`, `tokenizer_config.json`, `1_Pooling/config.json` and
`modules.json` rather than guessed. **A model directory without that manifest is
refused**; only the legacy `model embedding set local <file>.onnx` path keeps the bundled defaults.

Any repo that ships a Hugging Face `tokenizer.json` downloads too, whatever its architecture:
ModernBERT (`ibm-granite/granite-embedding-english-r2` and its small sibling,
`Alibaba-NLP/gte-modernbert-base`), `jinaai/jina-embeddings-v2-base-code`, Qwen3-Embedding and
EmbeddingGemma. AiRaccoon reads that file itself, with no native tokenizer library, and matches
Hugging Face's token ids exactly. Community ONNX exports keep several variants side by side, so
pick the one you want with `--file`:

```bash
ai-raccoon model download onnx-community/embeddinggemma-300m-ONNX --file onnx/model_quantized.onnx
ai-raccoon model download onnx-community/Qwen3-Embedding-0.6B-ONNX --file onnx/model_int8.onnx --yes
```

Some models were trained with prompts in front of queries and documents. The download copies
them from the repo's `config_sentence_transformers.json` into the manifest's `queryInstruction`
and `documentInstruction`. ONNX mirrors often leave that file out, so check the base model's card
and set the two fields by hand when they are missing. For EmbeddingGemma they are
`"task: search result | query: "` and `"title: none | text: "`. Changing either field changes the
engine fingerprint, which re-embeds the bank.

EmbeddingGemma is covered by the [Gemma Terms of Use](https://ai.google.dev/gemma/terms), not an
open-source licence. Downloading it for your own use is fine, and the terms claim no rights in the
vectors it produces. Redistributing the weights carries the notice and pass-through duties in
section 3.1 of those terms.

**`pooling.mode` comes from the graph, not only from those files.** A repo with no
`1_Pooling/config.json` leaves the mode to be inferred, and some models pool *inside* their
own ONNX graph — their token-embeddings output is `[batch, dimensions]`, already a vector, so
no token-level mode can be applied to it. After the download verifies the graph it reads that
output's declared rank, and a rank-2 output writes `pooling.mode: model-output` with
`onnx.embeddingOutput` naming it. A manifest written before this existed says something else
(`cls`, typically) and the engine logs event 417 on every load; **activating that directory
corrects the file once** and logs event 424 — activation re-embeds anyway, so the correction
costs nothing there, and the vectors are identical either way (the graph's own pooling was
always what ran). The sha256 pins are not the manifest's own and are left untouched.

**Known refusal — a fairseq-offset tokenizer with no `added_tokens_decoder`.** Whether a
sentencepiece repo is fairseq-offset is decided from data, never the tokenizer class: once the
sentencepiece model file is downloaded, its own piece count is compared against config.json's
`vocab_size`. When the two agree, the piece table's own numbering already IS the model's
vocabulary — the tokenizer_class string doesn't matter, and the download derives special-token
ids straight from the piece table (this is the default path for any sentencepiece repo with no
`added_tokens_decoder`). When `vocab_size` is larger than the piece count — some
xlm-roberta-family repos prepend fairseq's own four specials in front of the sentencepiece
pieces, so their `<s>` is 0 and `<unk>` is 3 while the piece table numbers them 1 and 0 — and the
repo also ships no `added_tokens_decoder`, the piece table is the only available source and its
numbering is the wrong one; writing those ids would embed the wrong `<s>` and `<unk>` for every
sequence without any error. The download refuses instead, naming the measured `vocab_size` and
piece-count difference. Hand-write `ai-raccoon.manifest.json` with the model's real
special-token ids and `tokenizer.options.vocabOffset`, then `model embedding set local` the directory as
usual.

Downloading never activates: `model embedding set local` (or `model code set local` for
the code corpus) is always the explicit next step.

Activation re-checks the pins, not just the manifest: every pinned tokenizer/ONNX file is
re-hashed against the bytes on disk, so a file swapped in place after download (manifest
untouched) is refused rather than silently embedded. The non-LFS provenance files
(`config.json`, `tokenizer_config.json`) are pinned into the manifest the same way and
covered by the same check.

### Recipe 5: Activate the code corpus's embedding engine

The code corpus (`kind=code`/`kind=both` search) has its **own** embedding engine,
configured independently of everything above — activating it never touches
`embedding.provider`/`embedding.model`/`embedding.engine`, and vice versa.

```bash
ai-raccoon model code set default
```

That is the whole recipe. It activates the bundled model (the same one memory uses, so one session
serves both) and marks the code corpus's rows pending for the code-reindex job. Nothing is
downloaded. Before 1.47.0 this command downloaded `faxenoff/code-daemon-embed-v1`; a corpus still
on that model keeps it until you run the command again.

It is the one command every surface that can notice a missing code engine quotes: the
`code engine not configured` search warning, `ai-raccoon doctor`, the MCP server
instructions and the `memory_search` tool description all name this exact string
(`CodeEngineSetup.DefaultModelCommand` — one constant, not six copies).

**Why this one activates when `model download` never does.** `model download` is a fetch
verb and stays one. `model code set default` lives in the `model code set` family, which is the
activating family, and it deliberately does both halves: the surfaces above have to hand a
user something they can paste, and "download, then run a second command with a path you
work out yourself" is a hint people do not complete (#422).

The long way round still works, and is what you want for a non-default model:

```bash
ai-raccoon model download faxenoff/code-daemon-embed-v1
ai-raccoon model code set local <data-root>/models/faxenoff__code-daemon-embed-v1
```

`faxenoff/code-daemon-embed-v1`'s HF repo ships no `added_tokens_decoder` in its
`tokenizer_config.json`; `model download` derives the special-token ids from the
sentencepiece model file's own piece table instead (issue #417, verified against a real
download) — still refusing, naming the missing piece, if a declared token isn't in that
table (D1: never guessed).

The code chunker's budget is 510 content tokens — the model's **measured** window (512)
minus its two special tokens. Activation refuses a manifest whose window is *narrower* than
that, because that engine would silently truncate every chunk at embed time; a *wider*
window is accepted, since under-filled chunks cost recall, not correctness. (Until #422
this gate demanded exactly 126 tokens, derived from an exploration note claiming a
128-token cap the ONNX graph does not have, and the flagship model could not be activated
without hand-editing its manifest. The measurement is on issue #422.)

`vec_code` is a vec0 table like the memory bank's: fresh banks start at `float[768]` (the
default model's dimension), and activation reconciles it to whatever dimension the manifest
declares, in the same transaction — there is no configure-time dimension gate (vec-code-unfix-dim).
A missing/invalid manifest is refused with the loader's own error.

On success the settings write (`embedding.codeModel`/`embedding.codeEngine`/
`embedding.codeDimensions`), the `vec_code` reconcile and invalidating every
already-embedded code row to `pending` commit together in one
transaction — `vec_code` empties in that same commit, so there is no window where it
holds vectors from the old engine. There is **no outbox and no migration wait**: the
command returns immediately, memory tools are never blocked, and `kind=code` search
degrades to FTS5-only until the `code-reindex` maintenance job re-embeds the pending
rows on its own cadence.

```bash
ai-raccoon settings model show          # includes codeModel/codeEngine when set
ai-raccoon settings model code reset    # deletes ONLY the code engine rows
ai-raccoon settings model reset         # the memory engine's reset; never touches code rows
```

---

## Re-embedding lifecycle

This section covers the **memory** engine (`model embedding set local`/`model embedding set openai`) only.
`model code set local` (Recipe 5) does not use this outbox/relay/ToolGate machinery at
all — it invalidates the code corpus in one plain transaction and returns; the
`code-reindex` maintenance job drains it in the background with no tool-blocking window.

Switching embedding engines re-embeds all memories in the active bank. When the new
engine's dimension differs, the drain's **first** step rebuilds `vec_entries` and
`vec_structure` at the new width in one transaction, then refills them as it re-embeds.

**Budget the time before you switch.** The bank refuses every tool call until the drain
finishes. Measured on a 23,520-entry bank: the bundled MiniLM re-embeds in minutes;
bge-m3 (1024-d, fp32, 2.27 GB) runs at ~1.85 entries/s — about **3.4 hours**. A dimension
change costs this in both directions.

```mermaid
sequenceDiagram
    autonumber
    actor Dev as Developer / CLI
    participant Server as AiRaccoon Server
    participant Store as memory.db (SQLite)
    participant Engine as New Embedding Engine
    
    participant Relay as Relay (on-demand job)

    Dev->>Server: `ai-raccoon model embedding set ...`
    rect rgb(240, 240, 240)
        note over Server,Store: one transaction (ADR-0076)
        Server->>Store: Write the new engine settings
        Server->>Store: Write the migration record (the outbox row)
        Server->>Store: Mark every embedded row pending — old vectors leave the index
    end
    Server-->>Dev: Returns here. No progress output.
    note over Server: every tool call now refused: model-migration-in-progress
    Relay->>Store: Claim the migration (lease)
    loop For each pending entry
        Relay->>Engine: Generate vector embedding
        Engine-->>Relay: Return float[] vector
        Relay->>Store: Update vec0 virtual table
    end
    Relay->>Store: Mark the migration finished — the bank serves again
```

The command returns before the re-embedding happens — but that is not the same as the *change* being
quick. Three things follow, and the first is the one that catches people out:

- **The bank refuses tool calls until the migration completes — for minutes, not seconds.** Measured
  on a 25,917-entry bank: **~6 minutes**, refusing every read and write throughout. Plan a model
  change as a maintenance window rather than a settings tweak; it scales with the size of the bank.
  Searching a half-migrated bank
  would return quietly worse results; refusing is the honest alternative.
- **A crash does not lose the migration.** The record is durable, so the next server's startup pass
  finishes it — you do not re-run `model embedding set`.
- **Search degrades to keyword-only in the meantime**, because the stale vectors are dropped when
  the transaction commits rather than being overwritten one at a time.

---

## Related documentation

- [Embedding benchmark data & harness](../reference/embedding-benchmark.md)
- [ADR-0004: Dual vector structure signal](../adr/0004-dual-vector-structure-signal.md)
