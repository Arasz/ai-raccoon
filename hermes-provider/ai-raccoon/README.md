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
    project_id: ""        # empty -> derived "hermes-<profile>" (e.g. hermes-default)
    search_limit: 5
    min_score: 0.5
    scope: all
```

- **stdio (default):** the provider spawns the installed `ai-raccoon` binary as a child
  process and speaks MCP over stdio. The child inherits the environment, so
  `AIRACCOON_DATA_ROOT` (temp bank) flows through for testing. No ports.
- **http:** the provider connects to a running server's Streamable HTTP endpoint —
  useful when one long-running server should serve several clients.
- `project_id` is derived `hermes-<profile>` unless overridden; every memory operation
  is scoped to it inside the shared `~/.ai-raccoon` bank.

No secrets: AiRaccoon is local. The bank passphrase stays in the server's own
environment, never in this plugin.

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
