# Hermes state.db — verified schema and access patterns

Verified live against `/Users/arasz/.hermes/state.db` (read-only sqlite, 2026-08-03) while a Hermes session was running. `HERMES_SESSION_ID` is set on every Hermes tool subprocess (live value format: `YYYYMMDD_HHMMSS_xxxxxx`, e.g.
`20260803_003425_29ed06`); `HERMES_HOME` is usually unset, so the db lives at `~/.hermes/state.db`. This machine's db was ~356 MB.

## Read-only access (never write to the live store)

```python
import sqlite3, os
db = sqlite3.connect("file:" + os.path.expanduser("~/.hermes/state.db") + "?mode=ro", uri=True)
```

- `mode=ro` gives SQLite's own `SQLITE_OPEN_READONLY`; read-only connections do not contend with a live Hermes writer. Never create tables, never write, never `flock`.
- Wrap in `try/except sqlite3.Error` — locked/busy must degrade to "no data", never raise.
- NULL numeric columns are normal for in-flight sessions (`or 0` at the read site).

## Tables and columns (PRAGMA-verified)

**sessions** — one row per session, aggregate usage:
`id` (the session id), `source`, `model`, `input_tokens`, `output_tokens`, `cache_read_tokens`,
`cache_write_tokens`, `reasoning_tokens`, `api_call_count`, `message_count`, `tool_call_count`,
`parent_session_id` (set on child/delegated sessions), `started_at`, `ended_at`, `end_reason`,
`cwd`, `git_branch`, `git_repo_root`, `estimated_cost_usd`, `actual_cost_usd`, `title`,
`profile_name`, `archived`, `pinned`, …

**session_model_usage** — per-session per-model rows (one row per model a session used):
`session_id`, `model`, `api_call_count`, `input_tokens`, `output_tokens`, `cache_read_tokens`,
`cache_write_tokens`, `reasoning_tokens`, `estimated_cost_usd`, `actual_cost_usd`, `first_seen`,
`last_seen`.

**messages** — per message: `id`, `session_id`, `role`, `content`, `timestamp`, `token_count`,
`finish_reason`, `reasoning`, `compacted`, `active`, …

**async_delegations** — dispatched subagent work:
`delegation_id`, `origin_session` (the dispatching session id — verified populated), `origin_session_id`,
`parent_session_id`, `state` (`'completed'` / others), `dispatched_at`, `completed_at`, `updated_at`,
`event_json`, `result_json`, `task_json`, `delivery_state`, `owner_pid`.

## Critical verified facts (the traps)

1. **`messages.token_count` is NULL on every row** (all 59,981 rows on this machine). There is NO per-message token data anywhere in the db, and no context-window snapshot column in
   `sessions`. Context-window occupancy (Claude's `contextTokens`) simply cannot be derived — report 0 with a "not measured" note; do NOT substitute `sessions.input_tokens` (cumulative input, not window occupancy).
2. **`result_json` tokens shape** (completed delegations):
   ```json
   {"results": [{"task_index": 0, "status": "completed", "summary": "...", "api_calls": 26,
                 "duration_seconds": ..., "model": "deepseek/deepseek-v4-pro",
                 "exit_reason": "...", "tokens": {"input": 2143043, "output": 8612},
                 "tool_trace": ..., "live_transcript": ...}],
    "total_duration_seconds": ..., "live_transcripts": ...}
   ```
   Per-delegation total = Σ over results of `tokens.input + tokens.output`. `results[0].model`
   is the per-delegation model; there is NO agent-type field in delegation records.
3. **`cache_write_tokens` is Hermes' "write" side** — maps to Claude's `cache_creation_input_tokens`
   in ai-badger's checkpoint shape (both are freshly-written cacheable input). On this machine it is 0 for most sessions, so cacheEfficiency reads 1.0 — not identical accounting to Claude, and SKILL.md already says cache efficiency does not
   discriminate.
4. **`origin_session` on async_delegations carries the dispatching session id** (e.g. `bg_130724_89094e`).
   `origin_session_id` also exists; verify against live rows when filtering delegations per session.
5. Parent `sessions` aggregates vs child sessions: UNVERIFIED whether `sessions.input_tokens`
   already includes delegated child sessions (rows whose `parent_session_id` = parent). Check before folding child rows in — double-count risk.

## Env resolution for tooling

- Session id: `HERMES_SESSION_ID` (always set on tool subprocesses; no PID/cwd fallback needed).
- Store: `HERMES_HOME` env var → `Path(HERMES_HOME)/state.db`, else `Path.home()/".hermes"/state.db`. Note `Path.home()` honors `$HOME`, which ai-badger's test suite overrides to a scratch dir (`conftest._home_off_limits`) — so `$HOME`
  -derived resolution is test-isolated for free.

## /task tracker mapping (ai-badger checkpoint shape)

| checkpoint key                   | Hermes source                                                                                              |
|----------------------------------|------------------------------------------------------------------------------------------------------------|
| `cumulative.inputTokens`         | `sessions.input_tokens`                                                                                    |
| `cumulative.outputTokens`        | `sessions.output_tokens`                                                                                   |
| `cumulative.cacheReadTokens`     | `sessions.cache_read_tokens`                                                                               |
| `cumulative.cacheCreationTokens` | `sessions.cache_write_tokens`                                                                              |
| `assistantMessages`              | `COUNT(*) FROM messages WHERE session_id=? AND role='assistant'`                                           |
| `byModel`                        | `session_model_usage` rows (slot `assistantMessages` ← `api_call_count`, the only per-model proxy)         |
| `dispatches`                     | completed `async_delegations` where `origin_session = ?` (`byAgentType` stays `{}` — no agent-type exists) |
| `contextTokens`                  | 0 (no per-message tokens exist — honest "not measured")                                                    |

Existing Claude branch of `resolve_own_session` returns `{"sessionId", "transcriptPath"}`
shapes that tests assert with exact dict equality — do NOT add keys to Claude branch returns; add a `"source": "hermes"` key only on the Hermes branch and let callers infer
`source = session.get("source") or "claude"`.
