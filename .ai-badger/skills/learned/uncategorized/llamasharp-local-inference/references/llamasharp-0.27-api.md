# LLamaSharp 0.27 — API surface, download, and the A/B/C measurement

## API surface (verified against LLamaSharp.xml, 0.27.0)

Discovered by grepping `~/.nuget/packages/llamasharp/0.27.0/lib/net8.0/LLamaSharp.xml` — do the same
when the version changes; don't guess the API.

- `LLamaWeights.LoadFromFile(IModelParams)` → `LLamaWeights` (implements `IDisposable`).
- `StatelessExecutor(LLamaWeights, IContextParams, ILogger)` — constructor. `ModelParams` implements
  both `IModelParams` and `IContextParams`, so pass `modelParams` for both.
- `StatefulExecutorBase.InferAsync(string prompt, IInferenceParams, CancellationToken)` →
  `IAsyncEnumerable<string>` (tokens). `StatelessExecutor` inherits it.
- `InferenceParams`: `MaxTokens`, `AntiPrompts` (List<string>), `SamplingPipeline`, `TokensKeep`,
  `OverflowStrategy`, `ContextTruncationPercentage`. **No `Temperature` property.**
- `DefaultSamplingPipeline`: `Temperature`, `TopP`, `TopK`, `Grammar`, `Seed`, etc. `GreedySamplingPipeline`
  also exists.
- `ModelParams`: `ContextSize`, `GpuLayerCount`, `Threads`, `BatchSize`, etc.

## Download (Qwen2.5-0.5B-Instruct)

- URL: `https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf`
- Size: 469 MB (491,400,032 bytes). q4_0 is ~350 MB; q4_k_m is the better quality/size default.
- SHA-256: `74a4da8c9fdbcd15bd1f6d01d621410d31c6fc00986f5eb687824e7b93d7a9db`
- Repo convention: models go in `~/.ai-raccoon/models/` (same dir as the embedding GGUF).
- HF `curl -sI` on the resolve URL returns a 302 redirect page (~1 KB), not the file — follow with
  `-L` to read the real `content-length`.

## The A/B/C measurement (2026-08-13, 55-row promotion fixture)

Fixture: `/tmp/promotion-fixture.json` built by
`docs/work/promotion-scoring-eval/rebuild_fixture.py reference-labels.json ~/.ai-raccoon/memory.db out.json`
(61 labels joined to the live bank by hash → 55 survive). usefulness 0–4, signal = ≥2 (16 rows).

Judged each row with the rubric's 0–4 scale as a single-digit answer (greedy sampling):

| Signal | Spearman vs usefulness | best F1 (signal ≥2) |
|---|---:|---:|
| A — mechanical PromotionScorer | **+0.397** | **0.596** |
| B — Qwen2.5-0.5B judge | +0.132 | 0.457 |
| C — combined (mean A,B) | +0.084 | 0.457 |

Qualitative: Qwen output "4" for ~half the rows including obvious noise (u=0, scorer 0.43–0.50) and
"0" for genuine durable rows (u=3, scorer 3.56). All 55 outputs parsed a digit.

Note: the scorer's +0.397 here is below ADR-0018's +0.70 because this is the small, noise-skewed
promotion subset and labels are joined to live-bank content that has drifted since labeling — the
A/B/C comparison is unaffected (all three see the same rows).

## Throwaway-harness pattern

For a one-off "measure then decide" evaluation, add a temp `[Fact]` to the test project (it has
`InternalsVisibleTo` to `PromotionScorer`), add LLamaSharp to `tests/*.csproj` +
`tests/Directory.Packages.props`, run `dotnet test --filter <Name>`, write results to
`/tmp/*.txt`, then delete the test and revert the package refs. This mirrors the earlier
`TempThresholdCalibration` approach.
