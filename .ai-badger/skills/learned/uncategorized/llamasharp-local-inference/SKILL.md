---
name: llamasharp-local-inference
description: Use when running local GGUF models via LLamaSharp here.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos]
metadata:
  hermes:
    tags: [llamasharp, gguf, local-llm, dotnet, evaluation]
    related_skills: [dotnext-library]
---

# LLamaSharp local inference (this repo)

## When to Use

- Running a local GGUF model **in-process for text generation** (as a judge, classifier, or grader) —
  the embedding path (`LLamaEmbedder`) is already covered by the benchmarks.
- Any "measure whether a local/semantic model adds value over the mechanical scorer" evaluation.
- Not for embedding (use `LocalGgufEmbedder`), and not for anything needing a >1B model to be useful
  (see the measured result below).

Run a local GGUF model in-process for text generation or as a judge. The repo already uses
LLamaSharp 0.27 for **embeddings** (`benchmarks/AiRaccoon.Benchmarks/Embedders/LocalGgufEmbedder.cs`
via `LLamaEmbedder.GetEmbeddings`); the **text-generation** path is a different API and this is it.

## Working API (LLamaSharp 0.27)

```csharp
using LLama; using LLama.Common; using LLama.Sampling;

var modelParams = new ModelParams(path) { ContextSize = 4096, GpuLayerCount = 0 };
using var weights = LLamaWeights.LoadFromFile(modelParams);
var executor = new StatelessExecutor(weights, modelParams, NullLogger.Instance); // NOT IDisposable
var inferenceParams = new InferenceParams
{
    MaxTokens = 16,
    AntiPrompts = ["<|im_end|>"],
    SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0f }, // temperature lives HERE, not on InferenceParams
};
var sb = new StringBuilder();
await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct)) sb.Append(token);
```

## Pitfalls

- **`StatelessExecutor` does NOT implement `IDisposable`** — `using var executor = …` fails to
  compile (CS1674). `using var weights` is fine; there is no `executor.Dispose()`.
- **Temperature is on `DefaultSamplingPipeline`, not `InferenceParams`.** Deterministic/greedy =
  `new DefaultSamplingPipeline { Temperature = 0f }`.
- **`InferAsync` returns `IAsyncEnumerable<string>`** (tokens) — join them into a string; it does not
  return a single string.
- **Central-package-management trap:** LLamaSharp versions live in the ROOT `Directory.Packages.props`,
  but the test project resolves `tests/Directory.Packages.props` (a separate "Testing" scope that
  wins for that subtree). Add `<PackageVersion Include="LLamaSharp" Version="0.27.0"/>` and
  `LLamaSharp.Backend.Cpu` to `tests/Directory.Packages.props` too, or the test build fails NU1010.
- **xunit v3:** `ITestOutputHelper` is in namespace `Xunit` (there is no `Xunit.Abstractions`); the
  xUnit1051 analyzer flags any token-taking call unless you pass `TestContext.Current.CancellationToken`.

## Qwen2.5 chat template (ChatML)

```
<|im_start|>system
<system prompt><|im_end|>
<|im_start|>user
<content><|im_end|>
<|im_start|>assistant
```

Ask for a single digit and parse the first `0-4` in the output; with `AntiPrompts=["<|im_end|>"]` and
`MaxTokens≈16` the output stays short. The GGUF's tokenizer recognises the `<|im_start|>`/`<|im_end|>`
special tokens, so the raw string prompt works — no separate tokenizer call needed.

## Measured result — a sub-1B model cannot judge portability

Qwen2.5-0.5B-Instruct (q4_k_m) scored Spearman **+0.13** against the 55-row labeled promotion
fixture, vs the mechanical `PromotionScorer`'s **+0.40**; fusing it *lowered* the scorer to +0.08.
It rates obvious noise as "4" and durable facts as "0". The promotion classifier was removed on this
evidence (scorer-only restored). Do not re-attempt a sub-1B local model as a promotion/quality judge —
if semantic judgement is ever needed again it must be a substantially larger model, which conflicts
with the project's no-local-LLM-out-of-box stance.

Full API surface, the download (URL/SHA/size), and the A/B/C measurement are in
`references/llamasharp-0.27-api.md`.
