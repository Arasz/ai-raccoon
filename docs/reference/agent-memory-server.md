# Agent memory server — reference

The ai-raccoon MCP server's complete agent-facing contract: tools, prompts,
environment variables, contexts, and error shapes. Consult this mid-task when
integrating or debugging; see `docs/features/agent-memory/spec-issue-1.md` for the
design rationale and `docs/explanation/agent-memory-architecture.md` for how the
pieces fit.

## Tools (17)

Every tool requires `project_id`. Writes land in `project:<id>` by default; naming a
`workspace_id` routes them into that workspace's isolated context.

| Tool | Parameters | Returns |
|---|---|---|
| `memory_write` | `projectId`, `content`, `workspaceId?`, `agentId?`, `context?` | `{hash, path, context, createdAt}` |
| `memory_search` | `projectId`, `query`, `scope=all\|project\|shared`, `workspaceId?`, `limit=20`, `minScore=0.7` | `{results:[{hash, seq, ranking, path, snippet}], projectId}` |
| `memory_list` | `projectId` | `{files: <json tree>}` |
| `memory_stats` | `projectId` | `{entries, pending, contexts}` |
| `memory_share` | `projectId`, `hash` | `{shared: true, context: "shared"}` |
| `memory_delete` | `projectId`, `hash` | `{deleted: 0\|1}` |
| `memory_delete_context` | `projectId`, `context` | `{deleted: n}` |
| `memory_ingest_file` | `projectId`, `path`, `context?` | `{indexed}` |
| `memory_ingest_directory` | `projectId`, `path`, `context?` | `{scanned}` |
| `memory_configure` | `projectId`, `provider`, `model`, `apiKey?` | `{provider, model, engine}` |
| `memory_embed_pending` | `projectId`, `limit?` | `{processed, pending}` |
| `memory_workspace_begin` | `projectId`, `agentId?`, `name?` | `{workspaceId, context}` |
| `memory_workspace_status` | `projectId`, `workspaceId` | `{entries, count}` |
| `memory_workspace_consolidate` | `projectId`, `workspaceId`, `keep` | `{promoted, discarded}` |
| `memory_workspace_discard` | `projectId`, `workspaceId` | `{discarded}` |
| `memory_sweep` | `projectId`, `dryRun=true` | `{candidates, deleted}` |
| `memory_sync` | `projectId` | `{sent, received, reindexed}` |

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

## Error shapes

Tool errors are returned as MCP tool errors (`CallToolResult.IsError`):

| Condition | Message prefix |
|---|---|
| Missing/blank `project_id` | `invalid-params: project_id is required` |
| Invalid `scope` | `invalid-params: Invalid scope '<x>'` |
| Remote embedding provider without a key | `embedding-api-key-missing: …` |
| Sync without credentials | `sync-not-configured: set AIRACCOON_SQLITECLOUD_DB_ID …` |

## Native extensions

sqlite-memory 1.3.5, sqlite-vector 1.0.0, sqlite-sync 1.1.2 are pinned and provisioned
per RID into `<data-root>/extensions/<rid>/`, SHA-256 verified. `linux-musl-x64` has no
sqlite-memory release binary — provisioning refuses with a clear error.
