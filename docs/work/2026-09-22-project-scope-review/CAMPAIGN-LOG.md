# Campaign log — infrastructure incident and recovery

Base: `5bca1900` · 2026-09-22

## Lane provenance

| Lane | Model that produced the integrated report | Notes |
|---|---|---|
| A architecture | `openrouter/deepseek/deepseek-v4.1-flash` | first run, complete |
| C lifecycle | `openrouter/deepseek/deepseek-v4.1-flash` | first run, complete (live MCP lifecycle) |
| G product design | `openrouter/deepseek/deepseek-v4.1-flash` | first run, complete |
| B, D, E, F, H, I, J | `openrouter/deepseek/deepseek-v4.1-flash` | **re-run** after the incident below |

## Incident (why lanes B/D/E/F/H/I/J were re-run)

A mid-campaign model-infrastructure failure silently destroyed lane output. It was **not** a product or repo problem.

1. `openrouter/deepseek/deepseek-v4.1-flash` requests began being routed through OpenRouter's
   **Poolside shared pool**, which returned `402 billing-exhaustion`
   (`limit_source: upstream_provider_shared_pool`, `provider_name: Poolside`). This was **not** account
   credit exhaustion — a direct `/api/v1/key` query showed `limit_remaining ≈ 0.98` of `1` with real
   usage of cents.
2. The delegating harness auto-retried and fell back to `poolside/laguna-s-2.1:free`, which is
   rate-limited on its shared pool (`free_model_daily_requests used: 48`) and returns **no assistant
   output** — the "silent-JSON" failure.
3. Consequence: lanes B, D, E, F, H, I, J were marked `done` by the harness while producing only a
   pre-tool monologue and **no saved report**. Only A, C, G saved real reports.

## Route candidates measured during recovery

| Route | Verdict |
|---|---|
| OpenRouter key | healthy — `limit_remaining 0.978`, usage ~$0.02 |
| `GEMINI_API_KEY` | **invalid** — HTTP 401 `ACCESS_TOKEN_TYPE_UNSUPPORTED` |
| Groq (`GROQ_API_KEY`) | key valid, but `x-ratelimit-limit-tokens: 8000` per minute for every served model — unusable for ~70k-token lane contexts |
| `openrouter/openai/gpt-oss-120b` | responds, but emits tool calls in harmony syntax that pi cannot parse (`format:"unknown"`) — no tool-driven work possible |
| `openrouter/anthropic/*`, `openrouter/google/*` | HTTP 404 — guardrail/data-policy blocked for this key |
| `openrouter/deepseek/deepseek-chat-v3.1` (Novita) | works (tool calls parse) — used for a first pass, but materially shallower: confirmation-heavy reports and at least one false-positive MEDIUM (Lane D F1, "gap-jumping migrations", refuted against `MemorySchema.cs:707-800`) |
| `openrouter/deepseek/deepseek-v4.1-flash` (Wafer / DeepSeek) | **preferred** — reachable again; tool calls parse; same model as the A/C/G lanes |

## Measures taken

- Lanes re-dispatched with an explicit instruction to **write their report file incrementally** from the
  first 2–3 findings, so a future cut-off cannot lose everything.
- A **global calibration note** appended to `BRIEFS.md`: a finding must exhibit the concrete failing
  scenario; a code-reading impression is not `MEASURED`.
- The v3.1 first-pass reports are preserved at `docs/work/2026-09-22-project-scope-review/lanes/firstpass-v31/` as supplementary
  evidence; they are **not** part of the integrated review.

## Live incident during the run (confirms Lane E F3)

While Lane H ran `SpeedGateCoverageTests`, the product's one-shot CLI auto-started a backend against the
**default data root** — `AiRaccoon --data-root /Users/arasz/.ai-raccoon --install-scope user --quiet serve
--port 7721` (PIDs 19902/19903) — and left it running after the test finished. Port 7721 had been free
before the run, so this was not the owner's own server.

The orphan was stopped with SIGTERM; port 7721 is free and no `serve` process remains. The owner's bank
(`memory.db`, 1.58 GB, plus `-shm`/`-wal`) was held open read-write for ~5 minutes and its WAL was touched;
SQLite's WAL design makes an abrupt close safe, and no lane deleted or inserted into that bank, but the
episode is exactly the exposure Lane E F3 describes (a read-only command buying up to four hours of
unattended background writes) and motivates treating F3 as more than a theoretical finding.
