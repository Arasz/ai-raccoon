# ai-raccoon memory provider plugin

Hermes memory provider backed by the [AiRaccoon](https://github.com/Arasz/ai-raccoon)
MCP memory server. Implements the `MemoryProvider` ABC
(`agent/memory_provider.py`), so the agent gets the full provider lifecycle on top of
the AiRaccoon bank: per-turn prefetch, background sync, a system-prompt block, and
mirroring of built-in memory writes.

Lives at `integrations/hermes/ai-raccoon/` in the ai-raccoon repo. Design record:
`docs/work/archive/2026-08-06-hermes-ai-raccoon-provider-protocol.md`. Interface record:
`docs/work/archive/2026-08-06-hermes-memory-provider-interface.md`.

## Install

One-shot, from the repo root — installs, probes and activates:

```bash
python3 scripts/hermes-provider-setup.py
```

Or by hand:

```bash
mkdir -p ~/.hermes/plugins
cp -R integrations/hermes/ai-raccoon ~/.hermes/plugins/ai-raccoon
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
| `shutdown()` | terminates the spawned child / closes the HTTP session — in stdio mode that child is the proxy; the `serve` backend it started keeps running until its own idle watchdog fires (4h default) |

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
    quiet: true           # spawn the server with --quiet (all logs to a file, none on stdout/stderr)
    status_words: true    # one-word stderr cue per call ("searching", "remembering", …)
    project_id: ""        # empty -> derived "hermes-<profile>" (e.g. hermes-default)
    search_limit: 5
    min_score: 0.5
    scope: all
```

- **stdio (default):** the provider spawns the installed `ai-raccoon` binary as a child
  process and speaks MCP over stdio. Since ADR-0020 that child is a *proxy*, not a
  server: it probes `http://127.0.0.1:7721/mcp`, starts `ai-raccoon serve` when nothing
  answers, and relays every message to it. So this mode does use a port, and the bank is
  held by a separate long-lived backend process that outlives the provider. Pass
  `--transport stdio` in `binary_args` for the old in-process behaviour. For isolation
  (tests, scratch banks), pass spawn args via `binary_args` — the production CLI resolves
  the data root ONLY from the `--data-root` flag, so
  `binary_args: ["--data-root", "/tmp/bank"]` is the way to point a spawned server at a
  temp bank; the proxy forwards `--data-root` and `--install-scope` to the backend it
  starts.
- **http:** the provider connects to a running server's Streamable HTTP endpoint —
  useful when one long-running server should serve several clients. Since ADR-0020 a
  backend started by `ai-raccoon serve` requires an `X-AiRaccoon-Token` header read from
  `<data-root>/mcp-token`, and this client sends no such header — so http mode only
  reaches an endpoint started as `ai-raccoon --transport http`, which is ungated.
- `project_id` is derived `hermes-<profile>` unless overridden; every memory operation
  is scoped to it inside the shared `~/.ai-raccoon` bank.

No secrets: AiRaccoon is local. The bank passphrase stays in the server's own
environment, never in this plugin.

## Data sources

Caller-side observability: the MCP server cannot attribute calls to a client, so the
provider emits both signals itself.

- **Status words:** with `status_words: true` (default) the provider prints one word to
  stderr as each call starts — "searching", "remembering", "counting", … Set
  `status_words: false` to silence.
- **Quiet server:** with `quiet: true` (default) the spawned child runs with `--quiet`,
  and passes it on to the `serve` backend it starts — every log level of that backend,
  including warnings, goes to a file beside its bank (`quiet.log`) instead of
  stdout/stderr. The proxy in the middle is exempt by design: it has no quiet
  destination, so it still writes `Warning`-and-above to stderr, and the line saying the
  backend could not be reached or started is written to stderr no matter what. Expect the
  status words plus, on failure, that one line. If the backend appears to fail silently,
  check `quiet.log` before assuming nothing was logged.
- **Memory operation log:** when the `AIRACCOON_MEMORY_LOG` env var is set (read by the
  provider at session start — a change needs a session restart; the spawned server merely
  inherits it), the provider appends one JSONL row per call:
  `{"ts", "tool", "project_id", "status", "error_type?", "duration_ms", "agent_id",
  "session_id"}` — with caller attribution the server cannot know. An analysis data
  source for memory usage and retrieval behavior.

## Notes

- Only one external memory provider can be active at a time (`memory.provider`).
- `sync_turn` and prefetch run only for primary agents (`agent_context == "primary"`);
  cron and subagent contexts never write to the bank.
- The plain-MCP ai-raccoon server registration in `~/.hermes/config.yaml` can stay
  alongside (full 22-tool surface); the provider adds the lifecycle on top of the
  curated 4-tool surface.

## Tests

From the repo root (`integrations/hermes/tests` is not in `pyproject.toml`'s `testpaths`,
so it needs an explicit path):

```bash
# unit (no server needed)
python3 -m pytest integrations/hermes/tests

# + integration: spawns a REAL ai-raccoon server against a temp bank
python3 -m pytest integrations/hermes/tests --run-slow
```

Requires the hermes runtime venv (has `mcp` + `pytest`), e.g.
`~/.hermes/hermes-agent/venv/bin/python -m pytest tests/`.
