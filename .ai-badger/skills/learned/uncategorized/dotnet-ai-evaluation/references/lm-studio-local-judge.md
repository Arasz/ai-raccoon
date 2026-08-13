# LM Studio local judge — probe transcript (2026-08-03, v0.4.20, Apple M4 / 24 GB)

Verified by live curl probes against a running LM Studio on the developer machine.
Machine: Mac16,12, Apple M4, 24 GB unified RAM, macOS 26. LM Studio 0.4.20+1.

## Server shape

- OpenAI-compat base: `http://localhost:1234/v1` — `/v1/chat/completions`, `/v1/models`.
- Native API: `http://localhost:1234/api/v1/...` — `/api/v1/models`,
  `/api/v1/models/load`, `/api/v1/models/unload`, `/api/v1/models/download`.
- No auth configured; any placeholder API key works with the OpenAI SDK.
- `/v1/models` lists every installed model (id/object/owned_by) — NOT just loaded ones.
  `/api/v1/models` gives per-model detail: `type`, `quantization`, `size_bytes`,
  `params_string`, `loaded_instances[].config` (context_length, parallel, reasoning
  budget), `max_context_length`, `capabilities` (vision, trained_for_tool_use,
  reasoning.allowed_options `["off","on"]` + default).

## Model inventory (this machine, 2026-08-03)

Chat-capable (LLM type):
- `prism-ml/bonsai-27b` — qwen3_5 arch, 2-bit AXQ, 8.5 GB, max_ctx 262144, loaded at
  ctx 41472. **Reasoning model, default ON.**
- `google/gemma-4-e4b` — 4-bit, 6.9 GB. Clean json_schema output in `content`.
- `google/gemma-4-12b-qat` — Q4_0, 7.2 GB. **Would not load** (see guardrail below).

Embedding-only (NOT usable as chat judge): ax-qwen3-embedding-8b-mlx-axq,
ax-qwen3-embedding-0.6b-mlx-axq, text-embedding-mxbai-embed-large-v1,
text-embedding-qwen3-embedding-0.6b, text-embedding-embeddinggemma-300m,
text-embedding-embeddinggemma-300m-qat, text-embedding-nomic-embed-text-v1.5.

## Probe: json_schema on gemma-4-e4b — WORKS

```bash
curl -s http://localhost:1234/v1/chat/completions -H "Content-Type: application/json" -d '{
  "model": "google/gemma-4-e4b",
  "messages": [{"role": "user", "content": "Rate the response. Output ONLY JSON: {\"score\": 0-100, \"reason\": \"short\"}"}],
  "max_tokens": 120,
  "response_format": {"type": "json_schema", "json_schema": {"name": "rating",
    "schema": {"type": "object", "properties": {"score": {"type": "integer"}, "reason": {"type": "string"}},
    "required": ["score", "reason"]}}}
}'
```
→ `choices[0].message.content` holds the JSON; `finish_reason: stop`. This is the shape
Microsoft.Extensions.AI.OpenAI expects.

## Probe: bonsai-27b reasoning trap — FIXED by UI "Reasoning Disabled" preset

Same payload with `"model": "prism-ml/bonsai-27b"` → `content: ""`,
`reasoning_content: "{\"score\": 95, ...}"`, `finish_reason: "stop"`. The OpenAI SDK
reads `content` only, so the judge would receive an empty response.

- Plain chat (no response_format) DOES return content after reasoning
  (e.g. `content: "\n\nPONG"`, reasoning in `reasoning_content`).
- Per-request `"reasoning": {"effort": "off"}` — IGNORED (still split).
- `/api/v1/models/load` with `{"model": "prism-ml/bonsai-27b", "reasoning": "off"}`
  → `"Unrecognized key(s) in object: 'reasoning'"`; with a `{"config": {...}}` wrapper
  → `"Unrecognized key(s) in object: 'config'"`.
- **FIX (verified [RAN] after the user applied the UI preset):** with the model's
  "Reasoning Disabled" preset set in the LM Studio app (model config → Reasoning →
  Disabled), the SAME json_schema payload returns clean JSON in `content` with
  `reasoning_content: ""` — no per-request param needed. The preset applies on model
  reload. `capabilities.reasoning.allowed_options` still reports `["off","on"]` default
  `on` in `/api/v1/models` even when the preset is active — the listing reflects the
  capability, not the live instance behavior; trust the probe, not the listing.
- Native `/api/v1/chat` accepts a top-level `"reasoning": "off"` string and then returns
  JSON in `output[].content` with `stats.reasoning_output_tokens: 0` — the native API
  does honor it per-request, the OpenAI-compat endpoint does not.
- Conclusion: a reasoning-default model IS usable as a schema-forced judge, but the
  thinking-off switch is UI-only on the OpenAI-compat path. The plan must include a
  "user flips the preset" decision point, or disqualify the model.

## Probe: load endpoint param whitelist + memory guardrail

`/api/v1/models/load` accepts ONLY: `model`, `context_length`, `eval_batch_size`,
`flash_attention`, `num_experts`, `offload_kv_cache_to_gpu`, `echo_load_config`.
(Documented on lmstudio.ai/docs/developer/rest/load; confirmed by unrecognized_keys
errors for anything else.)

gemma-4-12b-qat load attempts:
- `{"model": "google/gemma-4-12b-qat"}` → refused: "requires approximately 44.87 GB of
  memory" (default context = huge KV estimate).
- `{"model": "google/gemma-4-12b-qat", "context_length": 8192}` → refused: "requires
  approximately 8.20 GB" — still refused because bonsai-27b (8.5 GB) + gemma-4-e4b
  (6.9 GB) + embeddings were already loaded.
- **SUCCESS (verified [RAN]):** after unloading bonsai-27b and gemma-4-e4b via
  `/api/v1/models/unload {"instance_id": "<model id>"}` (note: `instance_id`, not
  `model`), the same `context_length: 8192` load succeeded — `load_time_seconds: 13.3`,
  `status: "loaded"`. Restore the user's models afterwards (unload the probe model,
  reload the originals).
- Lesson: the guardrail estimate is context-sensitive, and loadability is
  load(context_length, what else is resident). On 24 GB, unload other models before
  trying a ~7-8 GB model; 27B@2bit + 4B@4bit already saturate the machine. The unload
  endpoint's required key is `instance_id`, and `/api/v1/models` reports
  `loaded_instances[].id` to feed it.

## Implications for the ADR-0088 spike

- gemma-4-e4b is the lowest-friction local judge (valid json_schema in content, no
  preset needed).
- bonsai-27b is usable as a schema-forced judge ONCE the "Reasoning Disabled" preset is
  applied in the UI (verified) — the API cannot flip it. Factor the user step into the
  plan, and re-probe after any reload (the preset applies per load).
- gemma-4-12b-qat (the QAT-trained model, best quality/GB on paper) loads at
  context_length 8192 after the other chat models are unloaded (verified, 13.3 s) and
  also needs the Reasoning Disabled preset (it defaults to reasoning ON).
- Judge comparison protocol: one chat model loaded at a time; record per-model verdict
  agreement against the frozen known-good scenario set (≥80% bar, ±1 tolerance, pass ≥4 /
  fail ≤2 / 3 ambiguous); pin temperature 0 + seed; log HTTP request bodies to prove
  routing and whether response_format is actually sent.
- Local judge is dev-only: the nightly CI job cannot reach localhost. CI judge stays a
  cloud OpenAI-compatible key (EVAL_JUDGE_API_KEY).
