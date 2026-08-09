# AiRaccoon

[![publish](https://github.com/Arasz/ai-raccoon/actions/workflows/publish.yml/badge.svg)](https://github.com/Arasz/ai-raccoon/actions/workflows/publish.yml)

An MCP server that gives AI agents persistent, project-scoped memory. It runs
local-first: one SQLite bank per install scope, with hybrid FTS5+vec0 search,
workspace sandboxes, a curated shared tier, memory degradation, and optional
cloud sync to S3 or Azure Blob. Built on the
[ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) C# SDK
2.1.0 (net10.0).

## Quick start

Install the tool (package id `ai-raccoon`, command `ai-raccoon`):

```bash
dotnet tool install -g ai-raccoon
```

> **Migrating from `arasz.ai-raccoon`?** The package moved to the raw `ai-raccoon`
> id (NuGet assigned it to this project). The two share the same command shim, so
> install the new id *after* removing the old one — your bank under `~/.ai-raccoon`
> is untouched:
>
> ```bash
> dotnet tool uninstall -g arasz.ai-raccoon
> dotnet tool install -g ai-raccoon
> ```

Run the server:

```bash
ai-raccoon                    # proxy (default): relays to one HTTP backend, autostarting it
ai-raccoon --transport stdio  # complete in-process server, no backend, no autostart
ai-raccoon --transport http   # Streamable HTTP at /mcp
```

The proxy is the zero-config path (`.mcp.json` never changes): the first tool
call from any client probes `http://127.0.0.1:7721/mcp`, spawns `ai-raccoon
serve` if nothing answers, and relays every JSON-RPC message to it. It opens
no bank, holds no encryption key, and loads no embedding model itself — see
[Serve mode](#serve-mode-http) below and
[ADR 0020](docs/adr/0020-always-on-http-stdio-proxy.md). If the backend can
neither be reached nor started, the proxy exits loudly naming the URL and the
`--transport stdio` escape hatch — there is no silent in-process fallback.

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

| Feature                       | What it does                                                                                                                                                                                            |
|-------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| One bank per install scope    | user scope keeps `~/.ai-raccoon`, project scope keeps `<project>/.ai-raccoon`; projects partition via `project:<id>` context                                                                            |
| Hybrid search                 | `memory_search` fuses FTS5 keyword and vec0 semantic ranking with reciprocal rank fusion (RRF), scoped by `scope=all\|project\|shared` and optional workspace                                           |
| Workspace sandboxes           | `memory_workspace_begin` mints an isolated context; entries stay in the outbox until consolidated                                                                                                       |
| Shared promotion tier         | `memory_share` promotes a hash into the flat `shared` context, cross-project and exempt from degradation sweeps                                                                                         |
| Shared extraction             | `memory_share_extract` proposes/promotes shared-worthy candidates per project; the `extract` CLI family runs the same loop as a background service (HTTP/S hosts only, off by default, 30-min interval) |
| Rating and degradation        | search hits raise an entry's retrieval rating; sweeps remove old, low-rated project entries (`shared` is protected)                                                                                     |
| Cloud sync (optional)         | `memory_sync` pushes/pulls VACUUM snapshots to S3 or Azure Blob with If-Match conflict detection                                                                                                        |
| Access modes                  | `ro` (read-only), `rw` (read-write, default), `full` (adds destructive operations); per-project settings override the global default                                                                    |
| Encryption at rest (optional) | set `AIRACCOON_DB_PASSPHRASE` for page-level encryption via SQLite3MC (SQLite3MC.PCLRaw bundle, default cipher chacha20/sqleet); FTS5 and vec0 work unchanged                                           |

The full contract (22 tools, 2 prompts, parameters,
error shapes) is in [docs/reference/agent-memory-server.md](docs/reference/agent-memory-server.md).

## Configuration

| Variable | Purpose |
|---|---|
| `AIRACCOON_DB_PASSPHRASE` | SQLite encryption passphrase (optional; unset = plaintext) |

Plus the `OTEL_*` variables the OpenTelemetry SDK itself reads for OTLP export (serve
mode only, opt-in — see [OTLP export](#otlp-export) below).

Everything else lives in the settings table of the install's `memory.db` and is
changed with `ai-raccoon` verb commands. The CLI is the single config channel.
Secrets (OpenAI key, S3 access/secret keys, Azure connection string) are stored
there, encrypted at rest when a passphrase is set, never in the environment and
never in tracked files.

Launch flags (startup-scoped only):

| Option | Values | Default |
|---|---|---|
| `--transport` | `proxy`, `stdio`, `http`, `https` (https → warning) | `proxy` |
| `--data-root <path>` | any (`~` expanded) | `~/.ai-raccoon` |
| `--install-scope` | `user`, `project` | `user` |
| `--port <n>` | any port; `0` = random free port | `7721` |

Diagnostics go to stderr; stdout carries only MCP protocol frames — true of
the proxy too, which relays JSON-RPC frames without adding output of its own.

### Serve mode (HTTP)

Bare `ai-raccoon` (the default `proxy` transport) starts `ai-raccoon serve`
for you the first time any client touches memory — you normally never run
`serve` by hand. This section covers the manual path: connecting an
HTTP-native client straight to a long-lived server, or attaching to one the
proxy already started.

`ai-raccoon serve` runs the same HTTP endpoint with an idle watchdog — after 4
hours without MCP traffic the server shuts itself down (`--idle-timeout 0`
disables; spans: `90s/30m/4h/1d`). If the port already hosts an ai-raccoon
server, `serve` attaches to it and exits 0 (the first process owns the
watchdog). `/mcp` requires the `X-AiRaccoon-Token` header: before binding,
`serve` mints a random token into `<data-root>/mcp-token` (0600) and every
caller — the proxy included — must present it; `/observability` stays open,
unauthenticated. Background it and point a client at the URL:

```bash
ai-raccoon serve > serve.log 2>&1 &            # POSIX
hermes mcp add ai-raccoon --url http://127.0.0.1:7721/mcp
```

`serve --mcp-entry` prints the client config entry for the bound URL
(`--format hermes|claude|all`; keep stderr out of the entry file:
`ai-raccoon serve --mcp-entry > entry.json 2> serve.log &`) — the printed
entry carries the URL only, not the token, so a client connecting this way
(bypassing the proxy) must add the `X-AiRaccoon-Token` header itself, read
from `<data-root>/mcp-token`. `serve --port 0` picks a random free port and
reports it.

A direct `ai-raccoon --transport http` launch (no `serve` verb) stays
**ungated** — deliberate for now; see [SECURITY.md](SECURITY.md).

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
`AiRaccoon.MemoryTools` meter. The diagnostic tools need the server's process id,
and a backgrounded `serve` does not tell you what it is — so ask it:

```bash
ai-raccoon serve observability counters   # dotnet-counters monitor -p 4711
ai-raccoon serve observability trace      # dotnet-trace collect -p 4711 --providers AiRaccoon.MemoryTools
ai-raccoon serve observability pid        # 4711
ai-raccoon serve observability otlp       # http://127.0.0.1:4317
```

Each prints one line on stdout, so it composes:

```bash
$(ai-raccoon serve observability counters)
dotnet-gcdump collect -p $(ai-raccoon serve observability pid)
```

The PID comes from the running server itself over `GET /observability` on its
loopback port (`--port` to ask a server on a different one), so it is never
stale. With no server listening it exits 4 and says so.

The three views answer different questions:

| Verb | Shows | Notes |
|---|---|---|
| `counters` | GC, CPU, working set, thread pool | `dotnet-counters` with no `--counters` monitors `System.Runtime` alone; append `--counters AiRaccoon.MemoryTools` to swap in the tool metrics, broken out by `project_id` on the invocation counter |
| `trace` | one span per tool call, with `tool`, `project_id`, `result`, `error_type` | |
| `otlp` | everything, to your collector | tool metrics, promotion-queue metrics, runtime metrics and traces |

### OTLP export

Serve mode only, and off unless you ask for it — stdio servers recycle every few
minutes, too short-lived for a batch exporter to earn its keep. Set the standard
variable before starting the server:

```bash
OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4317 ai-raccoon serve > serve.log 2>&1 &
```

Unset, no exporter is built at all — no threads, no sockets, no cost. A
configured-but-unreachable collector is silent by design (OpenTelemetry routes
its own errors to an `EventSource` that nothing listens to unless you drop an
`OTEL_DIAGNOSTICS.json` beside the binary), which keeps `serve.log` clean but
means a broken endpoint fails quietly — `serve observability otlp` reports what
the server is actually exporting to.

Spans and the tool-invocation and promotion-queue counters carry `project_id` in
plaintext. No memory content, queries or embeddings
ever leave the process — see [SECURITY.md](SECURITY.md#what-leaves-the-process-when-otlp-export-is-on)
and [ADR 0009](docs/adr/0009-otlp-export.md).

## Architecture

```text
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

- [docs/reference/agent-memory-server.md](docs/reference/agent-memory-server.md): tool contract, CLI verbs, error shapes
- [docs/explanation/architecture.md](docs/explanation/architecture.md): system architecture
- [docs/reference/embedding-benchmark.md](docs/reference/embedding-benchmark.md): embedding model benchmark
- [docs/adr/README.md](docs/adr/README.md): architecture decision records

## Development

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build
dotnet test
```

The suite (xunit.v3 + Shouldly) covers the domain, the store, the tools, the
prompts, and an E2E layer: 1100+ tests.

To pack the tool and deploy to the local NuGet feed (`.nupkg-local/`), set
`DOTNET_ENV=local` for the directory (MSBuild env lookup is case-sensitive on
macOS, so `dotnet_env` will not match) and run `dotnet pack -c Release`. The
package embeds `.mcp/server.json`, so MCP clients can discover its inputs.

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
