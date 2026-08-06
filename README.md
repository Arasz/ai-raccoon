# AiRaccoon

[![publish](https://github.com/Arasz/ai-raccoon/actions/workflows/publish.yml/badge.svg)](https://github.com/Arasz/ai-raccoon/actions/workflows/publish.yml)

An MCP server that gives AI agents persistent, project-scoped memory. It runs
local-first: one SQLite bank per install scope, with hybrid FTS5+vec0 search,
workspace sandboxes, a curated shared tier, memory degradation, and optional
cloud sync to S3 or Azure Blob. Built on the
[ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) C# SDK
2.1.0 (net10.0).

## Quick start

Install the tool (package id `arasz.ai-raccoon`, command `ai-raccoon`):

```bash
dotnet tool install -g arasz.ai-raccoon
```

Run the server:

```bash
ai-raccoon                    # stdio (default)
ai-raccoon --transport http   # Streamable HTTP at /mcp
```

Connect an MCP client with a zero-config `.mcp.json`:

```json
{
  "mcpServers": {
    "ai-raccoon": { "command": "ai-raccoon" }
  }
}
```

That is the whole setup. The server keeps one bank per install scope:
`~/.ai-raccoon` for a user-scope install, `<project>/.ai-raccoon` for a
project-scope one. Projects partition the bank via context (`project:<id>`).

## What an agent gets

| Feature | What it does |
|---|---|
| One bank per install scope | user scope keeps `~/.ai-raccoon`, project scope keeps `<project>/.ai-raccoon`; projects partition via `project:<id>` context |
| Hybrid search | `memory_search` fuses FTS5 keyword and vec0 semantic ranking with reciprocal rank fusion (RRF), scoped by `scope=all\|project\|shared` and optional workspace |
| Workspace sandboxes | `memory_workspace_begin` mints an isolated context; entries stay in the outbox until consolidated |
| Shared promotion tier | `memory_share` promotes a hash into the flat `shared` context, cross-project and exempt from degradation sweeps |
| Rating and degradation | search hits raise an entry's retrieval rating; sweeps remove old, low-rated project entries (`shared` is protected) |
| Cloud sync (optional) | `memory_sync` pushes/pulls VACUUM snapshots to S3 or Azure Blob with If-Match conflict detection |
| Access modes | `ro` (read-only), `rw` (read-write, default), `full` (adds destructive operations); per-project settings override the global default |
| Encryption at rest (optional) | set `AIRACCOON_DB_PASSPHRASE` for AES-256-CBC page-level encryption via e_sqlite3mc; FTS5 and vec0 work unchanged |

The full contract (19 tools: 16 memory + 3 file-watcher, 2 prompts, parameters,
error shapes) is in [docs/reference/agent-memory-server.md](docs/reference/agent-memory-server.md).

## Configuration

One environment variable:

| Variable | Purpose |
|---|---|
| `AIRACCOON_DB_PASSPHRASE` | SQLite encryption passphrase (optional; unset = plaintext) |

Everything else lives in the settings table of the install's `memory.db` and is
changed with `ai-raccoon` verb commands. The CLI is the single config channel.
Secrets (OpenAI key, S3 access/secret keys, Azure connection string) are stored
there, encrypted at rest when a passphrase is set, never in the environment and
never in tracked files.

Launch flags (startup-scoped only):

| Option | Values | Default |
|---|---|---|
| `--transport` | `stdio`, `http`, `https` (https → warning) | `stdio` |
| `--data-root <path>` | any (`~` expanded) | `~/.ai-raccoon` |
| `--install-scope` | `user`, `project` | `user` |
| `--port <n>` | any port; `0` = random free port | `7721` |

Diagnostics go to stderr; stdout carries only MCP protocol frames.

## Embeddings

Two engines, set per bank with `ai-raccoon model`:

| Engine | Setup | Notes |
|---|---|---|
| Local (ONNX, in-process) | bundled `all-MiniLM-L6-v2` (int8, ~23 MB) | offline, ~9 ms/query, no API cost |
| Remote (OpenAI-compatible) | `ai-raccoon model set openai {model-id} [base-url] --api-key <key>` | any `/embeddings` backend: OpenAI, LM Studio, Ollama |

Changing the engine re-embeds the bank. Measured trade-off: the bundled model
finds the right memory first about as often as served models (MRR 0.836 vs
0.854–0.858) at 4–10× lower latency per query. Full numbers and the runnable
harness: [docs/reference/embedding-benchmark.md](docs/reference/embedding-benchmark.md).

## Observability

Every tool call records OpenTelemetry-compatible metrics and traces through the
`AiRaccoon.MemoryTools` meter. Watch them live without touching the server:

```bash
dotnet-counters monitor -p <server-pid> --counters AiRaccoon.MemoryTools
dotnet-trace collect -p <server-pid> --providers AiRaccoon.MemoryTools
```

## Architecture

```
src/AiRaccoon/                 # MCP server: thin tools, 1:1 to the API
src/AiRaccoon.Core/            # pure domain: memory, rating, degradation, workspace
src/AiRaccoon.Infrastructure/  # SQLite adapter, embeddings, sync
tests/AiRaccoon.Tests/         # xunit.v3 + Shouldly
docs/                          # documentation tree (see docs/README.md)
```

The store is a native .NET SQLite layer: one `memory.db` with all tables, FTS5
and vec0 virtual tables, and triggers created on first open. No native extension
provisioning, no download-on-first-run. Deep dive:
[docs/explanation/architecture.md](docs/explanation/architecture.md).

## Documentation

The docs tree ([docs/README.md](docs/README.md)) is the canonical reference:

- [docs/reference/agent-memory-server.md](docs/reference/agent-memory-server.md) — tool contract, CLI verbs, error shapes
- [docs/explanation/architecture.md](docs/explanation/architecture.md) — system architecture
- [docs/reference/embedding-benchmark.md](docs/reference/embedding-benchmark.md) — embedding model benchmark
- [docs/adr/README.md](docs/adr/README.md) — architecture decision records

## Development

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
dotnet test
```

The suite (xunit.v3 + Shouldly) covers the domain, the store, the tools, the
prompts, and an E2E layer: 1100+ tests.

## Contributing

Read [CLAUDE.md](CLAUDE.md) first. It is the source of truth for this repo's rules:

- **TDD is mandatory**: a failing, behavior-focused test precedes any production change.
- **One task per PR**: every unit of work ends in a pull request; never push directly to `main`.
- Keep the [non-negotiable invariants](CLAUDE.md) (clean layering, minimal comments, guarded nulls, no hardcoded secrets).

## Security

Report security problems privately, not as public issues. See
[SECURITY.md](SECURITY.md) for the reporting channel, supported-versions policy,
and threat model.

## License

MIT. See [LICENSE](LICENSE). Copyright (c) 2026 Rafał Araszkiewicz.
