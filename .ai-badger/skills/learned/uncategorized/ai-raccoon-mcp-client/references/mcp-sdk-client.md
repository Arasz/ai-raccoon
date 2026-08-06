# AiRaccoon MCP client via the official mcp SDK (Python)

Verified 2026-08-06 against mcp SDK 1.28.1 in the hermes venv
(`~/.hermes/hermes-agent/venv`), talking to the installed `ai-raccoon` binary
(1.0.9) and the server source (`src/AiRaccoon/Tools/MemoryTools.cs`,
`src/AiRaccoon.Core/Memory/MemorySearchResult.cs`). Use the SDK instead of
hand-rolled httpx/SSE when the client lives in a Python env that has `mcp`
installed (the hermes venv does; a bare repo .venv may not).

## Transport tuple arity — the crash waiting to happen

The two SDK client context managers yield DIFFERENT tuple shapes; wrong
unpacking crashes at runtime, not at import:

- `mcp.client.stdio.stdio_client(...)` yields a **2-tuple** `(read, write)`
  (stdio/__init__.py:189).
- `mcp.client.streamable_http.streamable_http_client(url)` yields a
  **3-tuple** `(read, write, get_session_id)` (client/streamable_http.py:670).

Both are entered manually: `read, write = await ctx.__aenter__()` (stdio) /
`read, write, _ = await ctx.__aenter__()` (http), then
`session = ClientSession(read, write)` and `await session.initialize()`.
Exit both the session and the ctx on teardown (`__aexit__`); the stdio ctx
exit terminates the child process.

## Persistent session: sync-over-asyncio

The SDK is asyncio; a stdio child must NOT be re-spawned per call. Keep ONE
event loop in a daemon thread and bridge synchronously:

- `connect()`: `loop = asyncio.new_event_loop()`; thread target
  `loop.run_forever`; `asyncio.run_coroutine_threadsafe(self._open(), loop).result(timeout=30)`.
- `_call()`: `asyncio.run_coroutine_threadsafe(session.call_tool(name, arguments=args), loop).result(timeout=15)`.
- `close()`: `run_coroutine_threadsafe(self._close(), loop).result(timeout=10)` then
  `loop.call_soon_threadsafe(loop.stop)`; join thread. Idempotent.
- On connect failure: close() (kills the loop thread) then re-raise — never
  leave a half-started loop.
- Import `mcp` lazily inside `_open()` (not at module top) so unit tests that
  fake the client never need the SDK installed.

## Result extraction

`CallToolResult` → first `content` item with `type == "text"` → `json.loads(text)`
(server returns JSON strings; guard with try/except → return raw text).
Check `result.isError` (true = tool-level error) BEFORE parsing.

## Verified tool result shapes (camelCase, from source)

- `memory_search(projectId, query, scope, limit, minScore, ...)` →
  `{"results": [ {hash, seq, ranking, path, snippet, sourceFile, chunkIndex, totalChunks} ]}`
  — the snippet field carries the content text; ranking is the score.
- `memory_write(projectId, content, workspaceId?, agentId?, context?, sourceFile?, section?)` →
  `{hash, path, context, createdAt}`.
- `memory_stats(projectId)` → `{entries, pending, contexts}`.
- `memory_share(projectId, hash)` → `{shared, context}`.

`projectId` is REQUIRED on every tool call (server-side guard) — client-side
inject it; model-facing schemas should omit it.

## Integration test isolation

Spawn the REAL installed binary with a temp bank — the stdio child inherits
`os.environ`, so `AIRACCOON_DATA_ROOT=<tmp>` fully isolates the test from the
real `~/.ai-raccoon` bank. FTS-only mode (no embedding model configured) still
returns search hits — pass `minScore: 0.0` in tests to avoid threshold flakiness.
Search/round-trip tests then need no ONNX model at all.
