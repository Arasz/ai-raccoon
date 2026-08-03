# AiRaccoon — Agent Memory MCP Server

An MCP server that gives AI agents persistent, project-scoped memory backed by
[sqlite-memory](https://github.com/sqliteai/sqlite-memory). Local-first by default:
one SQLite memory bank per install scope, local GGUF embeddings, hybrid semantic search, workspace sandboxes, a curated
shared tier, memory degradation, and opt-in cloud sync through SQLite Cloud.

Built on the ModelContextProtocol C# SDK 2.0.0 (net10.0).

## What an agent gets

- **One memory bank per install scope.** A user-scope install (global tool) keeps a single bank under `~/.ai-raccoon`
  shared by every project; a project-scope install keeps its own bank under `<project>/.ai-raccoon`. Projects partition
  the bank via context (`project:<id>`).
- **Workspace sandboxes.** `memory_workspace_begin` mints a `workspace_id` whose context is isolated by design — notes
  written with it stay in the outbox, never in committed project memory, until consolidated.
- **Shared promotion tier.** Plain writes land in the project. `memory_share`
  promotes a hash into the flat `shared` context — cross-project, curated, and exempt from degradation sweeps.
- **Hybrid search.** `memory_search` combines vector similarity and FTS5, scoped by
  `scope=all|project|shared` and optional workspace.
- **Rating and degradation.** Search hits raise an entry's retrieval rating; sweeps remove old, low-rated project
  entries (`shared` is protected).
- **Cloud sync (optional).** `memory_sync` pushes/pulls the bank's committed contexts (`shared` + `project:<id>`) into a
  configured SQLite Cloud database, which is the correlation point between a user-scope install and any project-scope
  install.

## Tools (17) and prompts (2)

`memory_write`, `memory_search`, `memory_list`, `memory_stats`, `memory_share`,
`memory_delete`, `memory_delete_context`, `memory_ingest_file`, `memory_ingest_directory`,
`memory_configure`, `memory_embed_pending`, `memory_workspace_begin`,
`memory_workspace_status`, `memory_workspace_consolidate`, `memory_workspace_discard`,
`memory_sweep`, `memory_sync` — plus the `memory-usage-guide` and
`workspace-consolidation-guide` prompts. Every tool requires a `project_id`.

## Environment variables

| Variable                         | Purpose                                   |
|----------------------------------|-------------------------------------------|
| `AIRACCOON_DATA_ROOT`            | Bank data root (default `~/.ai-raccoon`)  |
| `AIRACCOON_INSTALL_SCOPE`        | `user` (default) or `project`             |
| `AIRACCOON_ACCESS_MODE`          | Default global access mode (`ro`\|`rw`\|`full`) |
| `AIRACCOON_OPENAI_API_KEY`       | API key for `provider=openai` embeddings  |
| `AIRACCOON_EMBEDDING_MODEL`      | Custom ONNX model path for `provider=local` |
| `AIRACCOON_SQLITECLOUD_DB_ID`    | SQLite Cloud managed database id (sync)   |
| `AIRACCOON_SQLITECLOUD_API_KEY`  | SQLite Cloud API key (sync)               |

Credentials are read from the environment only — never from tracked files.

## Transports

- `stdio` (default) — MCP clients launch the server as a subprocess.
- `http` — Streamable HTTP at `/mcp`, selected via `MCP_TRANSPORT=http`
  (stateless per the 2026-07-28 spec revision).

All diagnostics go to stderr; stdout carries only MCP protocol messages.

## Native extensions

sqlite-memory, sqlite-vector and sqlite-sync ship as native SQLite extensions, provisioned per platform on first run
into `<data-root>/extensions/<rid>/`:
sqlite-memory 1.3.5, sqlite-vector 1.0.0, sqlite-sync 1.1.2 (pinned, SHA-256 verified).

## Embeddings

The default embedding engine is the small int8 all-MiniLM-L6-v2 ONNX model bundled inside the tool package
(`Models/`, SHA-256 pinned) — `memory_configure(provider="local")` embeds in-process with no sidecar or download.
`memory_configure(provider="openai")` routes through any OpenAI-compatible `baseUrl` (default
`https://api.openai.com/v1`) with a model id; it needs an API key (`api_key` arg or `AIRACCOON_OPENAI_API_KEY`).
Without a configured engine, writes are stored deferred (`embed_state=pending`) and indexed later via
`memory_embed_pending`. Changing the engine re-embeds the bank.

## Develop

- `dotnet build` / `dotnet test` from the repo root.
- The MCP server is packaged as the `ai-raccoon` dotnet tool (multi-RID: win-x64/arm64, osx-arm64,
  linux-x64/arm64/musl-x64).

## Licensing note

sqlite-memory is MIT. sqlite-vector and sqlite-sync are Elastic License 2.0 — free for open-source and non-production
use; contact SQLite Cloud for production/managed use. See the upstream repos for details.
