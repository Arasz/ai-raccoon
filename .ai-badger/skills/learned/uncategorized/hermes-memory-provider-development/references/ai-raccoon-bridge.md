# AiRaccoon Hermes provider — implementation record (2026-08-06)

Repo: `integrations/hermes/ai-raccoon/` in Arasz/ai-raccoon (PR #61; the directory was
`hermes-provider/` until 2026-08-08). Protocol design:
`docs/work/archive/2026-08-06-hermes-ai-raccoon-provider-protocol.md` (owner-ratified).
Interface record: `docs/work/archive/2026-08-06-hermes-memory-provider-interface.md`.

## Ratified decisions

- D1 transport: stdio child (default) + Streamable HTTP option
- D2 tool surface: curated 4 — memory_search / memory_write / memory_stats / memory_share
- D3 project id: config `project_id` override wins; else `{workspace|'hermes'}-{identity|'default'}`
  (kwargs from initialize: agent_workspace, agent_identity)
- D4a sync_turn writes the assistant final message: `sourceFile=hermes/<session>`,
  `section=turn`, `agentId=hermes-<profile>` (daemon thread)
- D4b no session-end summary in v1 (`on_session_end` no-op)
- D6 keep the plain-MCP ai-raccoon bridge (config.yaml MCP server) alongside

## Server tool contracts (src/AiRaccoon/Tools/MemoryTools.cs)

- `memory_write(projectId, content, workspaceId?, agentId?, context?, sourceFile?, section?)`
  → `{hash, path, context, createdAt}`
- `memory_search(projectId, query, scope=all|project|shared, workspaceId?, limit=20,
  minScore=0.7, rrfK, ftsWeight, vectorWeight, contextLabel?)` → `{"results": [...]}`
- `MemorySearchResult` = `{hash, seq, ranking, path, snippet, sourceFile, chunkIndex,
  totalChunks}` (src/AiRaccoon.Core/Memory/MemorySearchResult.cs; camelCase JSON) — prefetch
  formats `snippet` + `ranking`
- `memory_stats(projectId)` → `{entries, pending, contexts}`
- `memory_share(projectId, hash)` → `{shared, context}`
- `memory_delete(projectId, hash)` → `{deleted: 0|1}` — the supported cleanup path (handles
  FTS/vec side tables; direct SQL is wrong)
- Every tool requires `projectId` (server throws invalid-params otherwise)

## The real-bank incident (MUST-FIX lesson)

Integration tests set `AIRACCOON_DATA_ROOT` to a temp dir, but the production CLI
(CliArgs.cs) resolves the data root ONLY from `--data-root` (default `~/.ai-raccoon`); the
env var is a test-host-only construct. Result: 4 probe rows landed in the REAL bank under
project `hermes-itest`. Fix: spawn with `binary_args: ["--data-root", <tmp>]` (passed via
`StdioServerParameters(command, args)`); cleanup via `memory_delete` per hash (search first
to get hashes, delete each, then `memory_stats` to verify 0). Verify 0 rows in the real
store BEFORE and AFTER every integration run.

## HTTP smoke recipe

Spawn `[binary, --transport, http, --port, 0, --data-root, <tmp>]`, parse stderr for
`http transport listening on (http://\S+)` (HostExtensions.cs log line), then
`HttpClient(url).connect()` → stats round trip → close → terminate the subprocess.

## Verification recap (what passing means)

- 31 unit tests: fake client duck-type with `connect()` (a fake WITHOUT connect makes
  initialize fail and the provider silently run client-less — the failure mode that took a
  whole red run to spot), spec-loaded plugin module (hyphenated dir name), real C# result
  shapes pinned in fakes.
- 6 slow tests: 5 stdio integration + 1 HTTP smoke, real spawned server on a temp
  `--data-root` bank; spawn failure under `--run-slow` is pytest.fail, binary-missing is skip.
- Loader round trip: `HERMES_HOME=<tmp> python -c "from plugins.memory import
  load_memory_provider; p=load_memory_provider('ai-raccoon')"` — discover → load →
  initialize (spawns child, server logs visible) → shutdown.
- Test runtime: `/Users/arasz/.hermes/hermes-agent/venv/bin/python -m pytest` (venv has
  mcp 1.28.1 + pytest 9.x).
