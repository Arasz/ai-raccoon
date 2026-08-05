# AiRaccoon

An MCP server that gives AI agents persistent, project-scoped memory backed by a
managed .NET SQLite store: local-first by default, one memory bank per install scope,
hybrid FTS5+vec0 semantic search, workspace sandboxes, a curated shared tier, memory
degradation, and opt-in S3-compatible sync. Built on the
[ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) C# SDK 2.0.0
(net10.0).

> Domain: provides AI agents with persistent, project-scoped memory over MCP,
> backed by a managed SQLite store.
> Stacks: `dotnet`, `mcp`.

## What an agent gets

- **One memory bank per install scope.** A user-scope install (global tool) keeps a
  single bank under `~/.ai-raccoon` shared by every project; a project-scope install
  keeps its own bank under `<project>/.ai-raccoon`. Projects partition the bank via
  context (`project:<id>`).
- **Workspace sandboxes.** `memory_workspace_begin` mints a `workspace_id` whose
  context is isolated by design — notes written with it stay in the outbox, never in
  committed project memory, until consolidated.
- **Shared promotion tier.** Plain writes land in the project. `memory_share`
  promotes a hash into the flat `shared` context — cross-project, curated, and exempt
  from degradation sweeps.
- **Hybrid search.** `memory_search` combines FTS5 keyword ranking and vec0 semantic
  similarity via reciprocal rank fusion (RRF), scoped by `scope=all|project|shared`
  and optional workspace. Configurable weights per modality.
- **Rating and degradation.** Search hits raise an entry's on-row retrieval rating
  (half-life decay with access-count multiplier); sweeps remove old, low-rated
  project entries (`shared` is protected).
- **Cloud sync (optional).** `memory_sync` pushes/pulls VACUUM snapshots to an
  S3-compatible object store with If-Match conflict detection. This is the
  correlation point between a user-scope install and any project-scope install.
- **Access modes.** `ro` (read-only), `rw` (read-write, default), `full` (includes
  destructive operations). Per-project settings override the global default.
- **Encryption at rest (optional).** Set `AIRACCOON_DB_PASSPHRASE` to encrypt the
  SQLite bank with AES-256-CBC via e_sqlite3mc (transparent page-level encryption —
  FTS5 and vec0 work unchanged). Without the passphrase the bank is plaintext.

The full tool contract (19 tools, 2 prompts, environment variables, error
shapes) is in [`docs/reference/agent-memory-server.md`](docs/reference/agent-memory-server.md).

## Transports

- **stdio** (default) — what MCP clients expect when launching a server as a subprocess.
- **Streamable HTTP** — opt-in via `--transport http`; serves the
  protocol at `/mcp` (launch profile `http`, `http://localhost:8080`).

Transport selection lives in one place: `ServerConfig` takes the resolved transport
from the `--transport` launch flag (anything other than `http` runs stdio). All
diagnostics go to stderr; stdout carries only MCP protocol messages.

## Environment variables

Only one environment variable is read:

| Variable | Purpose |
|---|---|
| `AIRACCOON_DB_PASSPHRASE` | SQLite encryption passphrase (AES-256-CBC, optional) |

All other configuration (access modes, embedding engine, retrieval alpha, sweep,
sync, watch) lives in the settings table of the install's `memory.db` and is changed
through the `ai-raccoon` verb commands — the CLI is the single config channel. Secrets
(OpenAI API key, S3 access/secret keys) are stored in the settings table (encrypted at
rest when a passphrase is set), never in the environment and never in tracked files.

## Command-line options

The server parses its own arguments (System.CommandLine 2.0.10) before the host
builds. Launch-identity flags (startup-scoped only):

| Option | Values | Default |
|---|---|---|
| `--transport` | `stdio`, `http`, `https` (https → warning) | `stdio` |
| `--data-root <path>` | any (`~` expanded) | `~/.ai-raccoon` |
| `--install-scope` | `user`, `project` | `user` |

Runtime configuration is not read from environment variables — it lives in the
settings table and is changed with the config verbs (one-shot processes against the
bank; the running server hot-reloads the rows):

```
ai-raccoon access default set {ro|rw|full}      ai-raccoon access default show
ai-raccoon access set {project-id|*} {ro|rw|full}
ai-raccoon access unset {project-id|*}          ai-raccoon access list
ai-raccoon model set local [path]               ai-raccoon model set openai {model-id} [base-url] [--api-key <key>]
ai-raccoon model reset                          ai-raccoon model show
ai-raccoon retrieval alpha set {0..1}           ai-raccoon retrieval alpha show
ai-raccoon sweep threshold set {0..1}           ai-raccoon sweep show
ai-raccoon sync add s3 {url} --bucket {name} [--region {name}] [--object-key {key}]   # S3 credentials are prompted interactively
ai-raccoon sync remove                          ai-raccoon sync show
ai-raccoon watch enable|disable {project-id|*} {true|false}
ai-raccoon watch scope add|remove|list {project-id|*} {path}
ai-raccoon watch concurrency {project-id|*} {1..16}
ai-raccoon watch list
```

Secrets (OpenAI API key via `model set openai --api-key`, S3 access/secret keys via
`sync add s3`) are persisted in the settings table and are never launch flags — an
unknown-option parse error is the defense. `--help`/`--version` and parse errors
print to stderr (exit 0 / exit 1); stdout carries only MCP protocol frames. Generic
host flags (`--environment`, `--contentRoot`, `--applicationName`) are accepted
hidden and ignored.

Zero-config `.mcp.json` entry (defaults: stdio, `~/.ai-raccoon`, user scope, rw):

```json
{
  "mcpServers": {
    "ai-raccoon": { "command": "ai-raccoon" }
  }
}
```

Encrypted-bank setups set `AIRACCOON_DB_PASSPHRASE` in the client's user-scoped
config, never in a shared/tracked file:

```json
{
  "mcpServers": {
    "ai-raccoon": {
      "command": "ai-raccoon",
      "env": {
        "AIRACCOON_DB_PASSPHRASE": "change-me"
      }
    }
  }
}
```

## Embeddings

Embeddings are configured per bank via the `ai-raccoon model` CLI verbs; two engines:

| Engine | `provider` | `model` | Setup |
|---|---|---|---|
| Local (ONNX, in-process) | `local` | Optional ONNX path | Bundled `all-MiniLM-L6-v2` (int8, ~21 MB, Apache-2.0). No network, ~9 ms/query. |
| Remote (OpenAI-compatible) | `openai` | Model id (e.g. `text-embedding-3-small`) | Any OpenAI-compatible `baseUrl`; key via `--api-key` (persisted in the settings table) |

The API key is stored in the settings table (encrypted at rest when a passphrase is
set). Changing the engine (`provider`/`model`/`baseUrl`) re-embeds the entire bank
with the new engine. Other OpenAI-compatible endpoints (LM Studio, Ollama) are
supported — pass their base-url to `ai-raccoon model set openai`.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build & test

```bash
dotnet build
dotnet test
```

The test project (`tests/AiRaccoon.Tests`, xunit.v3 + Shouldly, Dapper) covers the
domain, the store, the tools, the prompts, and the E2E suite — 185+ cases.
Integration tests exercise the real SQLite FTS5 and vec0 tables against in-memory
databases. Tests that need the ONNX embedding model use the bundled int8 model
path; see the test project's README for the full setup.

## Embedding benchmark

**Do you need a bigger embedding model, or is the smallest one good enough?
Measured answer: the smallest one is good enough for most uses.** We benchmarked
three options on 174 real documents (68 judged queries):

| Model | Size | Quality (MRR) | Speed (per query) |
|---|---:|---:|---:|
| all-MiniLM-L6-v2 (local, in-process) | **~21 MB** | 0.836 | **~9 ms** |
| EmbeddingGemma-300m (LM Studio, network) | ~334 MB | 0.858 | ~37 ms |
| Qwen3-Embedding-0.6b (LM Studio, network) | ~639 MB | 0.854 | ~90 ms |

The 21 MB local model finds the right memory first essentially as often as the
served models (MRR 0.836 vs 0.854–0.858), is **4–10× faster per query** (no
network round-trip), costs nothing to run, and works offline. The served models
only pull ahead on one metric — nDCG@10 (0.70 vs 0.61), i.e. how well the whole
top-10 is ordered — which matters only when the *ranking of lower hits*, not the
first hit, decides the outcome.

**Recommendation:** start with the local model (bundled, zero setup). Move to a
served model only if retrieval quality on your own corpus proves insufficient —
you trade 4–10× latency and 15–30× disk for a quality gain visible only in top-10
ordering.

Full numbers, metric definitions (R@5, R@10, MRR, nDCG@10, dim, latency),
methodology and the runnable harness: [`docs/reference/embedding-benchmark.md`](docs/reference/embedding-benchmark.md).

## Quickstart — run it

Run from source with the stdio transport (the default):

```bash
dotnet run --project src/AiRaccoon
```

Or with the HTTP transport, using the `http` launch profile (listens on
`http://localhost:8080`):

```bash
dotnet run --project src/AiRaccoon --launch-profile http
```

(`--transport http` selects the HTTP transport too — via the `http` launch profile,
which passes `--transport http` as `commandLineArgs`, or by appending `-- --transport
http` to `dotnet run`. Without a profile the HTTP endpoint lands on ASP.NET's default
port, not 8080.)

### Connect a client

To use the server from an MCP client (for example VS Code's `.vscode/mcp.json`, or Visual
Studio's `.mcp.json`):

```json
{
  "servers": {
    "AiRaccoon": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "<PATH TO PROJECT DIRECTORY>", "--no-launch-profile"]
    }
  }
}
```

`--no-launch-profile` matters: `dotnet run` otherwise prints its launch-settings notice to
stdout, which corrupts the newline-delimited JSON-RPC stream strict MCP clients expect on
stdio.

## Architecture

```
AiRaccoon/
  src/AiRaccoon/              # the MCP server (thin)
    Program.cs               # transport selection + DI + MCP wiring
    Setup/McpServerSetup.cs  # stdio / HTTP transport
    Tools/MemoryTools.cs     # 16 [McpServerTool] memory tools, 1:1 to the port
    Tools/WatchTools.cs      # 3 [McpServerTool] file-watcher tools
    Prompts/MemoryPrompts.cs # 2 agent usage guides
    Access/                  # MemoryAccessGuard, ForgettingPolicyService
    Setup/Dependencies.cs    # DI registration
  src/AiRaccoon.Core/        # pure domain (no infra deps)
    Memory/                  # records, SearchQuery, IMemoryStore port
    Rating/                  # RatingPolicy, IMemoryExtension + MemoryExtensionHost
    Degradation/             # DegradationPolicy, SweepCandidate
    Chunking/                # IChunker, MarkdownChunker, TokenCount
    Access/                  # AccessMode, AccessModePolicy, AccessRequirement
    Workspace/               # Workspace, WorkspaceStatus, IWorkspaceStore
  src/AiRaccoon.Infrastructure/  # SQLite adapter, embeddings, sync
    Sqlite/                  # SqliteMemoryStore (Dapper), MemorySchema, RRF
    Embedding/               # EmbeddingService (ONNX + remote), OnnxEmbeddingGenerator
    Chunking/                # TokenizerChunker (o200k_base)
    Degradation/             # SweepService
    Workspace/               # WorkspaceService
    Sync/                    # SyncService (S3, VACUUM INTO, ATTACH+merge)
    Rating/                  # RetrievalRatingExtension (no-op, P1 rewire)
  tests/AiRaccoon.Tests/     # xunit.v3 + Shouldly
  Directory.Build.props      # analyzers, warnings-as-errors
  Directory.Packages.props   # central package versions
  docs/                      # canonical documentation tree (see docs/README.md)
```

The server keeps the [MCP layer thin](CLAUDE.md): `Tools/` maps parameters and formats
results, with no business logic of its own. The domain layer is pure; the SQLite adapter
lives in Infrastructure. Warnings are errors (`TreatWarningsAsErrors`), analyzers are on,
and package versions are managed centrally.

For the system architecture — data model, write/search/sync flows, workspace lifecycle,
access modes, and algorithms — see [`docs/explanation/architecture.md`](docs/explanation/architecture.md).

The store is our own managed SQLite layer: `MemorySchema.EnsureAsync` creates the
tables, FTS5 and vec0 virtual tables, and triggers on first open — no native extension
provisioning needed. The bundled ONNX embedding model (`all-MiniLM-L6-v2`, int8
quantized, ~21 MB) runs in-process.

## Packaging & release

The server packs as a .NET tool (`PackAsTool`, package id `ai-raccoon`, type `McpServer`):

```bash
dotnet pack -c Release
```

To deploy to the local NuGet feed (`.nupkg-local/`), set `DOTNET_ENV=local` for the
directory (MSBuild env lookup is case-sensitive on macOS — `dotnet_env` will not
match) — the `DeployToLocalSource` build target pushes the freshly built package. The
package embeds `.mcp/server.json`, so MCP clients can discover inputs.

## Contributing

Read [`CLAUDE.md`](CLAUDE.md) first — it is the source of truth for this repo's rules:

- **TDD is mandatory** — a failing, behavior-focused test precedes any production change.
- **One task per PR** — every unit of work ends in a pull request; never push directly to
  `main`. The one exception is an explicit instruction from the person you work with.
- Keep the [non-negotiable invariants](CLAUDE.md) (clean layering, minimal comments,
  guarded nulls, no hardcoded secrets, …).

Architecture decisions are recorded as ADRs under
[`docs/adr/`](docs/adr/README.md) — none recorded yet.

## Security

Do not open a public issue for a security problem — report it privately; see
[`SECURITY.md`](SECURITY.md) for the reporting channel, supported-versions policy, and the
threat model.

## License

MIT — see [`LICENSE`](LICENSE). Copyright (c) 2026 Rafał Araszkiewicz.
