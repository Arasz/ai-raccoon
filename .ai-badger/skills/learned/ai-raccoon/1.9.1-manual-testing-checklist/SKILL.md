---
name: 1.9.1-manual-testing-checklist
description: Use when running AiRaccoon 1.9.1 manual testing including noise filtering and Polly resilience.
---

# AiRaccoon 1.9.1 Manual Testing Checklist

Use this skill when evaluating the **AiRaccoon 1.9.1** release, specifically verifying pre-write noise filtering policies, zero-shot noise vectors, Polly v8 resilience pipelines, and the full 25-tool MCP surface.

## Process

1. Load `templates/checklist-template.json` from this skill.
2. For each step in the checklist, execute the corresponding evaluation or test.
3. Live test the new **Noise Filtering Features**:
   - **Process Noise Interception (`HermesProcessNoisePolicy`)**: Invoke `memory_write` with process completion text such as `[Process proc_12345 finished with exit code 0]`. Verify `NoiseFilteringService` flags it as noise, routes it to the isolated noise store with a 14-day TTL, and `memory_search` does NOT return this entry in main memory results.
   - **Zero-Shot Semantic Noise Filter (`ZeroShotEmbeddingNoisePolicy`)**: Invoke `memory_write` with generic filler or noise matching bundled noise vectors (`ZeroShotSemanticNoiseFilter`). Verify zero-shot semantic noise detection degrades the entry automatically.
   - **Clean Domain Write**: Invoke `memory_write` with a high-value architectural decision record. Verify it passes clean and is immediately searchable with ranking 1 on `memory_search`.
4. Verify **Polly Resilience**: Confirm `ServerProbe` and `AssetDownloader` use Polly v8 exponential backoff with decorrelated random jitter.
5. Execute the full 25 MCP tools and 2 MCP prompts surface check.
6. Fill out observed results, check status (`checked: true`), evaluation (`accepted: true/false`), and acceptation reason in the template.
7. Write the filled-out checklist to `.ai-raccoon/state-checklist-1.9.1-<timestamp>.json`.

## Checklist Scope

- **1.9.1 Tool & Version**: `ai-raccoon --version` -> `1.9.1`.
- **Server Lifecycle**: `ai-raccoon serve --restart` background HTTP service on port 7721.
- **Process Noise Interception**: `HermesProcessNoisePolicy` trapping process completion logs.
- **Zero-Shot Semantic Noise Filter**: `ZeroShotEmbeddingNoisePolicy` evaluating write embeddings vs bundled noise vectors.
- **Clean Domain Memory**: High-value ADR and domain context ingestion.
- **Polly v8 Resilience**: Exponential backoff with random drift/jitter.
- **Full MCP Tool Surface**: 25 tools + 2 prompts (`memory-usage-guide`, `workspace-consolidation-guide`).
- **Observability**: `[LoggerMessage]` events, EventIds, and `sqlite3mc` 2.4.0 engine version.
