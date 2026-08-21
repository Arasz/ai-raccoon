# LLamaSharp 0.27 text generation (local instruct model)

Verified 2026-08 on net10, ai-raccoon. Running a local GGUF *instruct* model in-process for judging/classification — no llama.cpp server, no Ollama, no download-on-run (the GGUF is on disk).

## API surface (LLamaSharp 0.27.0 + LLamaSharp.Backend.Cpu)

```csharp
using LLama;
using LLama.Common;
using LLama.Sampling;

var modelParams = new ModelParams(path)
{
    ContextSize = 4096,
    GpuLayerCount = 0,          // CPU
};
using var weights = LLamaWeights.LoadFromFile(modelParams);   // sync load
var executor = new StatelessExecutor(weights, modelParams, NullLogger.Instance); // NOT IDisposable
var inferenceParams = new InferenceParams
{
    MaxTokens = 16,
    AntiPrompts = ["<|im_end|>"],
    SamplingPipeline = new DefaultSamplingPipeline { Temperature = 0f },
};
var sb = new StringBuilder();
await foreach (var token in executor.InferAsync(prompt, inferenceParams, ct))  // IAsyncEnumerable<string>
    sb.Append(token);
var raw = sb.ToString();
```

Key facts probed from `~/.nuget/packages/llamasharp/0.27.0/lib/net8.0/LLamaSharp.xml`:

- `StatelessExecutor(LLamaWeights, IContextParams, ILogger)` — `ModelParams` satisfies both
  `IModelParams` and `IContextParams`, so pass the same instance to `LoadFromFile` and the ctor.
- `StatelessExecutor` does NOT implement `IDisposable` — `using var executor = …` fails CS1674. Only `LLamaWeights` is disposable.
- `InferAsync(string, IInferenceParams, CancellationToken)` → `IAsyncEnumerable<string>` (tokens).
- `InferenceParams` has NO `Temperature` property — temperature is on `SamplingPipeline`
  (`DefaultSamplingPipeline.Temperature`; `GreedySamplingPipeline` is argmax).

## Qwen2.5-Instruct prompt (ChatML)

```csharp
string BuildPrompt(string entry) =>
    "<|im_start|>system\n" + instruction + "<|im_end|>\n" +
    "<|im_start|>user\n" + entry + "<|im_end|>\n" +
    "<|im_start|>assistant\n";
```

`<|im_start|>`/`<|im_end|>` are special tokens in the GGUF tokenizer. `AntiPrompts=["<|im_end|>"]`
stops generation at the end of the assistant turn. For a deterministic grade, put the rubric in the system message, end with "Reply with a single digit 0-4 and nothing else", Temperature 0, MaxTokens 16, then parse the first digit in
`[0-4]`.

## Download (Qwen2.5-0.5B-Instruct)

- q4_k_m: `https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf`
  (491,400,032 bytes = 469 MB; SHA-256 `74a4da8c9fdbcd15bd1f6d01d621410d31c6fc00986f5eb687824e7b93d7a9db`).
- HF `resolve/main` URLs 302-redirect to a CDN; `curl -sI` returns the ~1 KB redirect page, not the file — add `-L` to follow and read the real `Content-Length`. The HF API `siblings[].size` may be 0; use the `-L` HEAD for the true size.

## A/B/C decision pattern (measure before shipping a local LLM as a judge)

Run all three on the SAME labeled set and compare rank correlation:
- **A** = incumbent/baseline (e.g. the tuned heuristic scorer).
- **B** = the model alone.
- **C** = combined (e.g. mean of A and B).

Keep the model only if B or C beats A. Result from ai-raccoon (2026-08): Qwen2.5-0.5B judging promotion portability (0-4) scored Spearman +0.13 vs the mechanical `PromotionScorer`'s +0.40, and fusing them *degraded* to +0.08 (it rated u=0
noise as 4 and u=3 durable rows as 0). The feature was removed. A 0.5B model cannot follow a nuanced multi-class rubric — small models are a pre-filter, not a judge.

## Harness mechanics

- Run the measurement in a throwaway xunit test in the test project (InternalsVisibleTo gives access to `internal` scorer types). Add `LLamaSharp` + `LLamaSharp.Backend.Cpu` to BOTH the test csproj and `tests/Directory.Packages.props` — the
  tests dir has its OWN props that overrides the root (NU1010 otherwise; see the SKILL's CPM pitfall).
- xunit v3: `ITestOutputHelper` is in the `Xunit` namespace, not `Xunit.Abstractions`.
- Write results to `/tmp/<name>.txt` from the test (Console output is captured, but a file is reliable to read back).
- Delete the harness and revert the package refs after measuring — the harness is a spike, not a test.
