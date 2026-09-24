# 0108 — One bundled engine for memory and code: granite-embedding-small-english-r2 (fp16), GPU first

Date: 2026-09-23

Status: Accepted

## Context

AiRaccoon shipped two engines. Memory used the bundled int8 all-MiniLM-L6-v2 (23 MB, inside the
tool package). Code had no bundled engine at all: `ai-raccoon model code set default` downloaded
`faxenoff/code-daemon-embed-v1` (179 MB) from Hugging Face.

The embedding survey (`docs/work/2026-09-23-embedding-model-survey.md`) and the follow-up
measurements in this ADR's session scored both against the candidates the tokenizer.json work
(1.46.0) made loadable. Four evals were used: code (summary → method, 482 pairs from this
repository), code across 12 languages (#673's corpus, 72 queries, 1,775 chunks), the 174-doc
memory corpus, and a title → document eval built from a copy of a live bank (150 documents,
1,037 chunks).

| engine | size | code (this repo) MRR | code (12 languages) MRR@10 | memory nDCG@10 | bank nDCG@10 |
|---|---|---|---|---|---|
| all-MiniLM-L6-v2 int8 (memory default) | 23 MB | 0.484 | 0.643 | 0.605 | 0.347 |
| code-daemon-embed-v1 (code default) | 179 MB | 0.324 | 0.391 | 0.573 | – |
| granite-embedding-small-english-r2 fp16 | 97 MB | – | 0.788 | 0.632¹ | 0.377 |
| granite-embedding-small-english-r2 int8 | 50 MB | 0.630 | 0.791 | 0.632 | 0.374 |

¹ fp16 and fp32 vectors agree at cosine 1.00000 on CPU; the fp32 export scored 0.636.

Through the product itself (#673's harness, 332 queries over 12 languages, `run_code_eval.py`), the
code corpus scored nDCG@5 0.501, hit@1 0.446 and hit@5 0.581 on code-daemon-embed-v1, and 0.603,
0.554 and 0.660 on the bundled fp16 granite. Its drain took 44 s against 120 s. Both score 0 on
CSS, HTML and SQL, which have no chunker yet.

granite-small beats both defaults on every eval, and it is Apache-2.0. One engine can serve both
corpora, and one ONNX session serves both because the engine cache is keyed by fingerprint.

Three constraints decided which of its files ships.

**The CPU is shared; the GPU mostly is not.** An embed drain competes with everything else on
the machine. Measured on an Apple M4 (ORT 1.30.0, granite-small, one row per run), the WebGPU
execution provider costs 5-10× less process CPU per embed than the CPU provider at the same or
better latency. At 128 tokens, fp32 took 61-64 ms of CPU on the CPU provider against 4.6-6.9 ms
on WebGPU. At 512 tokens it took 253-261 ms against 52-58 ms. fp16 on WebGPU ran at 9 ms and
29 ms latency, against 12 ms and 50 ms for fp32 on CPU.

**An int8 graph cannot move to the GPU without changing its vectors.** The int8 export on WebGPU
or CoreML reproduced its own CPU vectors at cosine 0.944-0.966 only. ADR-0049 is the same
phenomenon across CPUs: u8s8 integer matmuls take different arithmetic paths on NEON, AVX2 and
VNNI and give different answers. fp16 on WebGPU reproduced fp16 on CPU at cosine 0.9998, and
fp32 at 1.000000.

**The package has a ceiling.** nuget.org rejects packages over about 250 MB. The osx-arm64 RID
package was 65 MB compressed with MiniLM inside it. The fp32 export (186 MB) would put it close to
the ceiling, and fp16 (97 MB) does not.

## Decision

1. **The bundled engine is granite-embedding-small-english-r2, fp16 export**
   (`onnx-community/granite-embedding-small-english-r2-ONNX`, `onnx/model_fp16.onnx` plus its
   `model_fp16.onnx_data`, and `tokenizer.json`), pinned by SHA-256. It replaces all-MiniLM-L6-v2
   int8 for memory. 384 dimensions, the same as before, so vec0 tables keep their shape. It pools
   through the graph's own `sentence_embedding` output, uses the tokenizer-json family and has no
   prompts.
2. **It is also the code corpus's default engine.** `model code set default` activates the
   bundled engine instead of downloading code-daemon-embed-v1. The code engine stays separately
   configurable, and `model code set local <dir>` still takes any manifest model.
3. **The bundled engine's fingerprint changes, and memory re-embeds on its own.** The fingerprint
   is `local:bundled#<sha256 of the bundled manifest>`: stable across installs, changed exactly
   when the bundled model changes. The model-migration job now reconciles it on every pass, as the
   code-reindex job already did for code, so a bank embedded by MiniLM migrates on first start with
   no command (ADR-0076's drain). A code corpus on code-daemon-embed-v1 keeps it until
   `model code set default` is run, since that engine is a path, not the bundled one. That is
   accepted: one engine, one re-embed.
4. **Sessions try the GPU first.** Where the platform's ONNX Runtime build implements a GPU
   execution provider, the session appends it and ONNX Runtime keeps what the GPU cannot run on
   the CPU. The 1.30.0 osx-arm64 build implements WebGPU. WebGPU sessions share one process-wide
   GPU context that concurrent runs corrupt (a segfault inside the provider), so GPU runs are
   serialized process-wide; the GPU executes one graph at a time anyway. Where appending fails, or the build has
   none, the session runs on the CPU and logs which provider it got. A setting forces the CPU for
   machines where the GPU misbehaves.
5. **The bundled manifest carries three per-engine values.** `chunkTokens: 254` keeps memory
   chunks the size every measurement in this ADR used, instead of the min(510, window − 2) its
   8,190-token window would give. Smaller chunks also cost less per embed. `relevanceFloor: 0.79`
   replaces search's 0.35 cosine floor, which was calibrated on MiniLM: granite scores off-topic
   text at up to 0.787 (median 0.735) and relevant text at 0.802 and above (p5), where MiniLM's
   off-topic maximum was 0.286. Structure fusion (ADR-0004) first rescales similarities so that
   floor maps to 0. ADR-0004's "a chunk with no heading scores structure 0" was measured where 0 was
   roughly what unrelated text scored. Without the rescale, a heading-less note (content cosine
   0.959) ranked fifth behind unrelated headed chunks (0.757). Engines that declare no floor,
   MiniLM included, fuse exactly as before.
6. **`embedding.device`** (`settings model device auto|gpu|cpu`). `auto`, the default, puts only
   the bundled engine on the GPU. A downloaded int8 model would drift from its stored CPU vectors,
   so it opts in with `gpu`.
7. **The weights are fetched at build time, not committed.** `scripts/src/bundle.py` pins the
   files and `scripts/download-embedding-model.py` downloads and verifies them into
   `src/AiRaccoon/Models/` before `dotnet pack`, as it already did for the ONNX model. The 97 MB
   `.onnx_data` file is git-ignored.

## Consequences

- One model file for both corpora. The package loses MiniLM (23 MB) and the code tokenizer
  (0.6 MB) and gains about 100 MB.
- First start after upgrading re-embeds every bank. ADR-0076 measured roughly 6 minutes of
  refused tool calls on a 25,917-entry bank with the old engine. The new engine is heavier per
  row on the CPU and lighter on the GPU. README's Breaking changes names the cost.
- The bundled engine's memory chunk budget stays 254 tokens (`chunkTokens`). The code corpus keeps
  its own 510-token chunks, and activation now checks the engine's window, not its memory budget.
- The code corpus switches with `model code set default`. A corpus left on code-daemon-embed-v1
  keeps that engine until the command is run.
- Windows and Linux run on the CPU with the standard package. There, fp16 costs roughly twice
  fp32's CPU time, because ONNX Runtime upcasts it. A DirectML (Windows) or CUDA (Linux) build is
  the follow-up that brings the GPU path to those hosts.
- Committed fixtures that bake MiniLM vectors (ADR-0049/0050) keep them: the one gate that embedded
  its query live now points that bank at the MiniLM test asset, which moved to
  `tests/AiRaccoon.Tests/TestData/Models/`. Engine-mechanics tests (golden vectors, legacy
  single-file path) use it too.

## Amendment (2026-09-24): rows the floor clamps to 0 order by content similarity

The rescale in decision 5 maps every similarity under the floor to exactly 0, so on a query that
matches nothing, every vector candidate tied at 0 and fell back to the ordinal-hash tie-break. A
row's hash covers its source path, so the same bank ranked those rows differently under another
directory. `MemorySearchRankingTests.Search_AllTermsKeywordMatchThatWinsFusion_StaysFirstAboveBoostedNeighbours`
went red about once in 27 CI runs for that reason: its temp directory is random, and a third of
the resulting orders let consolidation fold both neighbours into the middle chunk.
`StructureFusion.Rank` now breaks score ties by raw content similarity, then by hash. Scores are
unchanged. Only the order of tied rows moves, and it now depends on the text, not the path.

## Alternatives rejected

- **granite-small int8 as the bundled file** (50 MB, the smallest). It cannot use the GPU without
  changing its vectors (cosine 0.94-0.97), and it inherits ADR-0049's host-dependent arithmetic.
  It is not cheaper than fp32 on this CPU either: 87 s against 77 s for the same 1,775 chunks.
- **granite-small fp32.** Exact on the GPU, but 186 MB would put the RID package near the nuget.org
  ceiling, and it needs twice the GPU memory.
- **EmbeddingGemma** (best on memory text). 295 MB int8 alone is over the package ceiling, and
  bundling its weights would make AiRaccoon a distributor under Gemma Terms §3.1. It stays an
  opt-in `model download`.
- **granite-embedding-english-r2** (best on the 12-language code eval, 0.838). 577 MB fp32 and
  161 MB int8. Too large to bundle as fp16/fp32, and int8 is ruled out above.
- **Keeping MiniLM for memory and bundling granite only for code.** Two models in the package,
  two vector spaces, and MiniLM loses on every eval.
- **CoreML instead of WebGPU on macOS.** With dynamic shapes, CoreML took 305 of 400 nodes in 37
  partitions and ran about 3× slower than the CPU. With fixed shapes, it was no faster than the
  CPU and loaded in 2-7 s.
