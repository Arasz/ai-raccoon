# AiRaccoon — Agent Memory MCP Server

An MCP server that gives AI agents persistent, project-scoped memory backed by
a native .NET SQLite store. Local-first by default:
one `memory.db` per install scope, a bundled in-process ONNX embedding model,
hybrid FTS5+vec0 semantic search with reciprocal rank fusion, workspace sandboxes,
a curated shared tier, memory degradation, three-tier access control, and opt-in
cloud sync (S3 or Azure Blob).

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
  workspace consolidation). Set with the `access` CLI commands (settings table).
- **Cloud sync (optional).** `memory_sync` pushes/pulls the bank's committed contexts
  (`shared` + `project:<id>`) as a single snapshot to cloud object storage —
  S3-compatible endpoints (R2, S3, MinIO) or Azure Blob — using VACUUM INTO +
  If-Match CAS + row merge.

## Tools (19) and prompts (2)

`memory_write`, `memory_search`, `memory_list`, `memory_stats`, `memory_share`,
`memory_delete`, `memory_delete_context`, `memory_ingest_file`, `memory_ingest_directory`,
`memory_embed_pending`, `memory_workspace_begin`,
`memory_workspace_status`, `memory_workspace_consolidate`, `memory_workspace_discard`,
`memory_sweep`, `memory_sync` — plus the file-watcher trio `memory_watch_add`,
`memory_watch_status`, `memory_watch_remove` — and the `memory-usage-guide` and
`workspace-consolidation-guide` prompts. Every tool requires a `project_id`.

Configuration is deliberately NOT an MCP tool: the CLI is the single config channel
(see below), so `memory_configure` and `memory_set_structure_alpha` were removed.
Watching pairs the `watch` CLI verbs (enable/scope/concurrency — CLI-only) with the
three watch tools above (registration and status).

## Configuration: the CLI is the single channel

Runtime configuration lives in the settings table of the install's `memory.db` and is
changed only through the `ai-raccoon` verb commands (one-shot processes against the bank;
the running server hot-reloads the rows). Bare `ai-raccoon` (with optional launch flags)
runs the server; a verb runs a config command:

```
ai-raccoon access default set {ro|rw|full}    ai-raccoon access default show
ai-raccoon access set {project-id|*} {ro|rw|full}
ai-raccoon access unset {project-id|*}        ai-raccoon access list
ai-raccoon model set local [path]             ai-raccoon model set openai {model-id} [base-url] [--api-key <key>]
ai-raccoon model reset                        ai-raccoon model show
ai-raccoon retrieval alpha set {0..1}         ai-raccoon retrieval alpha show
ai-raccoon sweep threshold set {0..1}         ai-raccoon sweep show
ai-raccoon sync add s3 {url} --bucket {name} [--region {name}] [--object-key {key}] [--cli]   # key prompts, or --cli = AWS credential chain
ai-raccoon sync add azure {container} [--object-key {key}] [--cli --account {name}]            # connection-string prompt, or --cli = az login
ai-raccoon sync remove                        ai-raccoon sync show
ai-raccoon watch enable|disable {project-id|*} {true|false}
ai-raccoon watch scope add|remove|list {project-id|*} {path}
ai-raccoon watch concurrency {project-id|*} {1..16}
ai-raccoon watch list
```

Secrets (OpenAI API key, S3 access/secret keys or the Azure Blob connection string) are stored in the settings table, which
is encrypted at rest when a passphrase is configured.

### Cloud sync credential modes

`sync add azure <container> --cli --account <name>` stores only the non-secret account
name and uses `DefaultAzureCredential` (az CLI login state, or
`AZURE_TENANT_ID`/`AZURE_CLIENT_ID`/`AZURE_CLIENT_SECRET` env vars for headless);
`sync add s3 <url> --bucket <name> --cli` stores only the `s3Chain` marker and uses the
AWS default credential chain (`aws configure`, or `aws sso login`). Nothing long-lived
is persisted for either `--cli` mode; tokens are short-lived and revocable. Auth
failures report `sync-auth-failed:` with a "run `az login`" / "run `aws configure` |
`aws sso login`" hint.

> `sync add azure` does **not** create the container — create it first (`az storage
> container create --account-name <account> --name <container>`), or the first sync
> fails with `sync-network:`.

Azure least privilege: `az login`, then
`az role assignment create --assignee "you@domain.com" --role "Storage Blob Data
Contributor" --scope "<storage-account-resource-id>"` (find the id with
`az storage account show -g <rg> -n <account> --query id`). AWS least privilege: IAM
policy allowing only `s3:GetObject` + `s3:PutObject` on
`arn:aws:s3:::<bucket>/<object-key-prefix>*` — the sync only GETs and PUTs one object.

## Environment variables

Only one environment variable is read:

| Variable                  | Purpose                                              |
|---------------------------|------------------------------------------------------|
| `AIRACCOON_DB_PASSPHRASE` | SQLCipher passphrase for the bank (unset = plaintext) |

All other configuration (access modes, embedding engine, sync, watch) comes from the
settings table via the CLI commands above.

## Command-line options

The server parses its own arguments (System.CommandLine 2.0.10) before the host
builds. Launch-identity flags (startup-scoped only):

| Option | Values | Default |
|---|---|---|
| `--transport` | `stdio`, `http`, `https` (https → warning) | `stdio` |
| `--data-root <path>` | any (`~` expanded) | `~/.ai-raccoon` |
| `--install-scope` | `user`, `project` | `user` |

Launch flags must precede a config verb: `ai-raccoon --data-root /x access list`.
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
        "--install-scope", "user"
      ]
    }
  }
}
```

Encrypted-bank setups set `AIRACCOON_DB_PASSPHRASE` in the client's user-scoped
config (e.g. Claude Code `~/.claude.json` `env`), never in a shared/tracked `.mcp.json`.

Registry installs (`.mcp/server.json`) pass no args — `packageArguments` stays
empty; `environmentVariables` lists the one surviving variable.

## Transports

- `stdio` (default) — MCP clients launch the server as a subprocess.
- `http` — Streamable HTTP at `/mcp`, selected via `--transport http`
  (stateless per the 2026-07-28 spec revision).

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
package under `Models/` — `ai-raccoon model set local` embeds in-process
with ONNX Runtime, no sidecar or download.

`ai-raccoon model set openai {model-id} [base-url] [--api-key <key>]` routes through
any OpenAI-compatible `baseUrl` (default `https://api.openai.com/v1`); the key is
persisted in the settings table (encrypted at rest).

Without a configured engine, writes are stored deferred (`embed_state=pending`) and
indexed later via `memory_embed_pending`. Changing the engine re-embeds the bank.

The `model` parameter is optional for local (defaults to the bundled model) and
required for openai. `ai-raccoon model reset` returns to FTS5-only search.

## Packaging note

One dotnet tool package bundles the ONNX model. A no-embed flavor
(`ai-raccoon.NoEmbed`) is deferred to when a size-sensitive deployment needs it (D5).

## Develop

- `dotnet build` / `dotnet test` from the repo root.
- The MCP server is packaged as the `ai-raccoon` dotnet tool (multi-RID: win-x64/arm64,
  osx-arm64, linux-x64/arm64/musl-x64).
