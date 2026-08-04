# AiRaccoon — Agent Memory MCP Server

An MCP server that gives AI agents persistent, project-scoped memory backed by
a native .NET SQLite store. Local-first by default:
one `memory.db` per install scope, a bundled in-process ONNX embedding model,
hybrid FTS5+vec0 semantic search with reciprocal rank fusion, workspace sandboxes,
a curated shared tier, memory degradation, three-tier access control, and opt-in
S3-compatible cloud sync.

Built on the ModelContextProtocol C# SDK 2.0.0 (net10.0).

## What an agent gets

- **One memory bank per install scope.** A user-scope install (global tool) keeps a single
  bank under `~/.ai-raccoon` shared by every project; a project-scope install keeps its own
  bank under `<project>/.ai-raccoon`. Projects partition the bank via context (`project:<id>`).
- **Workspace sandboxes.** `memory_workspace_begin` mints a `workspace_id` whose context is
  isolated by design — entries written with it have an FK to the workspace and an XOR CHECK
  that keeps them out of committed project memory until consolidated.
- **Shared promotion tier.** Plain writes land in the project. `memory_share`
  promotes a hash into the flat `shared` context — cross-project, curated, and exempt
  from degradation sweeps.
- **Hybrid search.** `memory_search` combines FTS5 keyword and vec0 vector retrieval,
  fused with Reciprocal Rank Fusion (default k=60, 1:1 weights). Tunable via `rrfK`,
  `ftsWeight`, and `vectorWeight`. Scoped by `scope=all|project|shared` and optional
  workspace. Degrades to FTS5-only when no embedding engine is configured.
- **Rating and degradation.** Search hits raise an entry's retrieval rating; sweeps
  remove old, low-rated project entries (`shared` is protected).
- **Access modes.** Three tiers enforced at the tool boundary: `ro` (read-only),
  `rw` (default: read + write), `full` (adds deletion, sweep execution, and
  workspace consolidation). Set globally with `AIRACCOON_ACCESS_MODE` or per-project
  in the settings table.
- **Cloud sync (optional).** `memory_sync` pushes/pulls the bank's committed contexts
  (`shared` + `project:<id>`) as a single snapshot to S3-compatible object storage
  (R2, S3, MinIO) using VACUUM INTO + If-Match CAS + row merge.

## Tools (17) and prompts (2)

`memory_write`, `memory_search`, `memory_list`, `memory_stats`, `memory_share`,
`memory_delete`, `memory_delete_context`, `memory_ingest_file`, `memory_ingest_directory`,
`memory_configure`, `memory_embed_pending`, `memory_workspace_begin`,
`memory_workspace_status`, `memory_workspace_consolidate`, `memory_workspace_discard`,
`memory_sweep`, `memory_sync` — plus the `memory-usage-guide` and
`workspace-consolidation-guide` prompts. Every tool requires a `project_id`.

All 17 tool names are unchanged. `memory_configure` gained a `baseUrl` parameter for
any OpenAI-compatible endpoint.

## Environment variables

| Variable                         | Purpose                                           |
|----------------------------------|---------------------------------------------------|
| `AIRACCOON_DATA_ROOT`            | Bank data root (default `~/.ai-raccoon`)          |
| `AIRACCOON_INSTALL_SCOPE`        | `user` (default) or `project`                     |
| `AIRACCOON_ACCESS_MODE`          | Global access mode seed: `ro`, `rw` (default), or `full` |
| `AIRACCOON_OPENAI_API_KEY`       | API key for `provider=openai` embeddings          |
| `AIRACCOON_EMBEDDING_MODEL`      | Custom ONNX model path overriding the bundled all-MiniLM-L6-v2 |
| `AIRACCOON_SYNC_ENDPOINT`        | S3-compatible endpoint URL (sync)                 |
| `AIRACCOON_SYNC_BUCKET`          | S3 bucket name (sync)                             |
| `AIRACCOON_SYNC_ACCESS_KEY`      | S3 access key (sync)                              |
| `AIRACCOON_SYNC_SECRET_KEY`      | S3 secret key (sync)                              |
| `AIRACCOON_SYNC_REGION`          | S3 region (optional)                              |
| `AIRACCOON_SYNC_OBJECT_KEY`      | Custom S3 object key (default `memory-<projectId>.db`) |

Credentials are read from the environment only — never from tracked files.

## Command-line options

The server parses its own arguments (System.CommandLine 2.0.10) before the host
builds. Precedence: **CLI args > environment variables > built-in defaults** —
every option below mirrors an environment variable, so env-only setups keep
working unchanged.

| Option | Values | Default | Maps to |
|---|---|---|---|
| `--transport` | `stdio`, `http`, `https` (https → warning) | `stdio` | `MCP_TRANSPORT` |
| `--data-root <path>` | any (`~` expanded) | `~/.ai-raccoon` | `AIRACCOON_DATA_ROOT` |
| `--install-scope` | `user`, `project` | `user` | `AIRACCOON_INSTALL_SCOPE` |
| `--access-mode` | `ro`, `rw`, `full` | unset (`rw` effective) | `AIRACCOON_ACCESS_MODE` |
| `--embedding-model <path>` | any (`~` expanded) | bundled model | `AIRACCOON_EMBEDDING_MODEL` |
| `--sync-endpoint <url>` | any | unset (sync off) | `AIRACCOON_SYNC_ENDPOINT` |
| `--sync-bucket <name>` | any | unset | `AIRACCOON_SYNC_BUCKET` |
| `--sync-region <name>` | any | unset | `AIRACCOON_SYNC_REGION` |
| `--sync-object-key <key>` | any | `memory-<projectId>.db` | `AIRACCOON_SYNC_OBJECT_KEY` |

Secrets are environment-only, never CLI options: `AIRACCOON_OPENAI_API_KEY`,
`AIRACCOON_SYNC_ACCESS_KEY`, `AIRACCOON_SYNC_SECRET_KEY`, `AIRACCOON_DB_PASSPHRASE` —
the parser's unknown-option error is the defense (`--sync-access-key x` fails).
`--help`/`--version` and parse errors print to **stderr** (exit 0 / exit 1);
stdout carries only MCP protocol frames. Generic host flags (`--environment`,
`--contentRoot`, `--applicationName`) are accepted hidden and ignored.

Zero-config `.mcp.json` entry (defaults: stdio, `~/.ai-raccoon`, user scope, rw):

```json
{
  "mcpServers": {
    "ai-raccoon": { "command": "ai-raccoon" }
  }
}
```

Explicit equivalent (identical behavior, spelled out):

```json
{
  "mcpServers": {
    "ai-raccoon": {
      "command": "ai-raccoon",
      "args": [
        "--transport", "stdio",
        "--data-root", "~/.ai-raccoon",
        "--install-scope", "user",
        "--access-mode", "rw"
      ]
    }
  }
}
```

Secrets go in the client's user-scoped config (e.g. Claude Code `~/.claude.json`
`env`), never in a shared/tracked `.mcp.json`:

```json
{
  "mcpServers": {
    "ai-raccoon": {
      "command": "ai-raccoon",
      "env": {
        "AIRACCOON_OPENAI_API_KEY": "sk-...",
        "AIRACCOON_DB_PASSPHRASE": "change-me"
      }
    }
  }
}
```

Registry installs (`.mcp/server.json`) pass no args — `packageArguments` stays
empty; `environmentVariables` is the secret channel.

## Transports

- `stdio` (default) — MCP clients launch the server as a subprocess.
- `http` — Streamable HTTP at `/mcp`, selected via `MCP_TRANSPORT=http` or
  `--transport http` (stateless per the 2026-07-28 spec revision).

All diagnostics go to stderr; stdout carries only MCP protocol messages.

## Architecture

The server is a native .NET store with no sqlite-memory, sqlite-vector, or
sqlite-sync extensions — no download-on-first-run provisioning, no `raccoon_meta.db`.
Everything lives in one `memory.db`: entries, workspaces, settings, FTS5, vec0,
sync_meta, and sync_tombstones.

- **vec0** ships via the `HiraokaHyperTools.sqlite-vec` NuGet package — always
  available, loaded with `connection.LoadVector()` on bank open.
- **Layering**: `AiRaccoon.Core` (domain, pure), `AiRaccoon.Infrastructure`
  (SQLite, embedding, sync), `AiRaccoon` (MCP tools, thin adapters).

## Embeddings

The default embedding engine is the bundled int8 all-MiniLM-L6-v2 ONNX model
(~23 MB, Apache-2.0, 384 dimensions, SHA-256 pinned) that ships inside the tool
package under `Models/` — `memory_configure(provider="local")` embeds in-process
with ONNX Runtime, no sidecar or download.

`memory_configure(provider="openai")` routes through any OpenAI-compatible `baseUrl`
(default `https://api.openai.com/v1`) with a model id; it needs an API key (`apiKey`
arg or `AIRACCOON_OPENAI_API_KEY`). API keys are never persisted.

Without a configured engine, writes are stored deferred (`embed_state=pending`) and
indexed later via `memory_embed_pending`. Changing the engine re-embeds the bank.

The `model` parameter is optional for local (defaults to the bundled model) and
required for openai. The `AIRACCOON_EMBEDDING_MODEL` env var overrides the bundled
model path with a custom ONNX model.

## Packaging note

One dotnet tool package bundles the ONNX model. A no-embed flavor
(`ai-raccoon.NoEmbed`) is deferred to when a size-sensitive deployment needs it (D5).

## Develop

- `dotnet build` / `dotnet test` from the repo root.
- The MCP server is packaged as the `ai-raccoon` dotnet tool (multi-RID: win-x64/arm64,
  osx-arm64, linux-x64/arm64/musl-x64).
