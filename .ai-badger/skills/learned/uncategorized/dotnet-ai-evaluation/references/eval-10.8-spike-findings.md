# S1 spike findings — Microsoft.Extensions.AI.Evaluation 10.8.0 vs LM Studio local judges

Spike date: 2026-08-03 (llm-validation-part-2 task, WP4 leg). Scratch harness in
`/tmp/llm-validation-part-2-spike/` (net10.0 console + xunit.v3 tests, packages:
Evaluation/Quality/Reporting 10.8.0, AI.OpenAI 10.8.3, OpenAI 2.12.0, xunit.v3 3.2.2 +
xunit.runner.visualstudio 3.1.5 — 3.2.2 does not exist for the runner).

## Verdict agreement method (validated)

12 frozen scenarios, one evaluator each (6 dimensions x pass/fail). Known-good 5/0 on the
same 0-5 NumericMetric scale. Rule: pass correct iff judge >= 4; fail correct iff judge
<= 2; score 3 = ambiguous (counted separately, never correct). Recommendable bar: >= 80%
(10/12). Result: gemma-4-e4b 10/12 correct, 0 wrong, 2 ambiguous (83.3%) → recommendable.

## HTTP wire evidence (from logging handler capturing request + response bodies)

- All six evaluators send `"response_format":{"type":"text"}`, temperature 0, and
  `max_completion_tokens: 800` (the library's own cap; the spike raised it to 2000 via a
  decorating IChatClient because small judges truncate mid-`<S0>` chain → finish_reason
  "length" → `<S2>` tag never emitted → `NumericMetric.Value == null`).
- Tag protocol request tail (Relevance/Groundedness/Completeness/Coherence/Fluency):
  `## Please provide your answers between the tags: <S0>your chain of thoughts</S0>,
  <S1>your explanation</S1>, <S2>your Score</S2>.# Output`
- Equivalence protocol: system prompt "return a single integer value between 1 to 5 ...
  no other text"; user prompt has few-shot `question/correct answer/predicted answer/
  stars: N` lines ending in `stars:` — the model completes with a bare integer.
- Parsed reply example (gemma-4-e4b): content contains
  `...<S0>...reasoning...</S0>\n<S1>...explanation...</S1>\n<S2>5</S2>`.

## 10.8 API shapes (verified by reflection)

- `ChatConfiguration`: only property `IChatClient ChatClient`; ctor `(IChatClient)`.
- `EvaluationContext`: abstract; protected ctors `(string, IEnumerable<AIContent>)`,
  `(string, AIContent[])`, `(string, string)`; properties `Name`, `Contents`.
- `EvaluationResult`: `IDictionary<string, EvaluationMetric> Metrics`; ctors from dict /
  enumerable / array; `TryGet<T>(string, out T)`, `Get<T>(string)`.
- `EvaluationMetric` base: Name, Reason, Interpretation, Context (dict), Diagnostics,
  Metadata. `NumericMetric.Value` is `double?` (from `EvaluationMetric<T>`).
- `IEvaluator.EvaluateAsync(IEnumerable<ChatMessage>, ChatResponse, ChatConfiguration,
  IEnumerable<EvaluationContext>, CancellationToken)`; `EvaluatorExtensions` provide
  simpler overloads, incl. `(IEvaluator, string userRequest, string modelResponse,
  ChatConfiguration, IEnumerable<EvaluationContext>, CancellationToken)`.
- `EvaluatorExtensions.EvaluationMetricNames` → e.g. ["Relevance"].
- `OpenAIClientExtensions.AsIChatClient` overloads in 10.8.3: `(ChatClient)`,
  `(ResponsesClient, string defaultModelId)`, `(AssistantClient, ...)`. The plain
  `AsIChatClient(string model)` on OpenAIClient is GONE.
- `OpenAIClientOptions`: Endpoint, OrganizationId, ProjectId, Transport, NetworkTimeout
  (no ApiKey, no HttpClient). `OpenAIClient` ctors: (string apiKey), (ApiKeyCredential),
  (ApiKeyCredential, options), (AuthenticationPolicy[, options]).
- `HttpClientPipelineTransport(HttpClient)` ctor exists in System.ClientModel.

## LM Studio routing trap (critical integrity check)

- Run A (gemma-4-e4b): all 12 requests ask gemma, all 12 responses report
  `model: google/gemma-4-e4b`. VALID.
- Run B (intended bonsai-27b): all 12 requests ask prism-ml/bonsai-27b, but ALL 12
  responses report `model: google/gemma-4-e4b` and are byte-identical to Run A. INVALID —
  bonsai had been removed from the inventory mid-session (a qwen/qwen3.6-27b model
  appeared in its place; load attempt returned `model_not_found`). LM Studio's
  OpenAI-compat endpoint silently serves the loaded model when the requested id is
  missing. Lesson: capture and check the response `model` field per leg; discard legs
  where request model != response model.

## Performance notes (Apple M4, 24 GB)

- gemma-4-e4b: ~54 s/scenario (12 in 11 min), 0 errors, clean tag output.
- gemma-4-12b-qat @8192 ctx: loaded OK after unloading other models (~6-13 s); defaults
  reasoning ON → `reasoning_content` populated, final tags still in `content` when it
  finishes; ~190 s/scenario, 2/6 timeouts at 280 s and 1 HTTP 400 → only 3/6 scored,
  inconclusive as judge. Needs the UI "Reasoning Disabled" preset and a generous per-
  scenario timeout if used.
- MaxOutputTokens 2000 + HttpClient.Timeout 280 s both required for the 12B leg.

## Restore-state lesson

Machine state drifted mid-session (user actively using LM Studio). Always snapshot
`/api/v1/models` loaded_instances at start AND before each leg, and re-verify at the end.
"Restore bonsai + gemma" was impossible because bonsai no longer existed in the
downloaded-models inventory — report the restore as partially achieved with the blocker
named, not silently.