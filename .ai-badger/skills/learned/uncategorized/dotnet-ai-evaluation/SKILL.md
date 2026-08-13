---
name: dotnet-ai-evaluation
description: >-
  Use when evaluating LLM response quality in .NET.
---

# .NET LLM-response evaluation (Microsoft.Extensions.AI.Evaluation)

LLM-as-judge evaluation of chat completions in .NET: the Evaluation family
(Quality/Reporting/Console 10.8.x, judge client via `Microsoft.Extensions.AI.OpenAI`
→ `OpenAI` 2.12.0) scores a model's response against evaluator prompts. Verified package/TFM
facts and the 14-call-site mapping live in the repo's research note
`docs/work/research/2026-08-03-llm-evaluation-microsoft-extensions-ai.md` (ADR-0088).

## Judge backends

| Judge | Adapter | Notes |
|---|---|---|
| OpenAI / OpenRouter | `Microsoft.Extensions.AI.OpenAI` with endpoint/base-address override | Verified [RAN]; works with any OpenAI-compatible server |
| LM Studio (local) | Same OpenAI-compatible adapter, `Endpoint = http://localhost:1234/v1`, placeholder key | Works for dev/on-demand runs — see reference |
| Anthropic | `Anthropic` SDK's own `IChatClient` (repo pins 12.39.0) | `Microsoft.Extensions.AI.Anthropic` does not exist on NuGet |
| Google Gemini | community adapter only | not needed for v1 |

## Architectural constraint: local judge ≠ CI judge

A localhost LM Studio judge cannot serve the nightly eval job (GitHub Actions runner).
Local = dev-time/on-demand eval on the developer machine; CI keeps the cloud
`EVAL_JUDGE_API_KEY`. Any plan for "local judge" must make this fork explicit.

## LM Studio as judge — verified probes (2026-08-03, v0.4.20)

- Server: OpenAI-compat `/v1/chat/completions` on `http://localhost:1234`. No auth; any
  placeholder key works. Probe first: `GET /v1/models` (also `/api/v1/models` for
  loaded_instances + capabilities).
- `response_format: json_schema` IS honored by the raw OpenAI-compat API (gemma-4-e4b
  returns clean JSON in `content`) — BUT the 10.8 Evaluation.Quality evaluators themselves
  do NOT send json_schema; they send `response_format: {"type":"text"}` and parse a
  `<S0>/<S1>/<S2>` tag protocol (see "Evaluator runtime wire behavior" below). Don't
  assume a judge's json_schema capability is what the evaluators exercise.
- **Reasoning-model trap (bonsai-27b):** with json_schema, output lands in
  `reasoning_content` and `content` is EMPTY — a standard chat client
  (Microsoft.Extensions.AI.OpenAI) reads `content` and gets nothing. Plain chat (no
  response_format) does produce content after reasoning. The reasoning toggle is
  LOAD-TIME ONLY: per-request `"reasoning": {"effort": "off"}` is ignored, and the
  `/api/v1/models/load` API rejects a reasoning key. **FIX (verified [RAN]): the LM
  Studio UI "Reasoning Disabled" preset** for that model — applied in the app's model
  config, applies on reload, and makes bonsai-27b return clean JSON in `content` with
  empty `reasoning_content`, no per-request param needed. gemma-4-12b-qat ALSO defaults
  to reasoning ON and needs the same preset. So: reasoning-default models ARE usable as
  schema-forced judges — but only after the user flips the UI preset, which is not
  scriptable via REST.
- Load API accepts ONLY: `model`, `context_length`, `eval_batch_size`, `flash_attention`,
  `num_experts`, `offload_kv_cache_to_gpu`, `echo_load_config`. Anything else →
  `unrecognized_keys`. Unload API takes `instance_id` (the model id string), NOT `model`.
- **Memory guardrail is context-sensitive:** gemma-4-12b-qat (Q4_0, 7.2 GB) refused at
  default context with a ~44.87 GB estimate; `context_length: 8192` dropped it to 8.20 GB,
  still refused while bonsai-27b (8.5 GB) + gemma-4-e4b (6.9 GB) were loaded. **It DID
  load (13.3 s) once the other chat models were unloaded first** — loadability =
  f(context_length, what else is loaded). On 24 GB: unload other models (or shrink
  context) before expecting a big model to load.
- Full probe transcript, payloads, and this machine's model inventory:
  `references/lm-studio-local-judge.md`.

## Eval design rules (ADR-0088)

- Evaluator sets by step shape: extraction/research → Relevance+Groundedness+Completeness;
  prose → Coherence+Fluency+Groundedness; meta/judge steps → Completeness+Coherence;
  classifier → Equivalence.
- Fixtures keyed by `StepType` (NOT prompt key — 4 sites diverge), replayed through the
  existing `FakeLlmClient` harness from sanitized `steps[].inputSnapshot` exports.
- Trend-not-gate: `dotnet aieval report` HTML artifact; CI never fails on individual scores.
- No eval in the PR gate (ADR-0016/0053), no USD accounting (ADR-0046), no production code
  change — the eval project is test-only under `tests/`.
- Spike first (S1): scratch project, real end-to-end run against the libraries + a judge,
  BEFORE building `tests/JobSearchAiAssistant.EvalTests`.

## Evaluator runtime wire behavior (10.8.0, spike-verified 2026-08-03)

The S1 spike (scratch .NET harness + local LM Studio judges, 12 frozen known-good
scenarios) verified the ACTUAL HTTP behavior of the Quality evaluators. These facts
differ from what older docs/assumptions suggest — trust them for 10.8.x:

- **No `response_format: json_schema` from evaluators.** Relevance/Groundedness/
  Completeness/Coherence/Fluency send `"response_format":{"type":"text"}` and a prompt
  asking for `<S0>thought</S0> <S1>explanation</S1> <S2>score</S2>` tags; the numeric
  score is parsed out of the `<S2>...</S2>` slot. Equivalence is different: its prompt
  ends with `stars:` and the model must complete with a BARE integer 1–5 (few-shot
  lines end `stars: N`). A fake judge in tests must mimic the protocol (tags for five,
  bare int for Equivalence), not return `{"score":N}`.
- **10.8 API surface (verified by reflection):**
  - `ChatConfiguration` has ONLY `ChatClient` — no `Temperature`. Pin temperature/seed
    by decorating the IChatClient (middleware style) setting `ChatOptions.Temperature = 0`
    and `ChatOptions.Seed`, or via `ChatClientBuilder().ConfigureOptions`.
  - `EvaluationContext` is ABSTRACT with protected ctors `(string name, string content)` /
    `(string, AIContent[])` — derive a concrete subclass for generic evaluators
    (Relevance/Coherence/Fluency). Groundedness/Completeness/Equivalence ship dedicated
    context types (`GroundednessEvaluatorContext(groundingContext)`,
    `CompletenessEvaluatorContext(groundTruth)`, `EquivalenceEvaluatorContext(groundTruth)`).
  - Call path: `EvaluatorExtensions.EvaluateAsync(IEvaluator, string userRequest,
    string modelResponse, ChatConfiguration, IEnumerable<EvaluationContext>, CancellationToken)`
    (or the ChatMessage/ChatResponse overloads). `EvaluationResult.Metrics` is a flat
    `IDictionary<string, EvaluationMetric>` keyed by metric name; `NumericMetric.Value`
    is `double?` (inherited from `EvaluationMetric<T>`).
  - **Client construction (10.8.3):** `OpenAIClient.AsIChatClient(modelId)` no longer
    exists. Use `new OpenAIClient(new ApiKeyCredential("lm-studio"), options)`
    `.GetChatClient(modelId).AsIChatClient()` — ChatClient is the chat-completions path.
    (The ResponsesClient overload exists and would hit `/v1/responses` — assert the path
    in tests.) `OpenAIClientOptions` has no `HttpClient` property; pass custom transport
    via `options.Transport = new HttpClientPipelineTransport(httpClient)`.
- **Token-cap pitfall:** evaluators set `max_completion_tokens: 800`. Small local models
  burn that budget on the `<S0>` thought chain, hit `finish_reason:"length"`, and never
  emit the `<S2>score</S2>` tag → metric key exists but `Value` is null (looks like
  "no metric found"). Fix seen in the spike: raise the cap (~2000) in the decorating
  client. Also raise the HttpClient default timeout (100 s kills slow local judges;
  set `httpClient.Timeout` explicitly — SDK `NetworkTimeout` alone is not enough when a
  custom HttpClient is supplied).
- **LM Studio silent model fallback:** if the requested model id is not loaded (or was
  removed from the inventory mid-session), LM Studio serves the request with WHATEVER is
  loaded and sets the RESPONSE `model` field to the actual serving model — the request
  `model` param is not honored. The spike's "bonsai-27b" leg was silently served 12/12
  by gemma-4-e4b (identical response bytes); only a response-model-field check caught it.
  ALWAYS assert request model == response model per leg, and re-verify the model is still
  installed/loaded before a long run on a machine the user is actively using. **The user
  actively manages the inventory** — bonsai-27b was removed and qwen/qwen3.6-27b (27B
  4-bit, ~14 GB) appeared mid-session; re-check `/api/v1/models` right before a long run.
- gemma-4-12b-qat defaults to reasoning ON (same trap as bonsai) and is ~4x slower as a
  judge (~190 s/scenario); it still emits final tags in `content` when it completes, but
  frequently exceeds timeouts. Budget a full 12-scenario leg accordingly (~20+ min).

## Local models for a local-only IDE coding assistant

Asked by the owner (2026-08-03): is there any sense in local models for a local-only
IDE coding assistant? Answer: yes — privacy for source code, zero per-call cost, and
MLX 4-bit models are first-class on Apple Silicon. Selection guidance for a 24 GB M4:
- ~6 GB class (e.g. qwen3.5-9b): daily-driver autocomplete/small refactors — fast,
  stays resident alongside other tools. No frontier parity on multi-file reasoning.
- ~14-16 GB class (e.g. qwen3.6-27b 4-bit, gpt-oss-20b): whole-file reasoning, hard
  tasks — better but slower, eats the machine; load alone.
- One big chat model at a time is the realistic budget on 24 GB (guardrail math in the
  reference). Embedding models coexist fine for RAG.
- Failure mode is speed/ceiling, not feasibility: trade completion quality and context
  size for zero-cost, zero-leak completions.

## Judge-quality sanity method

When comparing judge models, use constructed scenarios with KNOWN-GOOD verdicts (grounded
vs hallucinated response, off-topic vs on-topic, missing-fields vs complete) and score the
judge's output for correctness — not just schema validity. Small local models are weaker
judges on nuanced dimensions (groundedness, equivalence); a 4B model that emits valid JSON
is not automatically a good judge.

Operational rules that make the comparison sound (validated in the llm-validation-part-2
spike, 2026-08-03):

- **Express known-good verdicts on the SAME 0-5 NumericMetric scale the evaluators emit** —
  not pass/fail booleans. A scenario contract test should assert this.
- **Agreement rule: ±1 tolerance + direction reading.** Pass cases (known-good 5) count as
  correct when the judge scores ≥4; fail cases (known-good 0) when it scores ≤2; a 3 is
  ambiguous — count separately, never as correct.
- **Adoption bar: ≥80% known-good agreement** (e.g. 10/12) before recommending a local
  model as judge. With ~10-12 scenarios one flip moves 3-5%, so state the count basis
  (per-evaluator-verdict, per-scenario also reported) before results exist.
- **Freeze the scenario set and verdicts BEFORE any judge runs** — never retro-tune a
  scenario after seeing scores, or the bar becomes self-fulfilling.
- **Reproducibility knobs:** temperature 0 (and a fixed seed where supported) via
  `ChatClientBuilder().ConfigureOptions`; log HTTP request bodies (proves the endpoint
  path AND whether the evaluators actually send `response_format` — some evaluator
  versions may not, which changes whether reasoning models can participate).
- **Size-vs-quantization reality:** 2-bit 27B can judge worse than Q4_0 12B — quantization
  quality matters as much as parameter count for judge reliability. Model-size guidance:
  Coherence/Fluency are easy dimensions a 4B handles; Groundedness/Equivalence need
  reasoning a 4B is weak at. For trend-not-gate use (nightly regression detection), a
  small judge's systematic bias cancels out — stability beats absolute accuracy there.
- **Judge placement:** never add a judge call to the live request path — it doubles
  latency and LLM cost per response, and the score has no actionable consumer at request
  time (ADR-0088 excludes online/telemetry eval for exactly this). Judges belong offline:
  nightly fixed-scenario runs (CI trend) + dev-time on-demand runs (local judge). The
  repo already has in-pipeline meta-judges where the score IS the product
  (`TailoredCvReviewer`, `ScorePracticeSession`) — that pattern is the exception, not the
  rule.

## Support files

- `references/classifier-and-benchmark-qa.md` — QA & benchmark guidelines for .NET AI classifiers & ONNX model pipelines: dataset target alignment, async threadpool memory allocation traps, ONNX cold-start vs warm inference isolation, unmanaged RAM profiling, and composite classifier bypass metrics.
- `references/lm-studio-local-judge.md` — probed LM Studio endpoints, payloads, model
  behaviors, memory-guardrail math, and the machine's model inventory (dated).
- `references/eval-10.8-spike-findings.md` — S1 spike transcript: wire behavior of the
  Quality evaluators (tag protocol, no json_schema), 10.8 API shapes, agreement-method
  math, LM Studio silent-fallback trap, performance numbers for local judges (dated).
