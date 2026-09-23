# Research: what makes up the AiRaccoon server's memory footprint

**Date:** 2026-09-23
**Question:** What contributes to the 6.8 GB memory footprint of the running `ai-raccoon serve` process (PID 80534, 1.44.0), and in what proportions?

```chart:bars
title: live server footprint by owner (MB, vmmap, PID 80534)
native malloc (ONNX Runtime): 6000
managed GC heap, live: 213
code + read-only libs, resident: 254
```

```chart:bars
title: harness peak footprint by batch shape, SFR-400M, arena on (MB, one run each)
load only: 2564
1x510: 2776
8x510: 4388
32x128: 3252
32x510 (first run): 5948
32x510 (second run): 9492
```

## Findings

### F1 — Native `malloc` memory accounts for about 6.0 of the 6.8 GB footprint, and almost all of it is swapped out [MEASURED]

The process footprint is 6.8 GB (peak 7.4 GB), but only 537 MB of it is resident. The default malloc zone holds 6.0 GB allocated across 636k allocations, with MALLOC_LARGE at 5.3 GB virtual and 5.1 GB swapped. Activity Monitor's large figure is this footprint. The small figure is resident memory. The cost is swap, not RAM: the system reported 2.96 M pageouts.

**Evidence:** `vmmap --summary 80534` and `memory_pressure` on a Mac16,12 with 24 GB RAM, macOS 26.6.2, server up 1 h 18 m, 2026-09-23 15:58 CEST. Single snapshot.

### F2 — The managed .NET heap is small: 213 MB live, 1.45 GB committed [MEASURED]

At the last GC, gen2 held 159 MB, LOH 39 MB, gen1 15 MB and POH 0.3 MB. Fragmentation was under 0.5 MB. Managed code is not the problem. The committed-but-unused GC space is virtual and largely swapped.

**Evidence:** `dotnet-counters collect -p 80534 --counters System.Runtime --duration 00:00:08 --format json`, same machine and session as F1.

### F3 — Both engines point at the fp32 SFR-Embedding-Code-400M model (1.75 GB on disk) and share one ONNX session [READ]

`settings model show` lists the same model and engine fingerprint for `model` and `codeModel`. Engines are cached by `(provider, model, baseUrl)`, so memory and code resolve to a single `InferenceSession`. There is one copy of the weights, not two.

**Evidence:** `ai-raccoon settings model show` output; `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:95-103` (`_engines.GetOrAdd(EngineFingerprint(...))`); `ls -la ~/.ai-raccoon/models/Salesforce__SFR-Embedding-Code-400M_R/model.onnx` = 1,745,593,164 bytes.

### F4 — Loading the model alone costs about 2.6 GB [MEASURED]

In a standalone harness using the same ORT version (1.30.0) and 5 intra-op threads, the footprint goes from 19 MB to 2,564–2,743 MB after `new InferenceSession`. That is about 1.5× the file size. This is the floor for any process that holds this model.

**Evidence:** scratch harness `ortmem.cs` (file-based `dotnet run`, `#:package Microsoft.ML.OnnxRuntime@1.30.0`), footprint read from `proc_pid_rusage(RUSAGE_INFO_V4).ri_phys_footprint`. Seven fresh-process runs, range 2,564–2,743 MB. Same machine, with the live server running alongside.

### F5 — One inference batch at the server's shape (32 × 510 tokens) raises the footprint to 6–10 GB, and the footprint stays there [MEASURED]

With arena on (the server's configuration), the footprint was 5,948 MB after the first 32×510 run and 9,492 MB after the second. With the CPU arena off, peak was 10,114 MB and 5,786 MB stayed held afterwards. With arena and memory pattern both off, 6,727 MB stayed held. Turning the arena off does not give the memory back, because macOS malloc keeps the freed large blocks inside the process. The size of the batch is what matters.

**Evidence:** `dotnet run ortmem.cs -- <model> {1 1 | 0 1 | 0 0} 32x128,32x510,32x510`, one fresh process per configuration, one run each. Same machine as F4.

### F6 — Batch size drives the footprint, and batch 32 is no faster per item than batch 1 [MEASURED]

| batch × tokens | footprint after the run | time per run | time per item |
|---|---|---|---|
| 1 × 510 | 2,776 MB (+34 over load) | 440–480 ms | ~0.46 s |
| 8 × 510 | 4,388 MB (+1.6 GB) | 3.9–4.1 s | ~0.50 s |
| 32 × 510 | 5,948–9,492 MB (+3.4–6.9 GB) | 16–22 s | ~0.5–0.7 s |

On CPU, a 400M model's attention is already compute-bound at batch 1, so a larger batch adds activation memory without adding throughput. The arena-off 8×510 run matched arena on (4,408 MB).

**Evidence:** `ortmem.cs` runs with shapes `8x510` ×4, `1x510` ×3 and `32x510` ×2 (F5). Same machine and conditions.

### F7 — The server embeds in batches of 32, and chunks for this model are capped at 510 content tokens [READ]

`EntryEmbedder.BatchSize = 32` and `CodeEmbedder.BatchSize = 32`. The manifest chunk budget is `min(512 − 2, window − 2)` = 510. Every drain or code-reindex batch of full-size chunks is therefore the 32×510 shape measured in F5.

**Evidence:** `src/AiRaccoon.Infrastructure/Embedding/EntryEmbedder.cs:30`, `src/AiRaccoon.Infrastructure/Embedding/CodeEmbedder.cs:22`, `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:49,400-415`.

### F8 — The live server's 6.0 GB of native memory is the 2.6 GB of weights plus a retained 32-row inference peak [INFERRED]

This reasons from F1 (6.0 GB native, peak 7.4 GB), F4 (2.6 GB load floor), F5 (5.9–9.5 GB after 32×510 batches) and F7 (the server runs exactly that shape). The live numbers fall inside the harness range. No other native consumer comes close: the SQLite page cache shows 384 KB in vmmap, and the managed heap is F2.

### F9 — The ONNX session accepts up to 8,190 tokens per item, so a row longer than 510 would blow up the peak further [INFERRED]

`OnnxEmbeddingGenerator` truncates to `descriptor.ContextWindowTokens` (8,190 in this manifest), not to the 510 chunk budget. Attention memory grows with sequence length squared: a 2,048-token item in a batch of 8 would need about 2 GB for a single attention score tensor (8 × 16 heads × 2048² × 4 B). The log shows no event-414 truncations against the 8,190 window. That event fires only above 8,190, though, so rows between 511 and 8,190 tokens would pass silently. Reasoned from `OnnxEmbeddingGenerator.cs:53,131,288` and the manifest's `contextWindowTokens: 8190`, plus the model config (24 layers, 16 heads, hidden size 1024). Not measured.

### F10 — Two knobs would each cut the footprint by gigabytes: a smaller embed batch, and an int8-quantized model [INFERRED]

A batch of 1–8 bounds the inference peak to +0.03–1.6 GB (F6) with no throughput loss. int8 quantization would shrink the 2.6 GB weight floor to roughly a quarter, by analogy with the bundled int8 MiniLM (a 23 MB model versus its ~90 MB fp32 original). That quantization figure is reasoned, not measured on SFR-400M, and the vectors would change, so it would need a re-embed.

### F11 — On the bundled int8 MiniLM, one row per run is just as fast and far smaller [MEASURED]

Three runs at 32×254 took 324–434 ms each (about 10 ms per item) and left a 616 MB footprint. Thirty-two runs at 1×254 took 9–12 ms each and left 84 MB. The default install loses no throughput from single-row runs either.

**Evidence:** `dotnet run ortmem.cs -- <store>/ai-raccoon/1.44.1/.../Models/model_qint8_arm64.onnx 1 1 <shapes>`, one fresh process per shape, same machine as F4.

### F12 — Acted on: `OnnxEmbeddingGenerator` now runs one row per session call [READ]

The DB-side `BatchSize` (32) stays as it is: it sets the SQL page size, the transaction scope and the remote OpenAI request size, none of which drive native memory. Only the ONNX `session.Run` shape changed, which is where F5 locates the peak. `OnnxEmbeddingRowIsolationTests` pins the behaviour: a row embeds bit-identically whether it is alone or batched. It was red before the change.

**Evidence:** `src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs` (`RunEachRow`), `tests/AiRaccoon.Tests/Integration/Embedding/OnnxEmbeddingRowIsolationTests.cs`.

## Still open

- The live server's footprint after a drain on the fixed build. It should settle near the 2.6 GB load floor (F4, F6) but has not been observed.
- Whether a real drain batch ever carries more than 510 tokens (F9). This would be settled by logging the `maxLen` of each `RunBatch`, or by a query over stored chunk token counts.
- The int8 SFR-400M footprint and recall (F10). This needs a quantized export plus the existing embedding benchmark.
- Whether `ArenaExtendStrategy = kSameAsRequested`, or releasing the arena via a run option such as `memory.enable_memory_arena_shrinkage`, returns memory on macOS. F5 suggests the malloc zone keeps freed blocks either way. Not tested.
- Each harness configuration ran once in a fresh process. Repeat runs within a process were consistent, but cross-process spread is known only for the load step (F4).
- Heap attribution inside the live process was not taken (no `heap`/`malloc_history` with MallocStackLogging). F8 attributes the native memory by the harness match, not by stack.
