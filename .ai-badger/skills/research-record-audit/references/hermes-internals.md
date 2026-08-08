# Hermes Agent internals map (verified 2026-08-02)

All paths/lines verified by direct read during the ai-badger task-tracking audit.
Hermes Agent source is NOT in consumer repos — it lives at `~/.hermes/hermes-agent/`.

## Source locations
- `~/.hermes/hermes-agent/hermes_cli/` — CLI: hooks.py, oneshot.py, config_defaults.py, ...
- `~/.hermes/hermes-agent/hermes_state.py` — state.db access layer
- `~/.hermes/hermes-agent/gateway/session.py` — gateway session lifecycle
- Installed plugin copy: `~/.hermes/plugins/ai_badger_hooks.py` (must be byte-identical
  to the project's `.ai-badger/hooks/ai_badger_hooks.py`; a `COPY_SKEW_REFUSAL` guard
  makes a skewed copy register NOTHING — plugin loads silently with zero hooks).

## Hook payloads — `hermes_cli/hooks.py` `_DEFAULT_PAYLOADS` (:112-216)
Used verbatim by `hermes hooks test` / `hermes hooks doctor` (comment :107-111), so the
test stdin == production stdin shape.
- `pre_tool_call` / `post_tool_call`: tool_name, args, session_id, task_id, tool_call_id
  (+ result, duration_ms for post).
- `pre_llm_call`: session_id, user_message, model, platform.
- `on_session_start`: session_id only. `on_session_end`: session_id, task_id, turn_id,
  completed/failed/interrupted booleans, model, platform.
- `post_api_request`: session_id, model, provider, api_call_count, **usage
  {"input_tokens", "output_tokens"}** — per-call usage without any transcript parse.
- `subagent_stop`: parent_session_id, child_role, child_status, duration_ms.

## Usage-file writer — `hermes_cli/oneshot.py` `_write_usage_file` (:127-165)
`hermes -z --usage-file PATH` writes a JSON report: estimated_cost_usd, cost_status,
cost_source, input/output/cache_read/cache_write/reasoning tokens, total_tokens, api_calls,
model, provider, session_id, completed, failed, service_tier. Written even on failure.

## state.db (`~/.hermes/state.db`) — read-only probe pattern
```python
import sqlite3
con = sqlite3.connect('file:~/.hermes/state.db?mode=ro', uri=True)
```
- `sessions`: id, source, session_key, chat_id, thread_id, display_name, expiry_finalized,
  model, parent_session_id, started_at, ended_at, end_reason, message_count,
  tool_call_count, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,
  reasoning_tokens, cwd, git_branch, git_repo_root, billing_*, estimated_cost_usd,
  actual_cost_usd, title, api_call_count, profile_name, archived, pinned, ...
- `session_model_usage`: per (session_id, model): api_call_count, input/output/cache_read/
  cache_write/reasoning tokens, estimated_cost_usd, first_seen, last_seen — the per-model
  breakdown Claude-transcript parsing builds by hand.
- `async_delegations`: delegation_id, origin_session, ..., dispatched_at, completed_at,
  JSON payload (goal, status, api_calls, duration_seconds, model, tokens{input,output},
  tool_trace, ...). Child session linkage: child `sessions.parent_session_id` == parent id,
  child `started_at` ≈ delegation `dispatched_at`.
- `messages`: 58,818 rows observed; `token_count` is **NULL on every row** (0/58818) —
  per-message context occupancy is NOT in the store.
- `request_dump_*.json` in `~/.hermes/sessions/`: ALL observed (17/17) are error captures
  (`reason: non_retryable_client_error`), keys = timestamp/session_id/reason/request
  (method,url,headers,body)/error — **no usage anywhere**. Usage lives in API responses,
  which these request-error dumps never capture. Do not claim "dump has usage" without
  checking.
- `hermes insights --days N` → Platforms table includes a `subagent` row (per-dispatch
  aggregation; e.g. 26 sessions / 1,927 msgs / ~109M tokens in the Aug 2026 window).
  Numbers are snapshots — drift run-to-run.
- `hermes sessions` subcommands: list, export (JSONL/Markdown/QMD), delete, prune, archive,
  optimize, optimize-storage, repair, recover, stats, rename, browse. prune is manual.

## Session retention semantics
- `hermes_cli/config_defaults.py` `sessions` section (:2596-2620): `auto_prune: False`
  (:2608), `retention_days: 90` (:2611), `auto_archive: False`. The ONLY `auto_prune:
  True` default (:441) is the **filesystem checkpoints** store (rollback snapshots) —
  unrelated to sessions.
- `hermes_state.py` `set_expiry_finalized` (:2981-2996) + `gateway/session.py`
  (:1911-1938): a gateway lifecycle flag mirroring `SessionEntry.expiry_finalized`
  (sessions.json) — NOT a retention/deletion policy. Session deletion is manual.

## ai-badger task-tracking internals (as re-derived)
- `features/common/hooks/hooks-manifest.json`: 8 hooks; the 4 task hooks
  (session-start-tracking, task-checkpoint, task-checkpoint-session-end, dispatch-gate)
  list only `claude`; hermes entries only on drift-notice, context-enrichment,
  commit-reminder.
- `tooling/validate.py` `HOOKS_MANIFEST_AGENT_EXEMPTIONS` (:84-130): hermes exempted from
  all 4 task hooks with "Claude-only by design" reasons; prompt-markers hermes gap is an
  "acknowledged gap, not a design limit".
- Scaffolder has **no agent-based script filtering** (`engine/badger_lib.py`; only
  `config.exclude` patterns filter). `extensions/<agent>/extension.md` files are
  provenance rows, not behavior — a doc claiming "not scaffolded for hermes" can be false
  while the scripts ship byte-identical.
- `features/common/skills/task/scripts/tracker_lib.py`: `CLAUDE_SESSION_ENV =
  "CLAUDE_CODE_SESSION_ID"` (:35); `resolve_own_session()` (:373-406) tries env var → PID
  ancestry → unique cwd, `return {}` on failure; `parse_transcript_usage()` reads
  `<dir>/<session-id>/subagents/*.jsonl` (a layout Hermes does not produce).
- `task_tracker.py` `_session_or_die()`: demands `--session-id` when resolution fails
  (prints guidance, `sys.exit(2)`).
- `current-session.json` writers: `session_start_hook.py` AND `user_prompt_hook.py`, both
  via `tracker_lib.save_current_session()` (:328) — all Claude-only hooks.
