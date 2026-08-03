# AiRaccoon

An MCP server that gives AI agents persistent, project-scoped memory backed by
[sqlite-memory](https://github.com/sqliteai/sqlite-memory): local-first by default,
one SQLite memory bank per install scope, hybrid semantic search, workspace
sandboxes, a curated shared tier, memory degradation, and opt-in cloud sync
through SQLite Cloud. Built on the [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol)
C# SDK 2.0.0 (net10.0).

> Domain: provides AI agents with persistent, project-scoped memory over MCP,
> backed by sqlite-memory.
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
- **Hybrid search.** `memory_search` combines vector similarity and FTS5, scoped by
  `scope=all|project|shared` and optional workspace.
- **Rating and degradation.** Search hits raise an entry's retrieval rating; sweeps
  remove old, low-rated project entries (`shared` is protected).
- **Cloud sync (optional).** `memory_sync` pushes/pulls the bank's committed contexts
  (`shared` + `project:<id>`) into a configured SQLite Cloud database, which is the
  correlation point between a user-scope install and any project-scope install.

The full tool contract (17 tools, 2 prompts, environment variables, error
shapes) is in [`docs/reference/agent-memory-server.md`](docs/reference/agent-memory-server.md).

## Transports

- **stdio** (default) — what MCP clients expect when launching a server as a subprocess.
- **Streamable HTTP** — opt-in via `MCP_TRANSPORT=http`; serves the protocol at `/mcp`
  (launch profile `http`, `http://localhost:8080`).

Transport selection lives in one place: `McpTransportSelector` keys off the
`MCP_TRANSPORT` environment variable — anything other than `http` (case-insensitive)
runs stdio. All diagnostics go to stderr; stdout carries only MCP protocol messages.

## Environment variables

| Variable | Purpose |
|---|---|
| `AIRACCOON_DATA_ROOT` | Bank data root (default `~/.ai-raccoon`) |
| `AIRACCOON_INSTALL_SCOPE` | `user` (default) or `project` |
| `AIRACCOON_SQLITECLOUD_DB_ID` | SQLite Cloud managed database id (sync) |
| `AIRACCOON_SQLITECLOUD_API_KEY` | SQLite Cloud API key (sync) |
| `AIRACCOON_VECTORSSPACE_API_KEY` | vectors.space API key (remote embeddings) |

Credentials are read from the environment only — never from tracked files.

## Embeddings

Embeddings are configured per bank via `memory_configure`; two engines:

| Engine | `provider` | `model` | Setup |
|---|---|---|---|
| Local (llama.cpp, offline) | `local` | GGUF path | `scripts/download-embedding-model.sh all-minilm` (~21 MB, Apache-2.0) |
| Remote (vectors.space) | `openai` | e.g. `text-embedding-3-small` | Free key at [vectors.space](https://vectors.space), set `AIRACCOON_VECTORSSPACE_API_KEY` |

Other OpenAI-compatible endpoints (LM Studio, Ollama) are **not** supported by
the pinned sqlite-memory extension — its remote engine hardcodes the
vectors.space URL. See `docs/reference/agent-memory-server.md` for the full
matrix and the `AIRACCOON_TEST_GGUF` usage in the embedding tests.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build & test

```bash
dotnet build
dotnet test
```

The test project (`tests/AiRaccoon.Tests`, xunit.v3 + Shouldly, Dapper) covers the
domain, the store (unit + real-extension integration), the tools, the prompts, the
traits-filtered E2E suite — 185 cases, 0 skips when `AIRACCOON_TEST_GGUF` points at a
downloaded model. The integration tests exercise the real sqlite-memory 1.3.5 +
sqlite-vector 1.0.0 binaries and skip honestly when the host RID has no provisioned
extensions.

## Embedding benchmark

`benchmarks/AiRaccoon.Benchmarks` compares retrieval quality and latency across
embedding backends (local GGUF via LLamaSharp vs LM Studio via the OpenAI SDK,
both behind the official `Microsoft.Extensions.AI` abstraction):

```bash
AIRACCOON_TEST_GGUF=$HOME/.ai-raccoon/models/all-MiniLM-L6-v2.Q5_K_M.gguf \
LMSTUDIO_BASE_URL=http://localhost:1234 \
LMSTUDIO_MODELS="text-embedding-qwen3-embedding-0.6b,text-embedding-embeddinggemma-300m" \
dotnet run --project benchmarks/AiRaccoon.Benchmarks
```

Latest results (macos-arm64): local all-MiniLM-L6-v2 Q5_K_M reaches R@5 0.81 /
MRR 1.0 at ~9 ms per query in-process; LM Studio's Qwen3-0.6b and
EmbeddingGemma-300m both reach R@10 1.0 at 37–90 ms over the network. Full
methodology and numbers in `benchmarks/README.md`.

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

(`MCP_TRANSPORT=http` selects the HTTP transport too, but only with
`--no-launch-profile` — a launch profile overrides the environment variable, and the
default `stdio` profile would silently switch you back. Without a profile the HTTP
endpoint lands on ASP.NET's default port, not 8080.)

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
    McpTransportSelector.cs
    Tools/MemoryTools.cs     # 17 [McpServerTool] tools, 1:1 to the port
    Prompts/MemoryPrompts.cs # 2 agent usage guides
  src/AiRaccoon.Core/        # pure domain (no infra deps)
    Memory/                  # records, SearchScope, IMemoryStore port
    Rating/                  # RatingPolicy, IMemoryExtension + MemoryExtensionHost
    Degradation/             # DegradationPolicy, SweepCandidate
  src/AiRaccoon.Infrastructure/  # SQLite adapter, provisioning, sync
    Sqlite/                  # SqliteMemoryStore (Dapper), MetaStore, factory
    Workspace/               # WorkspaceService
    Degradation/             # SweepService
    Provisioning/            # ExtensionProvisioner (per-RID, SHA-256 verified)
    Sync/                    # SyncService (sqlite-sync)
  tests/AiRaccoon.Tests/     # xunit.v3 + Shouldly
  Directory.Build.props      # analyzers, warnings-as-errors
  Directory.Packages.props   # central package versions
  docs/                      # canonical documentation tree (see docs/README.md)
```

The server keeps the [MCP layer thin](CLAUDE.md): `Tools/` maps parameters and formats
results, with no business logic of its own. The domain layer is pure; the SQLite adapter
lives in Infrastructure. Warnings are errors (`TreatWarningsAsErrors`), analyzers are on,
and package versions are managed centrally.

Native extensions (sqlite-memory, sqlite-vector, sqlite-sync) are provisioned per RID on
first run into `<data-root>/extensions/<rid>/`, pinned and SHA-256 verified. Local
embeddings need a GGUF model configured via `memory_configure`; without a model, writes are
stored deferred and indexed later (`memory_embed_pending`). Download the small verified
embedding model (~21 MB, Apache-2.0) with `scripts/download-embedding-model.sh all-minilm`
(see `docs/reference/agent-memory-server.md` for the `nomic` alternative and
`AIRACCOON_TEST_GGUF` usage in the embedding tests).

## Packaging & release

The server packs as a .NET tool (`PackAsTool`, package id `ai-raccoon`, type `McpServer`):

```bash
dotnet pack -c Release
```

To deploy to the local NuGet feed (`.nupkg-local/`), set `dotnet_env=local` for the
directory — the `DeployToLocalSource` build target pushes the freshly built package. The
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
