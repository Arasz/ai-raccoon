# ai-raccoon memory provider plugin

Hermes memory provider backed by the [AiRaccoon](https://github.com/Arasz/ai-raccoon)
MCP memory server. Implements the `MemoryProvider` ABC
(`agent/memory_provider.py`), so the agent gets the full provider lifecycle on top of
the AiRaccoon bank: per-turn prefetch, background sync, a system-prompt block, and
mirroring of built-in memory writes.

Design record: `docs/work/2026-08-06-hermes-ai-raccoon-provider-protocol.md` (in the
ai-raccoon repo). Interface record: `docs/work/2026-08-06-hermes-memory-provider-interface.md`.

## Install

```bash
mkdir -p ~/.hermes/plugins
cp -R ai-raccoon ~/.hermes/plugins/ai-raccoon
hermes plugins list        # ai-raccoon should appear as a memory provider
```

Then activate it (replaces the currently active external provider — only one can run):

```bash
hermes config set memory.provider ai-raccoon
```

Rollback: `hermes config set memory.provider holographic` (or empty for built-in only).

## What it does

| Hermes lifecycle point | AiRaccoon call |
|---|---|
| `prefetch(query)` | `memory_search` (scope all, limit 5, minScore 0.5) → `## AiRaccoon Memory` block |
| `sync_turn(user, assistant)` | `memory_write` of the assistant message, `sourceFile=hermes/<session>`, `section=turn` (background) |
| tools | `memory_search`, `memory_write`, `memory_stats`, `memory_share` (curated surface, `projectId` injected) |
| `on_memory_write(add, …)` | `memory_write` mirror, `sourceFile=hermes-memory` |
| `shutdown()` | terminates the spawned server / closes the HTTP session |

## Config

Under `plugins.ai-raccoon` in the profile's `config.yaml` (also prompted by
`hermes memory setup` via `get_config_schema`):

```yaml
plugins:
  ai-raccoon:
    transport: stdio      # stdio | http
    url: http://127.0.0.1:7721/mcp   # http mode
    binary: ai-raccoon    # stdio mode (resolved on PATH)
    binary_args: []       # extra spawn args, e.g. ["--data-root", "/tmp/bank"]
    status_words: true    # one-word stderr cue per tool call ("searching", "remembering", …)
    project_id: ""        # empty -> derived "hermes-<profile>" (e.g. hermes-default)
    search_limit: 5
    min_score: 0.5
    scope: all
```

- **stdio (default):** the provider spawns the installed `ai-raccoon` binary as a child
  process and speaks MCP over stdio. No ports. For isolation (tests, scratch banks), pass
  spawn args via `binary_args` — the production CLI resolves the data root ONLY from the
  `--data-root` flag, so `binary_args: ["--data-root", "/tmp/bank"]` is the way to point a
  spawned server at a temp bank.
- **http:** the provider connects to a running server's Streamable HTTP endpoint —
  useful when one long-running server should serve several clients.
- `project_id` is derived `hermes-<profile>` unless overridden; every memory operation
  is scoped to it inside the shared `~/.ai-raccoon` bank.

No secrets: AiRaccoon is local. The bank passphrase stays in the server's own
environment, never in this plugin.

## Data sources

- **Status words:** with `status_words: true` (default) the spawned server prints one
  word per tool call to stderr — "remembering", "searching", "counting", … — instead of
  info logs. Set `status_words: false` for full logs.
- **Memory operation log:** when the `AIRACCOON_MEMORY_LOG` env var points at a file
  (inherited by the spawned server), every tool invocation appends one JSONL row:
  `{"ts", "tool", "project_id", "status", "error_type?", "duration_ms"}` — an analysis
  data source for memory usage and retrieval behavior.

## Notes

- Only one external memory provider can be active at a time (`memory.provider`).
- `sync_turn` and prefetch run only for primary agents (`agent_context == "primary"`);
  cron and subagent contexts never write to the bank.
- The plain-MCP ai-raccoon server registration in `~/.hermes/config.yaml` can stay
  alongside (full 20-tool surface); the provider adds the lifecycle on top of the
  curated 4-tool surface.

## Tests

```bash
# unit (no server needed)
python3 -m pytest tests/

# + integration: spawns a REAL ai-raccoon server against a temp bank
python3 -m pytest tests/ --run-slow
```

Requires the hermes runtime venv (has `mcp` + `pytest`), e.g.
`~/.hermes/hermes-agent/venv/bin/python -m pytest tests/`.
