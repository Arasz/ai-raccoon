# Research: smaller embedding models, the models the downloader blocks, and a use for an edge classifier

**Date:** 2026-09-23
**Question:** Is there an embedding model much smaller than SFR-Embedding-Code-400M with similar memory and code retrieval quality — including models the downloader currently rejects, and what supporting them would cost — and is there any AiRaccoon task where a small zero-shot classifier (OpenJev or an edge model) works?

```chart:matrix
title: size vs quality (fp32 ONNX MB, code MRR, memory nDCG@10, bank nDCG@10)
model, onnx MB, code MRR, memory nDCG, bank nDCG
embeddinggemma-300m int8, 295, 0.815, 0.711, 0.471
granite-embedding-english-r2, 577, 0.698, 0.653, 0.428
gte-modernbert-base, 569, 0.728, 0.652, 0.411
SFR-Embedding-Code-400M (current), 1665, 0.627, 0.642, 0.389
granite-embedding-small-english-r2, 186, 0.624, 0.636, 0.377
mxbai-embed-xsmall-v1, 92, 0.484, 0.586, 0.351
bge-small-en-v1.5, 127, 0.568, 0.640, 0.302
```

## Findings

### F1 — On code, five models under 620 MB beat SFR, one 186 MB model ties it, and the code-daemon model is last [MEASURED]

Code eval: 476 documented C# methods from this repo's `src/`; the query is the `/// <summary>` text, the target is the method with its doc comment stripped, and the corpus is all 476 methods. Metric: the rank of the method's own code.

| model | params | fp32 ONNX | R@1 | R@10 | MRR |
|---|---|---|---|---|---|
| embeddinggemma-300m (int8 export, prefixes) | 303M | 295 MB | 0.727 | 0.960 | **0.815** |
| qwen3-embedding-0.6b (int8 export, instruct) | 596M | 585 MB | 0.626 | 0.926 | 0.738 |
| jina-embeddings-v2-base-code | 161M | 612 MB | 0.618 | 0.929 | 0.734 |
| gte-modernbert-base | 149M | 569 MB | 0.613 | 0.922 | 0.728 |
| granite-embedding-english-r2 | 149M | 577 MB | 0.574 | 0.897 | 0.698 |
| SFR-Embedding-Code-400M_R (current) | 434M | 1,665 MB | 0.502 | 0.838 | 0.627 |
| granite-embedding-small-english-r2 | 48M | 186 MB | 0.481 | 0.876 | 0.624 |
| bge-small-en-v1.5 (query prefix) | 33M | 127 MB | 0.433 | 0.807 | 0.568 |
| snowflake-arctic-embed-m-v1.5 | 109M | 416 MB | 0.401 | 0.807 | 0.537 |
| gte-base-en-v1.5 | 137M | 530 MB | 0.357 | 0.784 | 0.501 |
| nomic-embed-text-v1.5 (prefixes) | 137M | 522 MB | 0.359 | 0.739 | 0.485 |
| mxbai-embed-xsmall-v1 | 24M | 92 MB | 0.332 | 0.761 | 0.484 |
| bundled all-MiniLM-L6-v2 int8 | 23M | 23 MB | 0.347 | 0.765 | 0.482 |
| snowflake-arctic-embed-xs | 22M | 86 MB | 0.233 | 0.592 | 0.362 |
| snowflake-arctic-embed-s | 33M | 127 MB | 0.225 | 0.578 | 0.345 |
| code-daemon-embed-v1 | – | 179 MB | 0.204 | 0.576 | 0.324 |

**Evidence:** `scratchpad/codeeval/codeeval.py` (manifest models, same WordPiece/pooling as production) and `eval2.py` (HF `tokenizer.json` models, model-card pooling and prefixes), python 3 + onnxruntime 1.29.0 CPU, 5 intra-op threads, batch 1, max 512 tokens, one run each (the method is deterministic). code-daemon was scored through `spcode.py` (sentencepiece ids checked against the manifest's `<pad>0 <unk>1 <s>2 </s>3`). Pair extraction was checked by eye on 25 random pairs after a fix that dropped type-level summaries. Mac16,12, 24 GB; other evals were running at the same time, so the `embed_s` timings are not comparable and are left out.

### F2 — On the 174-doc memory corpus every candidate is within noise of SFR; embeddinggemma alone is clearly ahead [MEASURED]

| model | R@10 | MRR | nDCG@10 |
|---|---|---|---|
| embeddinggemma-300m (int8 export, prefixes) | 0.428 | 0.878 | **0.711** |
| qwen3-embedding-0.6b (int8 export, instruct) | 0.390 | 0.871 | 0.662 |
| nomic-embed-text-v1.5 | 0.380 | 0.847 | 0.655 |
| granite-embedding-english-r2 | 0.384 | 0.867 | 0.653 |
| gte-modernbert-base | 0.391 | 0.875 | 0.652 |
| bge-small-en-v1.5 (prefix / none) | 0.387 / 0.385 | 0.843 / 0.842 | 0.645 / 0.640 |
| SFR-Embedding-Code-400M_R | 0.384 | 0.866 | 0.642 |
| granite-embedding-small-english-r2 | 0.387 | 0.840 | 0.636 |
| gte-base-en-v1.5 | 0.373 | 0.873 | 0.606 |
| bundled MiniLM int8 | 0.378 | 0.831 | 0.600 |
| mxbai-embed-xsmall-v1 | 0.373 | 0.836 | 0.586 |
| arctic-embed-xs / -s / -m-v1.5 | 0.356 / 0.360 / 0.348 | 0.829 / 0.807 / 0.776 | 0.584 / 0.591 / 0.573 |
| jina-v2-base-code | 0.360 | 0.829 | 0.566 |

**Evidence:** Supported models: `dotnet run -c Release --project benchmarks/AiRaccoon.Benchmarks` (the repo harness, production `EmbeddingService` path), run twice with identical output. Blocked models: `scratchpad/codeeval/memeval.py` over the same corpus dumped to JSON by a file-based `#:project` app. Its metrics reproduce the harness within 0.006 for arctic-embed-xs (0.831 vs 0.829 MRR) and mxbai (0.833 vs 0.836).

### F3 — A title-to-document eval built from the live bank separates the models far more than the 174-doc corpus, and ranks them like the code eval [MEASURED]

150 random ADR, research and reference documents from a snapshot of `~/.ai-raccoon/memory.db`. The query is the document's H1 title, with the heading line removed from its first chunk. The relevant set is that file's first eight chunks, and the corpus is those 1,037 chunks.

| model | R@10 | MRR | nDCG@10 |
|---|---|---|---|
| embeddinggemma-300m (int8 export, prefixes) | 0.460 | 0.719 | **0.471** |
| granite-embedding-english-r2 | 0.408 | 0.710 | 0.428 |
| gte-modernbert-base | 0.392 | 0.658 | 0.411 |
| SFR-Embedding-Code-400M_R (current) | 0.372 | 0.659 | 0.389 |
| granite-embedding-small-english-r2 | 0.370 | 0.629 | 0.377 |
| mxbai-embed-xsmall-v1 | 0.334 | 0.612 | 0.351 |
| bge-small-en-v1.5 (no prefix) | 0.297 | 0.525 | 0.302 |

The spread is 0.30–0.47 nDCG@10 here, against 0.57–0.71 on the 174-doc corpus, where bge-small had looked like the best small model. On real bank content it is the weakest of these.

**Evidence:** `sqlite3 ~/.ai-raccoon/memory.db ".backup"` → `scratchpad/codeeval/bankcorpus.py <snap> bankcorpus.json 150 8` → `memeval.py`. One run each, seed 5.

### F4 — granite-small-r2 loads in 144 MB and holds 252 MB after inference; SFR holds 2,776 MB [MEASURED]

With the same harness and conditions as `docs/work/2026-09-23-server-memory-usage.md` F4/F6 (ORT 1.30.0 in .NET, 5 threads, arena on), the granite-small fp32 export used 144 MB after load and 252 MB after five 1×510 runs, against 2,742 / 2,776 MB for SFR. Per-run timings (249–589 ms) were taken while two other evals were loading the CPU, so they are not a latency figure.

**Evidence:** `dotnet run ortmem.cs -- <granite-small>/onnx/model.onnx 1 1 1x510 ×5`.

### F5 — SFR-Embedding-Code-400M_R, the model this machine runs, is CC-BY-NC-4.0 [READ]

Every recommended replacement is Apache-2.0 (granite small/r2, gte-modernbert, jina-v2-code, nomic, Qwen3) or MIT (bge-small). embeddinggemma is under the Gemma terms and is gated (manual approval), so it cannot be fetched without a Hugging Face login.

**Evidence:** `https://huggingface.co/api/models/<repo>?expand[]=cardData&expand[]=gated`: `Salesforce/SFR-Embedding-Code-400M_R` → `cc-by-nc-4.0`; `google/embeddinggemma-300m` → `gemma`, `gated: manual`.

### F6 — What blocks each model is the tokenizer family, not the architecture [READ]

ONNX Runtime runs any exported graph. `ModelDownloadPlanner.PairTokenizer` decides support from `config.json`'s `model_type`: `bert*`, `new` and `gte*` map to WordPiece; `xlm-roberta`, `roberta` and `t5` map to SentencePiece; GPT-2, llama and Qwen2 are refused as "tokenizer-json family, not yet supported"; everything else is refused outright. Three blocker groups follow:

| group | models | tokenizer | what support needs |
|---|---|---|---|
| A — WordPiece, blocked by metadata only | bge-small-en-v1.5, nomic-embed-text-v1.5, CodeRankEmbed | `vocab.txt` WordPiece | map `nomic_bert` → WordPiece; derive `[CLS]/[SEP]/[PAD]/[UNK]` ids from `vocab.txt` when `added_tokens_decoder` is missing (bge-small) |
| B — byte-level BPE | granite r2 small/base, gte-modernbert, jina-v2-code, Qwen3-Embedding | `tokenizer.json` BPE + ByteLevel | a `tokenizer.json` → `BpeTokenizer` adapter (vocab, merges, byte-level, template post-processor); Qwen3 also needs a Split regex, `position_ids`, empty KV-cache inputs and last-token pooling |
| C — SentencePiece BPE with byte fallback | embeddinggemma | `tokenizer.json` BPE, `byte_fallback: true` | the gated `tokenizer.model` via the existing SentencePiece path, plus a gated-repo download |

**Evidence:** `src/AiRaccoon.Infrastructure/Embedding/Download/ModelDownloadPlanner.cs:231-257` (`PairTokenizer`) and `:313-316` (special-token refusal). `ai-raccoon model download <repo> --dry-run` refusal messages for each repo. Each export's `tokenizer.json` `model.type`, `pre_tokenizer` and `byte_fallback`, read with python.

### F7 — Microsoft.ML.Tokenizers 2.0 can build a byte-level BPE tokenizer in memory, but has no `tokenizer.json` loader [READ]

`BpeOptions` exposes `Vocabulary`, `Merges`, `ByteLevel`, `SpecialTokens`, `PreTokenizer` and `Normalizer`, and `BpeTokenizer.Create(BpeOptions)` takes them. The package's XML docs name no `tokenizer.json` factory, and mention `tokenizer.json` only as a reference for CodeGen's vocab and merges files. Group B support is therefore a parser from `tokenizer.json` into `BpeOptions`, not a new dependency.

**Evidence:** `~/.nuget/packages/microsoft.ml.tokenizers/2.0.0/lib/netstandard2.0/Microsoft.ML.Tokenizers.xml` (`P:Microsoft.ML.Tokenizers.BpeOptions.*`, `M:...BpeTokenizer.Create(BpeOptions)`); `Directory.Packages.props:36`.

### F8 — Production ignores the manifest's `queryInstruction`, and on the memory corpus that costs about nothing [MEASURED]

`EmbeddingManifest.QueryInstruction` is declared but has no other reference in `src/`. bge-small scored nDCG@10 0.645 with its query prefix and 0.640 without. Prefix-trained models may lose more on other query shapes; only bge-small was measured both ways.

**Evidence:** `grep -rn QueryInstruction src` → the one declaration, `Manifest/EmbeddingManifest.cs:56`. `memeval.py` bge-small with and without the prefix, F2 conditions.

### F9 — OpenJev is not an edge model: 27B parameters, 15–54 GB, non-commercial weights [READ]

OpenJev is a Qwen3.5 fine-tune with 27.36 B parameters and 54.7 GB of stored weights. Its smallest build is a 15 GB MLX 4-bit, and it is served with vLLM on an 80 GB GPU. The weights are CC-BY-NC-4.0. It cannot ship inside a dotnet tool, and it cannot run inside a CPU MCP server's latency budget.

**Evidence:** `https://huggingface.co/api/models/openjev/openjev?expand[]=safetensors` (`BF16: 27356728560`, `usedStorage 54734782719`), `config.json` (`qwen3_5_text`, 64 layers, hidden size 5120), README "Model files" table and license section.

### F10 — Small zero-shot classifiers are near chance at telling this bank's content types apart; a supervised probe on stored embeddings is twice as good [MEASURED]

Task: label 200 bank chunks (50 each) as ADR, research record, reference doc or agent note. Ground truth comes from the chunk's source folder, and chance is 0.25.

| classifier | params | accuracy | macro-F1 | ms/item* |
|---|---|---|---|---|
| logistic regression on stored SFR embeddings (5-fold) | – | **0.685** | **0.684** | – |
| gliclass-instruct-edge-v1.0 | 33M | 0.350 | 0.328 | 17 |
| gliclass-modern-base-v3.0 | 151M | 0.340 | 0.250 | 98 |
| deberta-v3-xsmall-zeroshot-v1.1 (NLI) | 71M | 0.310 | 0.242 | 1,313 |

\*CPU PyTorch while other evals were running. NLI runs one pass per label. deberta-v3-large-zeroshot-v2.0 (435M) was started as an upper bound and stopped unfinished after 65 CPU-minutes at a machine load average of 62, so it is not reported.

**Evidence:** `scratchpad/clf/clfeval.py <snapshot> <model>`, transformers 5.14.1, torch 2.13.0, gliclass (pip, scratch venv), labels phrased as in `CLASSES`, seed 11.

### F11 — The two classification jobs AiRaccoon already has are settled, and neither points to a zero-shot model [READ]

- **Read-path noise guard.** A 200-tree structural GBM reaches held-out AUC 0.964 at 0.107 ms, and an embedding linear probe reaches 0.946 at 14.6–26.5 ms.
- **Promotion worthiness.** Embeddings cap at F1 0.59 with full supervision, and a Qwen2.5-0.5B judge scored Spearman +0.132, below the mechanical scorer's +0.397.

**Evidence:** `docs/adr/0041-structural-noise-detector.md` §Context table; `docs/work/2026-08-13-fixing-zero-shot-promotion-classifier.md` F2, "Measured answer" table.

### F12 — The active bank is usable test data for retrieval and classification, but not for promotion and not as a query-relevance source [MEASURED]

- **Usable:** the bank snapshot produced F3 and F10 without touching the live file.
- **Promotion:** only 11 of the 61 labelled promotion rows still resolve by hash.
- **Query relevance:** of 400 logged `search_quality` queries, 9 carry a usefulness grade and 1 a follow-through, too few to act as relevance judgements.

**Evidence:** `python3 docs/work/promotion-scoring-eval/rebuild_fixture.py reference-labels.json <snapshot> out.json` → "rebuilt 11/61"; `sqlite3 <snapshot> "select count(*), sum(usefulness_grade is not null), sum(follow_through_count>0) from search_quality where project_id='ai-raccoon'"` → `400|9|1`.

### F13 — Recommendation: granite-small-r2 as the light engine, granite-r2 or gte-modernbert as the quality engine; embeddinggemma only if the Gemma terms and gating are acceptable [INFERRED]

This reasons from F1–F5:
- **embeddinggemma** leads all three evals (code MRR 0.815, memory nDCG 0.711, bank nDCG 0.471) at 295 MB int8. It is gated under the Gemma terms and needs the group-C tokenizer path.
- **granite-embedding-english-r2** (149M, Apache-2.0) is second on the bank eval and beats SFR on code by 0.07 MRR at about a third of the disk.
- **gte-modernbert-base** (149M, Apache-2.0) is similar to granite-r2 and slightly stronger on code.
- **granite-small-r2** (48M, 186 MB, 252 MB RAM) ties SFR on code and on the 174-doc corpus, and trails it by 0.012 nDCG on bank content (0.377 vs 0.389), at about a ninth of the disk and a tenth of the RAM.
- **bge-small** needs only group-A work, but it is the weakest on real bank content (F3), so group-A alone does not buy a good model.

Every model worth adopting sits in group B or C. The tokenizer work is the price of the gain.

### F14 — A small classifier has no current job here; the one plausible job is served better by the embeddings already stored [INFERRED]

This reasons from F9–F11: zero-shot edge models scored 0.31–0.35 on a four-way content-type task where a probe on stored vectors scored 0.685. The existing noise guard beats embeddings, and promotion is not recoverable by either approach. If a future feature needs labels (auto-typing writes, routing a query to memory or code), the measured path is a small supervised head on the vectors the bank already computes. That costs about one dot product per class, with no second model in memory.

### F15 — The two NuGet wrappers of HF `tokenizers` cannot cover AiRaccoon's runtimes, so the adapter is managed code on Microsoft.ML.Tokenizers [READ]

`Tokenizers.HuggingFace` 3.23.1 (Apache-2.0, one maintainer) ships native binaries for linux-arm64, linux-x64, osx-arm64, osx-x64, win-arm64 and win-x64. `Tokenizers.DotNet` 1.4.1 (MIT, one maintainer) publishes runtime packages for win-x64, win-arm64, linux-x64, linux-arm64, osx-arm64, osx-x64 and win. Neither has linux-musl-x64, which AiRaccoon publishes. The adapter (PR #680) is therefore written against Microsoft.ML.Tokenizers, already a dependency.

**Evidence:** `unzip -l tokenizers.huggingface.3.23.1.nupkg` (runtimes/ list); NuGet search `Tokenizers.DotNet.runtime`; NuGet registration metadata (authors, licence, publish dates); `src/AiRaccoon/AiRaccoon.csproj:5` (`RuntimeIdentifiers` includes `linux-musl-x64`).

### F16 — EmbeddingGemma: gated at Google, ungated mirror, embeddings unencumbered, weight redistribution carries pass-through duties [READ]

- **Gating.** `google/embeddinggemma-300m` is `gated: manual`: anonymous `resolve/` downloads return HTTP 401. `onnx-community/embeddinggemma-300m-ONNX` downloads anonymously but still declares `license: gemma`.
- **Outputs.** The Gemma Terms of Use (last modified 2026-04-01) state "For clarity, Outputs are not deemed Model Derivatives" (§1.1(e)) and "Google claims no rights in Outputs you generate using Gemma" (§3.3), so stored vectors carry no licence obligation.
- **Distribution.** "Distribution" includes making Gemma available "as a hosted service via API" (§1.1). A distributor must pass the use restrictions through, give recipients the Agreement, and ship a Notice file reading "Gemma is provided under and subject to the Gemma Terms of Use found at ai.google.dev/gemma/terms" (§3.1).
- **Commercial use and termination.** Commercial use is allowed. On breach, Google may terminate, and the licensee must then cease use (§4.5).

**Evidence:** `https://ai.google.dev/gemma/terms` fetched 2026-09-23, "For clarity" clause checked verbatim in the page text. HF API `gated` field and anonymous `curl` HTTP codes (research-agent notes).

### F17 — Two more ≤600 MB candidates from the HF survey do not beat granite-english-r2 [MEASURED]

| model | size | code MRR | memory nDCG@10 | bank nDCG@10 | licence |
|---|---|---|---|---|---|
| harrier-oss-v1-270m (int8, gemma3_text, last-token, query instruct) | 328 MB | 0.668 | 0.612 | 0.385 | MIT |
| granite-embedding-311m-multilingual-r2 (int8 quint8_avx2, cls) | 299 MB | 0.689 | 0.620 | 0.359 | Apache-2.0 |

These use the same scripts and conditions as F1–F3. The code corpus was 477 pairs for these two runs, against 476 earlier, because the main checkout moved in between.

**Evidence:** `eval2.py` and `memeval.py` on the onnx-community and IBM int8 exports; `picks.jsonl` in the scratch dir.

### F18 — Five picks for the ≤600 MB range [INFERRED]

This reasons from F1–F3, F5, F16 and F17:
1. **embeddinggemma-300m int8 (295 MB):** best on all three evals. Acceptable if users download it themselves, since the vectors are unencumbered; distributing the weights carries the pass-through duties in F16.
2. **granite-embedding-english-r2 (577 MB):** best Apache-2.0 model on bank content (0.428), and beats SFR on code.
3. **gte-modernbert-base (569 MB):** granite-r2's peer. Strongest permissive model on code among the encoders (0.728), with the same tokenizer family.
4. **granite-embedding-small-english-r2 (186 MB):** the light engine. It ties SFR on code at about a tenth of the RAM.
5. **Qwen3-Embedding-0.6B int8 (585 MB):** code MRR 0.738, Apache-2.0. It needs the decoder-input support in PR #680.

Dropped: harrier-270m and granite-311m-multilingual (F17); jina-v2-base-code, which is strong on code but weakest on the memory corpus (0.566).

## Still open

- Code eval uses one repo (this one) and summary-style queries. Agent queries are shorter and keyword-heavier, and a second repo would show whether jina/gte's lead transfers.
- Tokenizer parity: the adapter in PR #680 reports exact id parity for 7 models × 10 strings against HF `tokenizers` 0.22.2. The production-path re-run of the picks, including Gemma with and without prefixes, is pending that PR.
- Latency on an idle machine for granite-small, jina-v2-code and gte-modernbert was not measured, because every run shared the CPU.
- int8 exports of granite-small (52 MB) and gte-modernbert (150 MB) were not scored. They would shrink the footprint further, and the quality loss is unknown.
- Qwen3 was run from an int8 export with no explicit EOS append for last-token pooling. Its numbers may be understated.
- CodeRankEmbed (137M, nomic_bert, WordPiece; group A) has no public ONNX export and was not scored.
