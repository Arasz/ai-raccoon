# Agent memory server — reference

The ai-raccoon MCP server's complete agent-facing contract: tools, prompts,
environment variables, contexts, and error shapes. Consult this mid-task when
integrating or debugging; see `docs/features/agent-memory/spec-issue-1.md` for the
design rationale and `docs/explanation/agent-memory-architecture.md` for how the
pieces fit.

## Tools (17)

Every tool requires `projectId` (camelCase — all parameters are camelCase). Writes
land in `project:<id>` by default; naming a `workspaceId` routes them into that
workspace's isolated context.

| Tool | Parameters | Returns |
|---|---|---|
| `memory_write` | `projectId`, `content`, `workspaceId?`, `agentId?`, `context?` | `{hash, path, context, createdAt}` |
| `memory_search` | `projectId`, `query`, `scope=all\|project\|shared`, `workspaceId?`, `limit=20`, `minScore=0.7` | `{results:[{hash, seq, ranking, path, snippet}], projectId}` |
| `memory_list` | `projectId` | `{files: <json tree>}` |
| `memory_stats` | `projectId` | `{entries, pending, contexts}` |
| `memory_share` | `projectId`, `hash` | `{shared: true, context: "shared"}` |
| `memory_delete` | `projectId`, `hash` | `{deleted: 0\|1}` |
| `memory_delete_context` | `projectId`, `context` | `{deleted: n}` |
| `memory_ingest_file` | `projectId`, `path`, `context?` | `{indexed: 0\|1}` |
| `memory_ingest_directory` | `projectId`, `path`, `context?` | `{scanned: n}` |
| `memory_configure` | `projectId`, `provider`, `model`, `apiKey?` | `{provider, model, engine}` |
| `memory_embed_pending` | `projectId`, `limit?` | `{processed, pending}` |
| `memory_workspace_begin` | `projectId`, `agentId?`, `name?` | `{workspaceId, context}` |
| `memory_workspace_status` | `projectId`, `workspaceId` | `{entries, count}` |
| `memory_workspace_consolidate` | `projectId`, `workspaceId`, `keep` | `{promoted, discarded}` |
| `memory_workspace_discard` | `projectId`, `workspaceId` | `{discarded}` |
| `memory_sweep` | `projectId`, `dryRun=true` | `{candidates, deleted}` |
| `memory_sync` | `projectId` | `{sent, received, reindexed}` |

### Notes on the less obvious tools

- **`scope` values:** `scope=all` (default) searches `shared` + `project:<id>` (+ workspace
  when named); `scope=project` searches `project:<id>` only; `scope=shared` searches the
  `shared` promotion tier only. Workspace scratch is never included in `scope=all` — it is
  only visible to a search that names that `workspaceId`.
- **`memory_share`:** promotes the entry whose `hash` you pass (from a `memory_write`
  or `memory_search` result) into `shared`. It is additive — the source project row
  stays. There is no un-share; `memory_delete` on the shared row's hash removes it from
  `shared`.
- **`memory_workspace_consolidate`:** `keep` is an array of hashes to promote, or
  `["all"]` to promote every entry in the workspace. It then deletes the workspace
  context entirely — entries not kept are gone.
- **Workspace lifecycle record:** `memory_workspace_begin` writes an `Active` row
  (id, project, created_at) into the local meta DB (`raccoon_meta.db`, never synced);
  consolidate and discard mark it `Closed` with `closed_at`. A workspace begun but
  never finished therefore stays traceable after a crash.
- **`memory_sweep`:** `dryRun=true` (default) only lists candidates; pass `dryRun=false`
  to delete. An entry is a candidate when its retrieval rating falls below 0.3 and its
  age exceeds 30 days. `shared` entries are never swept.
- **`memory_configure`:** `engine` is `local` (GGUF model on disk) or `remote`
  (provider API). For a remote provider, `apiKey` or `AIRACCOON_VECTORSSPACE_API_KEY`
  is required — an explicit `apiKey` parameter takes precedence over the environment
  variable. Local embeddings need a GGUF model path; until one is configured, writes
  are stored deferred (`memory_stats.pending > 0`) and only become searchable after
  `memory_embed_pending`.

## Prompts (2)

| Prompt | Purpose |
|---|---|
| `memory-usage-guide` | Protocol: always pass `project_id`; when a workspace is active, write with its `workspace_id`; writes land in the project by default; promote cross-project knowledge via `memory_share`; search `scope=all` sees shared + project. |
| `workspace-consolidation-guide` | Ritual: list the outbox, promote durable facts, drop noise. |

## Contexts

| Context | Meaning | Synced? | Swept? |
|---|---|---|---|
| `shared` | curated cross-project knowledge — only via `memory_share` | yes | exempt |
| `project:<project-id>` | committed, durable project memory | yes | yes |
| `workspace:<workspace-id>` | sandboxed workspace scratch (outbox) | never | no |
| custom | user-defined labels (`docs:api`, …) | yes | project sweep only |

## Environment variables

| Variable | Purpose |
|---|---|
| `AIRACCOON_DATA_ROOT` | Bank data root (default `~/.ai-raccoon`) |
| `AIRACCOON_INSTALL_SCOPE` | `user` (default) or `project` |
| `AIRACCOON_SQLITECLOUD_DB_ID` | SQLite Cloud managed database id (sync) |
| `AIRACCOON_SQLITECLOUD_API_KEY` | SQLite Cloud API key (sync) |
| `AIRACCOON_VECTORSSPACE_API_KEY` | vectors.space API key (remote embeddings) |
| `AIRACCOON_TEST_GGUF` | GGUF model path; gates the embedding integration tests |

Credentials are read from the environment only.

## Local embedding model

Local embeddings run through sqlite-memory's llama.cpp integration and need a
GGUF embedding model on disk, passed to `memory_configure(provider="local")`.
The server does not bundle a model — download one once per install:

```bash
# Smallest verified model (~21 MB, Apache-2.0):
scripts/download-embedding-model.sh all-minilm
# sqlite-memory's documented reference model (~139 MB, Apache-2.0):
scripts/download-embedding-model.sh nomic
```

The script pins the SHA-256 of `all-minilm` (all-MiniLM-L6-v2 Q5_K_M) and
installs it under `<data-root>/models/`. Point the embedding integration/E2E
tests at it with `export AIRACCOON_TEST_GGUF=<data-root>/models/all-MiniLM-L6-v2.Q5_K_M.gguf`
(without it those tests skip honestly).

## Embedding configuration matrix

`memory_configure` accepts any provider string; the pinned sqlite-memory
extension (1.3.5) resolves exactly two engines:

| Engine | `provider` | `model` | Key | Notes |
|---|---|---|---|---|
| Local (llama.cpp) | `local` | GGUF file path | none | Offline, no API cost; model file per the download script |
| Remote (vectors.space) | `openai` | e.g. `text-embedding-3-small` | `AIRACCOON_VECTORSSPACE_API_KEY` | Free tier; endpoint is hardcoded to `https://api.vectors.space/v1/embeddings` |

Other OpenAI-compatible endpoints (LM Studio, Ollama, self-hosted) are **not
configurable**: the extension's remote engine pins the vectors.space URL and
its custom-provider hook is an in-process C callback API, not a setting. To
use such a backend the extension itself would need a base-URL override — out
of scope for the pinned build.

## Error shapes

Tool errors are returned as MCP tool errors (`CallToolResult.IsError`):

| Condition | Message prefix |
|---|---|
| Missing/blank `projectId` | `invalid-params: project_id is required` |
| Invalid `scope` | `invalid-params: Invalid scope '<x>'` |
| Remote embedding provider without a key | `embedding-api-key-missing: set AIRACCOON_VECTORSSPACE_API_KEY or pass api_key for a remote embedding provider` |
| Sync without credentials | `sync-not-configured: set AIRACCOON_SQLITECLOUD_DB_ID and AIRACCOON_SQLITECLOUD_API_KEY` — both are required |

## Native extensions

sqlite-memory 1.3.5, sqlite-vector 1.0.0, sqlite-sync 1.1.2 are pinned and provisioned
per RID into `<data-root>/extensions/<rid>/` (e.g. `~/.ai-raccoon/extensions/osx-arm64/`),
SHA-256 verified. `linux-musl-x64` has no sqlite-memory release binary — provisioning
refuses with a clear `ExtensionProvisioningException` naming what is missing.

## Deletion and sync semantics

- Deletes are permanent — there is no trash or recovery.
- `memory_delete` targets one hash wherever it lives, including a `shared` row;
  `memory_delete_context` deletes every entry under a context label. Nothing forbids
  targeting `shared` — use it deliberately.
- Deleting a synced context (`shared`, `project:<id>`, custom) removes rows locally;
  the next `memory_sync` runs the sqlite-sync push/pull over the committed contexts, so
  the removal is expected to reach the cloud database (sqlite-sync CRDT semantics —
  verify against the pinned 1.1.2 release before relying on it).
- Workspace contexts are never synced, so `memory_workspace_discard` and consolidation's
  discard have no cloud counterpart.

## Known limitations

- There is no tool to list active workspaces: `memory_workspace_status` needs a
  `workspaceId` you must already hold (keep the value returned by `memory_workspace_begin`).
- No un-share tool exists; see `memory_share` notes above.
